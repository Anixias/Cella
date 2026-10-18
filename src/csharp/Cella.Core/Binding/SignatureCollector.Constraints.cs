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
			var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[member];
			RegisterBounds(member.TypeParameters, function.DeclaredTypeParameters, _declarations[function].Context,
				function);
		}
	}
	
	private void RegisterBounds(ImmutableArray<TypeParameterNode> nodes, ImmutableArray<TypeParameterSymbol> parameters,
		ResolutionContext context, Symbol? owner = null)
	{
		var deferring = context with { DeferConstraintCheck = DeferAlways };
		for (var i = 0; i < nodes.Length && i < parameters.Length; i++)
		{
			if (nodes[i].ValueType is { } valueType)
				parameters[i].ValueType = ResolveValueType(valueType, context);
			
			ReportConstraintConflicts(nodes[i]);
			var bounds = nodes[i].Traits.Select(deferring.ResolveTraitReference).ToList();
			ReportRepeatedTraits(nodes[i].Traits, bounds);
			if (owner is not null)
				for (var j = 0; j < bounds.Count; j++)
					if (bounds[j] is { } bound)
						ReportHiddenType(nodes[i].Traits[j], bound, GetOwnerVisibility(owner, context), owner.Name);
			
			_boundLists.Add((nodes[i].Traits, bounds, parameters[i]));
			ImmutableArray<TraitType> traits = [..bounds.OfType<TraitType>().Distinct()];
			ImmutableArray<TypeParameterSymbol> traitParameters = [..bounds.OfType<TypeParameterSymbol>().Distinct()];
			ImmutableArray<FunctionType> functions = [..bounds.OfType<FunctionType>().Distinct()];
			if (!traits.IsEmpty)
				_typePool.SetBounds(parameters[i], traits);
			
			if (functions.Length > 1)
				foreach (var node in nodes[i].Traits.Where((_, j) => bounds[j] is FunctionType))
					Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
						"Cannot require more than one function type"));
			else if (!functions.IsEmpty)
				_typePool.SetFunctionBounds(parameters[i], functions);
			
			if (!traitParameters.IsEmpty)
				_typePool.SetParameterBounds(parameters[i], traitParameters);
			
			if (!nodes[i].Constructors.IsEmpty)
				_pendingConstructorBounds.Add((nodes[i], parameters[i], context, owner));
		}
	}
	
	private TypeSymbol ResolveValueType(ITypeNode node, ResolutionContext context)
	{
		var type = context.ResolveType(node);
		if (type is IntegerType { Kind: not PrimitiveTypeKind.Char } or InvalidType)
			return type;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Value parameters need an integer type"));
		return NativeSymbols.Invalid;
	}
	
	private void RegisterRequiredTraits(TraitSymbol trait, TraitNode node, ResolutionContext context)
	{
		var deferring = context with { DeferConstraintCheck = DeferAlways };
		var required = node.RequiredTraits.Select(deferring.ResolveTrait).ToList();
		ReportRepeatedTraits(node.RequiredTraits, required);
		_boundLists.Add((node.RequiredTraits, [..required], trait.Self));
		for (var i = 0; i < required.Count; i++)
		{
			if (required[i] is { } requiredTrait)
				ReportHiddenType(node.RequiredTraits[i], requiredTrait, trait.Visibility, trait.Name);
		}
		
		_typePool.SetRequiredTraits(trait, [..required.OfType<TraitType>().Distinct()]);
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
		foreach (var (nodes, bounds, self) in _boundLists)
		{
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
						Hints = [$"Required by '{requirer.Name}'"]
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
			if (!_typePool.HasDefaultPart(type))
				Diagnostics.Add(new(DiagnosticSeverity.Hint,
					node.Modifiers.First(static modifier => modifier.Type == TokenType.KeywordReq).SourceLocation,
					"Redundant 'req'"));
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
		foreach (var (node, parameter, context, owner) in _pendingConstructorBounds)
		{
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
		_deferredChecks.Add(check);
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