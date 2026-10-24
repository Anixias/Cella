using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void RegisterConformances(NamedTypeSymbol type, ImmutableArray<ITypeNode> traits, ResolutionContext context)
	{
		var deferring = context with { ContainingType = type, DeferConstraintCheck = DeferAlways, TraitSelf = type };
		foreach (var traitNode in traits)
		{
			if (deferring.ResolveTrait(traitNode) is { } trait)
				AddConformance(new(trait.Trait, trait.Arguments, type, type.TypeParameters, null,
					traitNode.SourceLocation));
		}
	}
	
	private void AddConformance(Conformance conformance)
	{
		conformance.Owner = CurrentOwner;
		_localConformances.Add(conformance);
		if (FindForeignError(conformance) is { } error)
			_foreignErrors[conformance] = error;
		else
			_typePool.AddConformance(conformance);
	}
	
	private void AddImpliedConformances()
	{
		var implied = new List<Conformance>();
		foreach (var conformance in _localConformances)
		{
			var self = GetConformanceSelf(conformance);
			if (self is InvalidType)
				continue;
			
			var visited = new HashSet<TraitType>();
			var pending = new Queue<TraitType>(_typePool.GetRequiredTraits(
				_typePool.GetTraitType(conformance.Trait, conformance.Arguments), self));
			
			while (pending.TryDequeue(out var required))
			{
				if (!visited.Add(required) || _typePool.HasConformance(self, required) ||
				    _foreignErrors.Keys.Any(written => written.Target == conformance.Target &&
				                                       written.Trait == required.Trait))
					continue;
				
				if (!implied.Any(existing => existing.Impl == conformance.Impl &&
				                             existing.Target == conformance.Target &&
				                             existing.Trait == required.Trait &&
				                             existing.Arguments.SequenceEqual(required.Arguments)))
					implied.Add(new(required.Trait, required.Arguments, conformance.Target, conformance.Parameters,
						conformance.Impl, conformance.Location)
					{
						IsImplied = true,
						Owner = conformance.Owner
					});
				
				foreach (var next in _typePool.GetRequiredTraits(required, self))
					pending.Enqueue(next);
			}
		}
		
		foreach (var conformance in implied)
		{
			_localConformances.Add(conformance);
			if (FindForeignError(conformance) is { } error)
				_foreignErrors[conformance] = error;
			else
				_typePool.AddConformance(conformance);
		}
	}
	
	private string? FindForeignError(Conformance conformance)
	{
		var trait = DescribeTrait(conformance);
		return conformance.Target is TypeParameterSymbol && !IsLocal(conformance.Trait)
			? $"'{trait}' is declared in another project"
			: conformance.Impl is not null && !IsLocal(conformance.Trait) && !IsLocal(conformance.Target)
				? $"'{trait}' and '{conformance.Target.Name}' are both declared in other projects"
				: null;
	}
	
	public bool DeclaresTypeParameter(ImplNode node, int index) =>
		_symbolTable.DeclarationSymbols.GetValueOrDefault(node) is not ImplSymbol impl ||
		index >= impl.TypeParameters.Length || impl.Declares(impl.TypeParameters[index]);
	
	public IEnumerable<Token> GetFreeParameterNames(ImplNode node) =>
		_symbolTable.DeclarationSymbols.GetValueOrDefault(node) is ImplSymbol impl
			? impl.FreeParameters.Select(static parameter => parameter.Definition).OfType<Token>()
			: [];
	
	private void RegisterImpl(ImplSymbol impl, ImplNode node, ResolutionContext context)
	{
		var leaves = TypeParameterNode.CollectNestedLeaves(node.TypeParameters).ToList();
		impl.NestedArguments =
		[
			..leaves.Select((leaf, i) => ResolveTargetArgument(leaf, context) ?? impl.NestedParameters[i])
		];
		
		var nestedIndex = 0;
		impl.TargetArguments =
		[
			..node.TypeParameters.Select((entry, i) => entry.Nested.IsEmpty
				? ResolveTargetArgument(entry, context) ?? impl.TypeParameters[i]
				: ResolveNestedTarget(impl, entry, context, ref nestedIndex))
		];
		
		DeclareFreeParameters(impl, node, context with { ImplBlock = impl });
		RegisterBounds(node.TypeParameters, impl.TypeParameters, context with { ImplBlock = impl });
		RegisterBounds([..leaves], impl.NestedParameters, context with { ImplBlock = impl });
		RegisterBounds([..TypeParameterNode.CollectDeclared(node.TypeParameters)], impl.FreeParameters,
			context with { ImplBlock = impl });
		
		ReportDuplicateNestedParameters(impl, node, leaves);
		if (ResolveImplTarget(impl, node, context) is not { } target)
			return;
		
		_implTargets[impl] = target;
		if (node.Traits.IsEmpty && IsExtensionTarget(target, context))
			RegisterExtension(impl, node, context);
		
		if (node.Traits.IsEmpty)
			_typePool.AddMemberBlock(impl, target);
		
		var deferring = context with
		{
			ImplBlock = impl,
			DeferConstraintCheck = DeferAlways,
			TraitSelf = target is NamedTypeSymbol { IsGenericDefinition: true } definition
				? _typePool.Instantiate(definition, [..impl.TargetArguments])
				: target
		};
		
		var traits = new List<TraitType>();
		foreach (var traitNode in node.Traits)
		{
			if (deferring.ResolveTrait(traitNode) is not { } trait)
				continue;
			
			traits.Add(trait);
			AddConformance(new(trait.Trait, trait.Arguments, target, impl.TypeParameters, impl,
				traitNode.SourceLocation));
		}
		
		impl.Traits = [..traits];
	}
	
	private bool IsExtensionTarget(TypeSymbol target, ResolutionContext context) =>
		target is TypeParameterSymbol || target.OriginalDefinition is not NamedTypeSymbol named ||
		Modules.FindModule(named) != context.File.Module;
	
	private void RegisterExtension(ImplSymbol impl, ImplNode node, ResolutionContext context)
	{
		impl.ExtensionFile = context.File;
		impl.ExtensionAssembly = Modules.AssemblyName;
		foreach (var member in node.Members)
		{
			if (!_symbolTable.DeclarationSymbols.TryGetValue(member, out var symbol))
				continue;
			
			switch (symbol)
			{
				case FunctionSymbol function when member is FunctionNode functionNode:
					function.Visibility = Visibility.FromKeyword(functionNode.Visibility);
					break;
				
				case GlobalSymbol global when member is GlobalNode globalNode:
					global.Visibility = Visibility.FromKeyword(globalNode.Visibility);
					break;
				
				case PropertySymbol property when member is PropertyNode propertyNode:
					var keyword = propertyNode.Visibility;
					property.Visibility = Visibility.FromKeyword(keyword);
					foreach (var accessor in new[] { property.Getter, property.Setter }.OfType<FunctionAccessor>())
						accessor.Function.Visibility =
							Visibility.FromKeyword(((FunctionNode)accessor.Function.Syntax).Visibility ?? keyword);
					
					break;
			}
		}
	}
	
	private void DeclareFreeParameters(ImplSymbol impl, ImplNode node, ResolutionContext context)
	{
		var unused = impl.FreeCandidates
			.Where(candidate =>
				context.ResolveTypeName(new IdentifierTypeNode(candidate.Definition!.Value)) is not null)
			.Concat(impl.FreeParameters.Where((parameter, i) =>
				impl.TypeParameters.Any(other => impl.Declares(other) && other.Name == parameter.Name) ||
				impl.FreeParameters.Take(i).Any(other => other.Name == parameter.Name)))
			.ToHashSet();
		
		impl.FreeParameters =
		[
			..impl.FreeParameters.Concat(impl.FreeCandidates).Where(parameter => !unused.Contains(parameter))
		];
		
		if (unused.Count > 0)
			foreach (var function in GetBlockFunctions(impl))
			{
				function.DeclaredTypeParameters = [..function.DeclaredTypeParameters.Where(p => !unused.Contains(p))];
				function.TypeParameters = [..function.TypeParameters.Where(p => !unused.Contains(p))];
			}
		
		if (node.Traits.IsEmpty)
			return;
		
		foreach (var parameter in impl.FreeParameters)
			Diagnostics.Add(new(DiagnosticSeverity.Error, parameter.Definition!.Value.SourceLocation,
				"Cannot declare type parameters in trait impl constraints"));
	}
	
	private static IEnumerable<FunctionSymbol> GetBlockFunctions(ImplSymbol impl) => impl.Functions
		.Select(static method => method.Function)
		.Concat(impl.Properties
			.SelectMany(static property => new[] { property.Getter, property.Setter })
			.OfType<FunctionAccessor>()
			.Select(static accessor => accessor.Function));
	
	private TypeSymbol ResolveNestedTarget(ImplSymbol impl, TypeParameterNode entry, ResolutionContext context,
		ref int nestedIndex)
	{
		var arguments = new List<TypeSymbol>();
		foreach (var child in entry.Nested)
			arguments.Add(child.Nested.IsEmpty
				? impl.NestedArguments[nestedIndex++]
				: ResolveNestedTarget(impl, child, context, ref nestedIndex));
		
		var symbol = context.ResolveTypeName(new IdentifierTypeNode(entry.Identifier), true);
		if (TypeOverloads.FindFamily(symbol) is { } family)
			symbol = family.FirstOrDefault(member => TypeOverloads.GetParameters(member).Length == arguments.Count);
		
		if (symbol is not NamedTypeSymbol { IsGenericDefinition: true } definition)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, entry.Identifier.SourceLocation, symbol is null
				? $"Type '{entry.Identifier.Text}' not found in this scope"
				: $"'{entry.Identifier.Text}' takes no generic arguments"));
			
			return NativeSymbols.Invalid;
		}
		
		if (definition.TypeParameters.Length != arguments.Count)
		{
			Diagnostics.Add(ResolutionContext.ReportGenericArgumentCount(entry.Identifier.SourceLocation,
				definition.Name, definition.TypeParameters.Length));
			
			return NativeSymbols.Invalid;
		}
		
		var map = TypePool.CreateMap(definition.TypeParameters, arguments);
		for (var i = 0; i < arguments.Count; i++)
			DeferAlways(new(definition.TypeParameters[i], arguments[i], map, entry.Nested[i].SourceLocation));
		
		return arguments.Any(static argument => argument is InvalidType)
			? NativeSymbols.Invalid
			: _typePool.Instantiate(definition, [..arguments]);
	}
	
	private void ReportDuplicateNestedParameters(ImplSymbol impl, ImplNode node, List<TypeParameterNode> leaves)
	{
		var slots = node.TypeParameters
			.Where((entry, i) => entry.Nested.IsEmpty && impl.TargetArguments[i] is TypeParameterSymbol)
			.ToList();
		
		var nested = leaves.Where((_, i) => impl.NestedArguments[i] is TypeParameterSymbol)
			.Concat(TypeParameterNode.CollectDeclared(node.TypeParameters))
			.ToList();
		
		var all = slots.Concat(nested).ToList();
		foreach (var parameter in all.Where(parameter => all.Any(other => other != parameter &&
		                                                                  other.Identifier.Text ==
		                                                                  parameter.Identifier.Text &&
		                                                                  (nested.Contains(other) ||
		                                                                   nested.Contains(parameter)))))
			Diagnostics.Add(ReportDuplicateTypeParameter(parameter));
	}
	
	private static TypeSymbol? ResolveTargetArgument(TypeParameterNode entry, ResolutionContext context)
	{
		if (entry is not { Keywords.IsEmpty: true, Traits.IsEmpty: true, Constructors.IsEmpty: true, ValueType: null })
			return null;
		
		var name = new IdentifierTypeNode(entry.Identifier);
		return context.ResolveTypeName(name) is TypeSymbol ? context.ResolveType(name) : null;
	}
	
	private TypeSymbol? ResolveImplTarget(ImplSymbol impl, ImplNode node, ResolutionContext context)
	{
		var location = node.Target.SourceLocation;
		var name = location.GetText().ToString();
		if (node.IsBlanket)
			return ResolveBlanketTarget(impl, node);
		
		var symbol = context.ResolveTypeName(node.Target, true);
		if (TypeOverloads.FindFamily(symbol) is { } family)
		{
			List<GenericArgumentKind?> kinds = [..impl.TargetArguments.Select(GetTargetKind)];
			symbol = family.FirstOrDefault(member =>
				TypeOverloads.GetParameters(member).Length == kinds.Count && TypeOverloads.Accepts(member, kinds));
			
			if (symbol is null)
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, location,
					ResolutionContext.DescribeMissingOverload(name, kinds.Count)));
				
				return null;
			}
		}
		
		switch (symbol)
		{
			case NamedTypeSymbol named when named.TypeParameters.Length == impl.TargetArguments.Length:
				return named;
			
			case NamedTypeSymbol named:
				Diagnostics.Add(ResolutionContext.ReportGenericArgumentCount(location, named.Name,
					named.TypeParameters.Length));
				
				return null;
			
			case PrimitiveType primitive when impl.TargetArguments.IsEmpty:
				return primitive;
			
			case PrimitiveType:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{name}' takes no generic arguments"));
				return null;
			
			case PointerType when impl.TargetArguments.IsEmpty:
				return NativeSymbols.VoidPtr;
			
			case PointerType when impl.TargetArguments.Length == 1:
				return ReportParameterViolations(NativeSymbols.GenericPointer.TypeParameters, impl, node)
					? _typePool.GetPointerType(impl.TargetArguments[0])
					: null;
			
			case PointerType:
				Diagnostics.Add(ResolutionContext.ReportGenericArgumentCount(location,
					NativeSymbols.GenericPointer.Name,
					NativeSymbols.GenericPointer.TypeParameters.Length));
				
				return null;
			
			case null when node.Target is IdentifierTypeNode { Token.Text: var builtin } &&
			               NativeSymbols.FindGeneric(builtin) is { Kind: BuiltinGenericKind.Array }:
				return ResolveArrayImplTarget(impl, node);
			
			case null when node.Target is IdentifierTypeNode { Token.Text: var builtin } &&
			               NativeSymbols.FindGeneric(builtin) is not null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot implement traits for '{name}'"));
				return null;
			
			case null when node.Target is IdentifierTypeNode:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Type '{name}' not found in this scope"));
				return null;
			
			case null:
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot implement traits for '{name}'"));
				return null;
		}
	}
	
	private static GenericArgumentKind GetTargetKind(TypeSymbol argument) => argument switch
	{
		TypeParameterSymbol parameter => TypeOverloads.GetKind(parameter),
		ValueArgumentType => GenericArgumentKind.Value,
		_ => GenericArgumentKind.Type
	};
	
	private TypeSymbol? ResolveBlanketTarget(ImplSymbol impl, ImplNode node)
	{
		if (node.TypeParameters is [_, var extra, ..])
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, extra.SourceLocation,
				"Blanket impls take one type parameter"));
			
			return null;
		}
		
		if (impl.TargetArguments[0] is not TypeParameterSymbol { IsValue: false } parameter)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.TypeParameters[0].SourceLocation,
				"Expected a type parameter"));
			
			return null;
		}
		
		return parameter;
	}
	
	private TypeSymbol? ResolveArrayImplTarget(ImplSymbol impl, ImplNode node)
	{
		var declared = NativeSymbols.GenericArray.TypeParameters;
		if (impl.TargetArguments.Length != declared.Length)
		{
			Diagnostics.Add(ResolutionContext.ReportGenericArgumentCount(node.Target.SourceLocation,
				NativeSymbols.GenericArray.Name, declared.Length));
			
			return null;
		}
		
		return ReportParameterViolations(declared, impl, node)
			? _typePool.GetArrayType(impl.TargetArguments[0], impl.TargetArguments[1])
			: null;
	}
	
	private void RegisterImplMembers(ImplSymbol impl)
	{
		var declaration = _declarations[impl];
		var target = GetImplTarget(impl);
		if (target is NamedTypeSymbol { IsGenericDefinition: true } definition)
			target = _typePool.Instantiate(definition, [..impl.TargetArguments]);
		
		_implTargets[impl] = target;
		foreach (var property in impl.Properties)
			property.ContainingType = target;
		
		var memberContext = declaration.Context with { ContainingType = target, ImplBlock = impl };
		foreach (var member in ((ImplNode)declaration.Node).Members)
		{
			var memberSymbol = _symbolTable.DeclarationSymbols[member];
			_declarations[memberSymbol] = new(member, WithTypeParameters(memberContext, memberSymbol));
			if (member is FunctionNode function)
				RegisterBounds(function.TypeParameters, ((FunctionSymbol)memberSymbol).DeclaredTypeParameters,
					_declarations[memberSymbol].Context);
			
			if (member is not PropertyNode property)
				continue;
			
			foreach (var accessor in property.Accessors)
				_declarations[_symbolTable.DeclarationSymbols[accessor]] = new(accessor, memberContext);
		}
	}
	
	private static ResolutionContext WithTypeParameters(ResolutionContext context, Symbol symbol) =>
		symbol is FunctionSymbol { DeclaredTypeParameters: { IsEmpty: false } typeParameters }
			? context with { TypeParameters = typeParameters }
			: context;
	
	private Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context, TypeSymbol? targetType) =>
		constants!.EvaluateConstant(expression, context, targetType);
	
	private ValueParameterUse? FindValueParameterUse(IExpressionNode expression, ResolutionContext context) =>
		constants!.FindValueParameterUse(expression, context);
	
	private Constant? EvaluateChecked(IResolvedExpressionNode value)
	{
		if (Evaluator.FindConstantError(value) is not { } error)
			return Evaluator.Evaluate(value);
		
		Diagnostics.Add(error);
		return InvalidConstant.Instance;
	}
	
	private void CheckImplBlocks()
	{
		var blocks = _impls.Where(impl => ((ImplNode)_declarations[impl].Node).Traits.IsEmpty).ToList();
		var conformances = _impls.Except(blocks).ToList();
		foreach (var impl in conformances)
		{
			using var frame = Own(impl);
			if (GetImplTarget(impl).OriginalDefinition is NamedTypeSymbol named)
				ReportParameterViolations(named.TypeParameters, impl, (ImplNode)_declarations[impl].Node);
		}
		
		foreach (var impl in blocks)
		{
			using var frame = Own(impl);
			CheckMemberBlock(impl, (ImplNode)_declarations[impl].Node);
		}
		
		foreach (var sameModule in blocks.Where(static impl => impl.IsExtension)
			         .GroupBy(impl => (Target: GetExtendedType(impl), impl.ExtensionFile!.Module)))
			ReportExtensionDuplicates([..sameModule]);
		
		foreach (var sameType in blocks.Where(static impl => !impl.IsExtension)
			         .GroupBy(impl => GetImplTarget(impl).OriginalDefinition))
			ReportMemberBlockConflicts(sameType.Key, [..sameType],
				[..conformances.Where(impl => GetImplTarget(impl).OriginalDefinition == sameType.Key)]);
	}
	
	private TypeSymbol? GetExtendedType(ImplSymbol impl) =>
		GetImplTarget(impl) is TypeParameterSymbol ? null : GetImplTarget(impl).OriginalDefinition;
	
	private void ReportExtensionDuplicates(IReadOnlyList<ImplSymbol> blocks)
	{
		if (blocks.Count < 2)
			return;
		
		var members = blocks
			.SelectMany(impl => impl.Functions.Select(method =>
				(Impl: impl, method.Name, method.Function,
					Signature: GetAlignedSignature(impl, method.Function, blocks[0]))))
			.ToList();
		
		foreach (var member in members)
		{
			if (!members.Any(other => other.Impl != member.Impl && other.Name == member.Name &&
			                          HaveSameParameters(other.Signature, member.Signature)))
				continue;
			
			var target = DiagnosticReporter.DescribeType(GetImplTarget(member.Impl).OriginalDefinition);
			using var frame = Own(member.Function);
			Diagnostics.Add(new(DiagnosticSeverity.Error, GetFunctionLocation(member.Function),
				$"'{target}.{member.Name}' is declared more than once with the same signature"));
		}
		
		var constants = blocks.SelectMany(static impl => impl.Constants).ToList();
		foreach (var constant in constants.Where(constant =>
			         constants.Any(other => other.Impl != constant.Impl && other.Name == constant.Name)))
		{
			var target = DiagnosticReporter.DescribeType(GetImplTarget(constant.Impl!).OriginalDefinition);
			using var frame = Own(constant);
			Diagnostics.Add(new(DiagnosticSeverity.Error, constant.Syntax.Identifier.SourceLocation,
				$"'{target}.{constant.Name}' is declared more than once"));
		}
	}
	
	private FunctionSignature GetAlignedSignature(ImplSymbol impl, FunctionSymbol function, ImplSymbol reference)
	{
		var map = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		for (var i = 0; i < impl.TargetArguments.Length && i < reference.TargetArguments.Length; i++)
			if (impl.TargetArguments[i] is TypeParameterSymbol parameter)
				map[parameter] = reference.TargetArguments[i];
		
		return _typePool.SubstituteSignature(GetFunctionInfo(function).Signature, map);
	}
	
	private static bool HaveSameParameters(FunctionSignature a, FunctionSignature b) =>
		a.ParameterTypes.SequenceEqual(b.ParameterTypes) &&
		Enumerable.Range(0, a.ParameterTypes.Length).All(i => a.GetMode(i) == b.GetMode(i));
	
	private void CheckMemberBlock(ImplSymbol impl, ImplNode node)
	{
		var target = GetImplTarget(impl).OriginalDefinition;
		if (target is InvalidType)
			return;
		
		if (impl.IsExtension)
		{
			if (target is NamedTypeSymbol extended)
				ReportParameterViolations(extended.TypeParameters, impl, node);
			
			return;
		}
		
		foreach (var keyword in node.Members.Select(static member => member switch
			         {
				         FunctionNode function => function.Visibility,
				         PropertyNode property => property.Visibility,
				         ConstructorNode constructor => constructor.Visibility,
				         _ => null
			         })
			         .OfType<Token>()
			         .Where(static keyword => keyword.Type == TokenType.KeywordPub)
			         .DistinctBy(static keyword => keyword.SourceLocation))
			Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation, "Cannot declare 'pub' members"));
		
		if (!IsLocal(target))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Target.SourceLocation,
				$"'{target.Name}' is declared in another project"));
			
			return;
		}
		
		var declared = target is NamedTypeSymbol named ? named.TypeParameters : [];
		var map = TypePool.CreateMap(declared, [..impl.TypeParameters]);
		if (ReportParameterViolations(declared, impl, node) && !declared
			    .Where((parameter, i) => impl.TargetArguments[i] is not TypeParameterSymbol ||
			                             AddsBound(parameter, impl.TypeParameters[i], map))
			    .Any())
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Keyword.SourceLocation,
				"Cannot declare 'impl' blocks that add no bounds"));
	}
	
	private bool ReportParameterViolations(ImmutableArray<TypeParameterSymbol> declared, ImplSymbol impl,
		ImplNode node)
	{
		var map = TypePool.CreateMap(declared, [..impl.TargetArguments]);
		var isValid = true;
		for (var i = 0; i < declared.Length; i++)
		{
			if (_typePool.FindConstraintViolation(declared[i], impl.TargetArguments[i], map) is not { } violation)
				continue;
			
			Diagnostics.Add(violation.At(node.TypeParameters[i].SourceLocation));
			isValid = false;
		}
		
		return isValid;
	}
	
	private bool AddsBound(TypeParameterSymbol declared, TypeParameterSymbol block,
		Dictionary<TypeParameterSymbol, TypeSymbol> map) =>
		block.IsValue && block.ValueType != declared.ValueType ||
		block.IsNoref && !declared.IsNoref || block.HasNull && !declared.HasNull || block.IsCopy && !declared.IsCopy ||
		block.HasDrop && !declared.HasDrop || block.HasNew && !_typePool.HasNew(declared) ||
		block.HasDefault && !declared.HasDefault ||
		block.IsAtomic && !declared.IsAtomic || block.IsExt && !declared.IsExt ||
		_typePool.GetConstructorBounds(block).Any(constructor => !_typePool.GetConstructors(declared)
			.Any(existing => TypePool.MatchesConstructor(_typePool.SubstituteSignature(existing.Signature, map),
				constructor.Signature))) ||
		_typePool.GetBounds(block)
			.Except(_typePool.GetBounds(declared).Select(bound => _typePool.SubstituteTrait(bound, map)))
			.Any() ||
		_typePool.GetParameterBounds(block)
			.Except(_typePool.GetParameterBounds(declared).Select(bound => map[bound]))
			.Any();
	
	private void ReportMemberBlockConflicts(TypeSymbol target, IReadOnlyList<ImplSymbol> blocks,
		IReadOnlyList<ImplSymbol> conformances)
	{
		if (!_declarations.TryGetValue(target, out var declaration))
			return;
		
		var declared = target is NamedTypeSymbol named ? named.TypeParameters : [];
		var members = GetBodyMembers(declaration.Node)
			.Select(static member => (member.Name, member.Function, Block: (ImplSymbol?)null))
			.Concat(blocks.Concat(conformances).SelectMany(impl => GetBlockMembers(impl)
				.Select(member => (member.Name, member.Function, Block: (ImplSymbol?)impl))))
			.ToList();
		
		foreach (var sameName in members.GroupBy(static member => member.Name.Text))
		{
			if (!sameName.Any(member => member.Block is { } block && blocks.Contains(block)))
				continue;
			
			foreach (var member in sameName)
			{
				var isMemberBlock = member.Block is { } own && blocks.Contains(own);
				var others = sameName.Where(other => other.Block != member.Block &&
				                                     (isMemberBlock || other.Block is { } block &&
					                                     blocks.Contains(block)))
					.ToList();
				
				if (others.Count == 0)
					continue;
				
				var message = member.Function is not { } function || others.Any(static other => other.Function is null)
					? $"'{member.Name.Text}' is declared more than once in '{target.Name}'"
					: DescribeConflict(member.Name.Text, GetDeclaredSignature(function, member.Block, declared),
						others.Select(other => AlignTypeParameters(function, other.Function!,
							GetDeclaredSignature(other.Function!, other.Block, declared))).OfType<FunctionSignature>(),
						CountFixedModes(function, GetFunctionInfo(function).Signature));
				
				if (message is null)
					continue;
				
				using var frame = member.Function is { } owner ? Own(owner) : Own(declaration.Node);
				Diagnostics.Add(new(DiagnosticSeverity.Error, member.Name.SourceLocation, message));
			}
		}
		
		ReportBlockConstructorConflicts(declaration.Node, blocks, declared);
	}
	
	private void ReportBlockConstructorConflicts(IDeclarationNode node, IReadOnlyList<ImplSymbol> blocks,
		ImmutableArray<TypeParameterSymbol> declared)
	{
		var constructors = (node is RecordNode record ? record.Members : [])
			.OfType<ConstructorNode>()
			.Select(constructor => (Function: (FunctionSymbol)_symbolTable.DeclarationSymbols[constructor],
				Block: (ImplSymbol?)null))
			.Concat(blocks.SelectMany(static impl => impl.Constructors.Select(constructor =>
				(Function: constructor, Block: (ImplSymbol?)impl))))
			.Where(constructor => _builder.Functions.ContainsKey(constructor.Function))
			.Select(constructor => (constructor.Function, constructor.Block,
				Signature: GetDeclaredSignature(constructor.Function, constructor.Block, declared)))
			.ToList();
		
		foreach (var constructor in constructors)
		{
			var others = constructors
				.Where(other => other.Function != constructor.Function &&
				                (constructor.Block is not null || other.Block is not null))
				.Select(static other => other.Signature);
			
			if (DescribeConflict(constructor.Function, constructor.Signature, others) is not { } message)
				continue;
			
			using var frame = Own(constructor.Function);
			Diagnostics.Add(new(DiagnosticSeverity.Error,
				((ConstructorNode)constructor.Function.Syntax).Keyword.SourceLocation, message));
		}
	}
	
	private IEnumerable<(Token Name, FunctionSymbol? Function)> GetBodyMembers(IDeclarationNode node)
	{
		ImmutableArray<IDeclarationNode> members = node switch
		{
			RecordNode record => record.Members,
			EnumNode enumNode => enumNode.Members,
			_ => []
		};
		
		IEnumerable<(Token, FunctionSymbol?)> cases = node is EnumNode { Cases: var enumCases }
			? enumCases.Select(static enumCase => (enumCase.Identifier, (FunctionSymbol?)null))
			: [];
		
		return cases.Concat(members.Select(GetNamedMember).OfType<(Token, FunctionSymbol?)>());
	}
	
	private IEnumerable<(Token Name, FunctionSymbol? Function)> GetBlockMembers(ImplSymbol impl) =>
		((ImplNode)_declarations[impl].Node).Members.Select(GetNamedMember).OfType<(Token, FunctionSymbol?)>();
}