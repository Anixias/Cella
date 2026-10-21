using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void RegisterConstraints(FileNode node)
	{
		foreach (var declaration in node.Declarations)
		{
			using var frame = Own(declaration);
			var symbol = _symbolTable.DeclarationSymbols[declaration];
			var context = _declarations[symbol].Context;
			switch (declaration)
			{
				case RecordNode record:
					RegisterBounds(record.TypeParameters, ((RecordSymbol)symbol).TypeParameters,
						context with { ContainingType = (RecordSymbol)symbol }, symbol);
					
					RegisterMemberBounds(record.Members);
					RegisterConformances((NamedTypeSymbol)symbol, record.Traits, context);
					break;
				
				case EnumNode enumNode:
					RegisterBounds(enumNode.TypeParameters, ((EnumSymbol)symbol).TypeParameters,
						context with { ContainingType = (EnumSymbol)symbol }, symbol);
					
					RegisterMemberBounds(enumNode.Members);
					RegisterConformances((NamedTypeSymbol)symbol, enumNode.Traits, context);
					break;
				
				case FunctionNode function:
					RegisterBounds(function.TypeParameters, ((FunctionSymbol)symbol).DeclaredTypeParameters, context,
						symbol);
					
					break;
				
				case TraitNode traitNode:
					var trait = (TraitSymbol)symbol;
					var traitContext = context with { ContainingType = trait.Self, Trait = trait };
					_typePool.SetBounds(trait.Self, [_typePool.GetTraitType(trait, [..trait.TypeParameters])]);
					RegisterBounds(traitNode.TypeParameters, trait.TypeParameters, traitContext, trait);
					RegisterRequiredTraits(trait, traitNode, traitContext);
					RegisterMemberBounds(traitNode.Members);
					break;
				
				case ImplNode implNode:
					RegisterImpl((ImplSymbol)symbol, implNode, context);
					break;
			}
		}
	}
	
	private void RegisterMemberBounds(IEnumerable<IDeclarationNode> members)
	{
		foreach (var member in members.OfType<FunctionNode>())
		{
			using var frame = Own(member);
			var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[member];
			RegisterBounds(member.TypeParameters, function.DeclaredTypeParameters, _declarations[function].Context,
				function);
		}
	}
	
	private void RegisterBounds(ImmutableArray<TypeParameterNode> nodes, ImmutableArray<TypeParameterSymbol> parameters,
		ResolutionContext context, Symbol? owner = null)
	{
		for (var i = 0; i < nodes.Length && i < parameters.Length; i++)
		{
			if (nodes[i].ValueType is { } valueType)
				parameters[i].ValueType = ResolveValueType(valueType, context);
			
			ReportConstraintConflicts(nodes[i]);
			var traits = RegisterConstraintList(nodes[i], parameters[i], context, owner);
			if (!traits.IsEmpty)
				_typePool.SetBounds(parameters[i], traits);
		}
	}
	
	private ImmutableArray<TraitType> RegisterConstraintList(TypeParameterNode node, TypeParameterSymbol parameter,
		ResolutionContext context, Symbol? owner)
	{
		var deferring = context with { DeferConstraintCheck = DeferAlways };
		var bounds = node.Traits.Select(deferring.ResolveTraitReference).ToList();
		ReportRepeatedTraits(node.Traits, bounds);
		if (owner is not null)
			for (var j = 0; j < bounds.Count; j++)
				if (bounds[j] is { } bound)
					ReportHiddenType(node.Traits[j], bound, GetOwnerVisibility(owner, context), owner.Name);
		
		_boundLists.Add((node.Traits, bounds, parameter, CurrentOwner));
		_constrainedParameters.Add((node, parameter, bounds, CurrentOwner));
		ImmutableArray<TypeParameterSymbol> traitParameters = [..bounds.OfType<TypeParameterSymbol>().Distinct()];
		ImmutableArray<FunctionType> functions = [..bounds.OfType<FunctionType>().Distinct()];
		if (functions.Length > 1)
			foreach (var functionNode in node.Traits.Where((_, j) => bounds[j] is FunctionType))
				Diagnostics.Add(new(DiagnosticSeverity.Error, functionNode.SourceLocation,
					"Cannot require more than one function type"));
		else if (!functions.IsEmpty)
			_typePool.SetFunctionBounds(parameter, functions);
		
		if (!traitParameters.IsEmpty)
			_typePool.SetParameterBounds(parameter, traitParameters);
		
		if (!node.Constructors.IsEmpty)
			_pendingConstructorBounds.Add((node, parameter, context, owner, CurrentOwner));
		
		return [..bounds.OfType<TraitType>().Distinct()];
	}
	
	private TypeSymbol ResolveValueType(ITypeNode node, ResolutionContext context)
	{
		var type = context.ResolveType(node);
		if (type is IntegerType or InvalidType)
			return type;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Value parameters need an integer type"));
		return NativeSymbols.Invalid;
	}
	
	private void RegisterRequiredTraits(TraitSymbol trait, TraitNode node, ResolutionContext context)
	{
		var requirements = node.Requirements;
		foreach (var keyword in requirements.Keywords.Where(static keyword => keyword.Type == TokenType.KeywordTrait))
			Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation, "Cannot use 'trait' in 'req'"));
		
		ReportKeywordConflicts(requirements.Keywords);
		_typePool.SetRequiredTraits(trait, RegisterConstraintList(requirements, trait.Self, context, trait));
	}
	
	private void ReportRepeatedTraits(ImmutableArray<ITypeNode> nodes, IReadOnlyList<TypeSymbol?> traits)
	{
		for (var i = 0; i < nodes.Length; i++)
		{
			if (traits[i] is { } trait && traits.Where((other, j) => j != i && other == trait).Any())
				Diagnostics.Add(new(DiagnosticSeverity.Error, nodes[i].SourceLocation,
					$"'{trait.Name}' is required more than once"));
		}
	}
	
	private void ReportRedundantBounds()
	{
		foreach (var (nodes, bounds, self, errorOwner) in _boundLists)
		{
			using var frame = Own(errorOwner);
			for (var i = 0; i < nodes.Length; i++)
			{
				if (bounds[i] is not TraitType bound || bounds.Count(other => other == bound) > 1)
					continue;
				
				var requirer = bounds
					.OfType<TraitType>()
					.FirstOrDefault(other => other != bound && Requires(other, bound, self));
				
				if (requirer is not null)
					Diagnostics.Add(new(DiagnosticSeverity.Hint, nodes[i].SourceLocation, $"Redundant '{bound.Name}'")
					{
						Hints = [$"Required by '{requirer.Name}'"],
						Fixes = DiagnosticFix.RemoveRedundantBound(nodes[i].SourceLocation) is { } fix ? [fix] : [],
						Lint = Lints.Redundant
					});
			}
		}
	}
	
	private bool Requires(TraitType trait, TraitType required, TypeSymbol self)
	{
		var visited = new HashSet<TraitType>();
		var pending = new Queue<TraitType>(_typePool.GetRequiredTraits(trait, self));
		while (pending.TryDequeue(out var current))
		{
			if (current == required)
				return true;
			
			if (!visited.Add(current))
				continue;
			
			foreach (var next in _typePool.GetRequiredTraits(current, self))
				pending.Enqueue(next);
		}
		
		return false;
	}
	
	private void ReportRedundantRequirements()
	{
		foreach (var (node, type) in _requiredFields)
		{
			using var frame = Own(node);
			if (!_typePool.HasDefaultPart(type))
				Diagnostics.Add(DiagnosticFix.Redundant(node.Modifiers
					.First(static modifier => modifier.Type == TokenType.KeywordReq).SourceLocation));
		}
	}
	
	private void InheritSelfConstraints()
	{
		foreach (var constrained in _constrainedParameters)
			_typePool.InheritSelfConstraints(constrained.Parameter);
		
		_typePool.ResetFacts();
	}
	
	private void ReportInheritedConflicts()
	{
		foreach (var (node, parameter, bounds, errorOwner) in _constrainedParameters)
		{
			if (parameter.InheritedConstraints.IsEmpty)
				continue;
			
			using var frame = Own(errorOwner);
			var sources = node.Keywords
				.Select(static keyword =>
					(Location: keyword.SourceLocation, Keyword: keyword.Type, Trait: (TraitType?)null))
				.Concat(node.Traits.SelectMany((traitNode, j) => bounds[j] is TraitType trait
					? _typePool.GetSelfConstraints(trait, parameter)
						.Select(keyword => (Location: traitNode.SourceLocation, Keyword: keyword,
							Trait: (TraitType?)trait))
					: []))
				.ToList();
			
			var drops = sources.Where(static source => source.Keyword == TokenType.KeywordDrop).ToList();
			foreach (var copying in _copyingConstraints)
			{
				var conflicting = sources.Where(source => source.Keyword == copying).Concat(drops).ToList();
				if (drops.Count == 0 || conflicting.Count == drops.Count ||
				    conflicting.All(static source => source.Trait is null) ||
				    conflicting.Select(static source => source.Location).Distinct().Count() < 2)
					continue;
				
				var message = $"Cannot combine '{copying.Representation}' and 'drop'";
				foreach (var (location, keyword, trait) in conflicting)
					Diagnostics.Add(new(DiagnosticSeverity.Error, location, message)
					{
						Hints = trait is null ? [] : [$"'{trait.Name}' requires '{keyword.Representation}'"]
					});
			}
		}
	}
	
	private void ReportRedundantKeywords()
	{
		foreach (var (node, parameter, bounds, errorOwner) in _constrainedParameters)
		{
			using var frame = Own(errorOwner);
			foreach (var keyword in node.Keywords)
			{
				if (bounds.OfType<TraitType>().FirstOrDefault(trait =>
					    _typePool.GetSelfConstraints(trait, parameter).Contains(keyword.Type)) is { } requirer)
					Diagnostics.Add(new(DiagnosticSeverity.Hint, keyword.SourceLocation, $"Redundant '{keyword.Text}'")
					{
						Hints = [$"Required by '{requirer.Name}'"],
						Fixes = DiagnosticFix.RemoveRedundantBound(keyword.SourceLocation) is { } fix ? [fix] : [],
						Lint = Lints.Redundant
					});
			}
		}
	}
	
	private void ReportRequirementCycles(IEnumerable<FileNode> files)
	{
		var cyclic = files
			.SelectMany(static file => file.Declarations)
			.Select(declaration => _symbolTable.DeclarationSymbols[declaration])
			.OfType<TraitSymbol>()
			.Where(RequiresItself)
			.ToList();
		
		foreach (var trait in cyclic)
		{
			using var frame = Own(trait);
			Diagnostics.Add(new(DiagnosticSeverity.Error, trait.Node.Identifier.SourceLocation,
				$"'{trait.Name}' requires itself"));
			
			_typePool.SetRequiredTraits(trait, []);
		}
	}
	
	private bool RequiresItself(TraitSymbol trait)
	{
		var visited = new HashSet<TraitSymbol>();
		var pending = new Stack<TraitType>(_typePool.GetRequiredTraits(trait));
		while (pending.TryPop(out var current))
		{
			if (current.Trait == trait)
				return true;
			
			if (!visited.Add(current.Trait))
				continue;
			
			foreach (var required in _typePool.GetRequiredTraits(current.Trait))
				pending.Push(required);
		}
		
		return false;
	}
	
	private void RegisterConstructorBounds()
	{
		foreach (var (node, parameter, context, owner, errorOwner) in _pendingConstructorBounds)
		{
			using var frame = Own(errorOwner);
			var deferring = context with { DeferConstraintCheck = DeferAlways };
			var constructors = node.Constructors
				.Select(constructor => CollectConstructorBound(constructor, parameter, deferring))
				.ToList();
			
			if (owner is not null)
				for (var i = 0; i < constructors.Count; i++)
				{
					var types = node.Constructors[i].Types;
					var parameterTypes = constructors[i].Signature.ParameterTypes;
					for (var j = 0; j < types.Length && j + 1 < parameterTypes.Length; j++)
						ReportHiddenType(types[j], parameterTypes[j + 1], GetOwnerVisibility(owner, context),
							owner.Name);
				}
			
			ReportRepeatedConstructors(node.Constructors, constructors);
			_typePool.SetConstructorBounds(parameter,
				[..constructors.Where(static constructor => constructor.Signature.ParameterTypes.Length > 1)]);
		}
		
		_pendingConstructorBounds.Clear();
	}
	
	private bool DeferAlways(ConstraintCheck check)
	{
		_deferredChecks.Add((check, CurrentOwner));
		return true;
	}
	
	private FunctionInfo CollectConstructorBound(ConstructorConstraintNode node, TypeParameterSymbol parameter,
		ResolutionContext context)
	{
		var modes = node.Modes.Select(SymbolCollector.GetMode).ToArray();
		var types = new List<TypeSymbol> { _typePool.GetPointerType(parameter) };
		var parameters = new List<ParameterSymbol> { new("self", node.SourceLocation) { Mode = ParameterMode.Mut } };
		for (var i = 0; i < node.Types.Length; i++)
		{
			var typeNode = node.Types[i];
			if (typeNode is BorrowTypeNode borrow)
			{
				Diagnostics.Add(DiagnosticReporter.ReportBorrowParameter(borrow, null, node.Modes[i] is not null,
					false));
				
				modes[i] = borrow.IsMutable ? ParameterMode.Mut : ParameterMode.Imm;
				typeNode = borrow.Target;
			}
			else if (typeNode is FunctionTypeNode { IsRef: true } function)
				Diagnostics.Add(DiagnosticReporter.ReportRefFunctionParameter(function, null, node.Modes[i] is not null,
					false));
			
			var type = RejectValueDyn(typeNode, context.ResolveType(typeNode), modes[i]);
			types.Add(_typePool.GetPassedType(type, modes[i]));
			parameters.Add(new($"${i + 1}", typeNode.SourceLocation) { Mode = modes[i], IsNamed = false });
		}
		
		var symbol = new FunctionSymbol($"{parameter.Name}.new", node, Visibility.Public, null, parameters,
			FunctionKind.Constructor)
		{
			TypeParameters =
			[
				parameter,
				..types.SelectMany(TypePool.FindTypeParameters).Where(found => found != parameter).Distinct()
			]
		};
		
		var signature = new FunctionSignature(types, NativeSymbols.Void, false, [ParameterMode.Mut, ..modes]);
		return new FunctionInfo(null, symbol, signature, null, null, context.File);
	}
	
	private void ReportRepeatedConstructors(ImmutableArray<ConstructorConstraintNode> nodes,
		IReadOnlyList<FunctionInfo> constructors)
	{
		for (var i = 0; i < nodes.Length; i++)
		{
			var signature = constructors[i].Signature;
			var same = constructors
				.Where((other, j) => j != i && HasSameParameters(other.Signature, signature))
				.ToList();
			
			if (same.Count == 0)
				continue;
			
			var message = same.Any(other => other.Signature.ParameterModes.SequenceEqual(signature.ParameterModes))
				? $"'{TypePool.DescribeConstructorConstraint(TypePool.DescribeParameters(signature))}' is required more than once"
				: "'new' constraints cannot differ only in parameter modes";
			
			Diagnostics.Add(new(DiagnosticSeverity.Error, nodes[i].SourceLocation, message));
		}
	}
	
	private void ReportConstraintConflicts(TypeParameterNode node)
	{
		var keywords = node.Keywords;
		if (keywords.Any(static keyword => keyword.Type == TokenType.KeywordTrait) &&
		    keywords.Length + node.Traits.Length + node.Constructors.Length > 1)
		{
			foreach (var location in keywords.Select(static k => k.SourceLocation)
				         .Concat(node.Traits.Select(static trait => trait.SourceLocation))
				         .Concat(node.Constructors.Select(static constructor => constructor.SourceLocation)))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location,
					"Cannot combine 'trait' with other constraints"));
			
			return;
		}
		
		ReportKeywordConflicts(keywords);
	}
	
	private static readonly TokenType[] _copyingConstraints =
	[
		TokenType.KeywordCopy, TokenType.KeywordAtomic, TokenType.KeywordExt, TokenType.KeywordNull
	];
	
	private void ReportKeywordConflicts(ImmutableArray<Token> keywords)
	{
		foreach (var repeated in keywords.GroupBy(static keyword => keyword.Type)
			         .Where(static group => group.Count() > 1))
		{
			foreach (var keyword in repeated)
				Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation,
					$"'{keyword.Text}' is required more than once"));
		}
		
		if (keywords.All(static keyword => keyword.Type != TokenType.KeywordDrop))
			return;
		
		foreach (var copying in keywords.Where(static k => k.Type is TokenType.KeywordCopy or TokenType.KeywordAtomic or
			         TokenType.KeywordExt or TokenType.KeywordNull))
		{
			var message = $"Cannot combine '{copying.Text}' and 'drop'";
			foreach (var keyword in keywords.Where(k => k == copying || k.Type == TokenType.KeywordDrop))
				Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation, message));
		}
	}
}