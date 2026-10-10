using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Collections;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public interface IConstantResolver
{
	IResolvedExpressionNode ResolveInitializer(IExpressionNode initializer, TypeSymbol type, ResolutionContext context);
	Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context);
}

public sealed class SignatureCollector
{
	private readonly record struct Declaration(IDeclarationNode Node, ResolutionContext Context);
	
	private enum MemberKind
	{
		Field,
		Case,
		Function,
		Property
	}
	
	private static readonly HashSet<TokenType> _unaryOperators =
		[TokenType.OpPlus, TokenType.OpMinus, TokenType.OpTilde];
	
	private static readonly HashSet<TokenType> _compoundOperators =
	[
		TokenType.OpPlusEqual, TokenType.OpMinusEqual, TokenType.OpStarEqual, TokenType.OpSlashEqual,
		TokenType.OpPercentEqual, TokenType.OpPlusPercentEqual, TokenType.OpMinusPercentEqual,
		TokenType.OpStarPercentEqual, TokenType.OpLessLessEqual, TokenType.OpGreaterGreaterEqual,
		TokenType.OpLessLessLessEqual, TokenType.OpGreaterGreaterGreaterEqual, TokenType.OpAmpersandEqual,
		TokenType.OpBarEqual, TokenType.OpHatEqual
	];
	
	private readonly string? _entryPointName;
	private readonly SymbolTable _symbolTable;
	private readonly TypePool _typePool;
	private readonly ImmutableArray<AssemblySymbol> _dependencies;
	private readonly SignatureTable _dependencyTable;
	private readonly SignatureTable.Builder _builder = new();
	private readonly Dictionary<Symbol, Declaration> _declarations = [];
	private readonly Dictionary<GlobalSymbol, TypeSymbol> _globalTypes = [];
	private readonly Dictionary<PropertySymbol, TypeSymbol> _propertyTypes = [];
	private readonly HashSet<TypeSymbol> _completedTypes = [];
	private readonly HashSet<Symbol> _completedSymbols = [];
	private readonly List<(Symbol Symbol, bool IsValue)> _inProgress = [];
	private readonly HashSet<Symbol> _cyclic = [];
	private readonly List<FunctionInfo> _entryPoints = [];
	private readonly ExtSignatureTypes _extSignatureTypes;
	private readonly uint _pointerBitSize;
	private readonly List<ImplSymbol> _impls = [];
	private readonly Dictionary<ImplSymbol, TypeSymbol> _implTargets = [];
	private readonly List<Conformance> _localConformances = [];
	private readonly HashSet<FunctionSymbol> _witnessMembers = [];
	private readonly HashSet<(SourceLocation, string)> _conformanceErrors = [];
	private readonly List<ConstraintCheck> _deferredChecks = [];
	private readonly List<(TypeParameterNode, TypeParameterSymbol, ResolutionContext)> _pendingConstructorBounds = [];
	private readonly List<(ImmutableArray<ITypeNode>, List<TypeSymbol?>, TypeSymbol)> _boundLists = [];
	private readonly List<(FieldNode, TypeSymbol)> _requiredFields = [];
	private readonly List<(GlobalSymbol Global, ITypeNode Node, TypeSymbol Type)> _variables = [];
	private readonly HashSet<FunctionSymbol> _lambdas = [];
	private IConstantResolver? constants;
	
	public DiagnosticList Diagnostics { get; } = new();
	public ConstantEvaluator Evaluator { get; }
	public ModuleIndex Modules { get; }
	
	public SignatureCollector(string? entryPointName, SymbolTable symbolTable, TypePool typePool,
		IEnumerable<AssemblySymbol> dependencies, uint pointerBitSize)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
		_typePool = typePool;
		_extSignatureTypes = new(typePool);
		_pointerBitSize = pointerBitSize;
		_dependencies = dependencies.ToImmutableArray();
		_dependencyTable = SignatureTable.Combine(_dependencies.Select(static a => a.SignatureTable));
		Evaluator = new ConstantEvaluator(typePool, pointerBitSize, GetGlobalValue);
		Modules = new ModuleIndex(symbolTable, _dependencies.Select(static a => a.SymbolTable));
	}
	
	public void Collect(IReadOnlyCollection<FileNode> files, IConstantResolver constantResolver)
	{
		constants = constantResolver;
		_typePool.TypeCompleter = Complete;
		_typePool.TraitCompleter = CompleteTrait;
		
		foreach (var file in files)
			Register(file);
		
		foreach (var file in files)
			RegisterConstraints(file);
		
		ReportRequirementCycles(files);
		
		foreach (var impl in _impls)
			RegisterImplMembers(impl);
		
		ReportRedundantBounds();
		RegisterConstructorBounds();
		
		foreach (var file in files)
			foreach (var declaration in file.Declarations)
				Complete(_symbolTable.DeclarationSymbols[declaration]);
		
		ReportRedundantRequirements();
		ReportDeferredChecks();
		ReportBorrowingVariables();
		CheckConformances();
		CheckMemberBlocks();
		_typePool.TypeCompleter = null;
		_typePool.TraitCompleter = null;
	}
	
	public TypeSymbol GetImplTarget(ImplSymbol impl) => _implTargets.GetValueOrDefault(impl, NativeSymbols.Invalid);
	
	public AssemblySymbol FinishAssembly(string name)
	{
		ReportDuplicateDeclarations();
		ReportModuleConflicts();
		ReportLayoutCycles();
		ReportEntryPoint();
		ReportPlainEnumsInExtSignatures();
		ReportDestructorsInExtSignatures();
		
		FunctionInfo? entryPoint = _entryPoints.Count == 1 ? _entryPoints[0] : null;
		return new(name, _symbolTable, _builder.Build(), entryPoint);
	}
	
	public bool IsLocal(Symbol symbol) =>
		_declarations.ContainsKey(symbol) || symbol is FunctionSymbol function && _lambdas.Contains(function);
	
	public void AddLambda(FunctionInfo info)
	{
		_lambdas.Add(info.Symbol);
		_builder.Functions[info.Symbol] = info;
		for (var i = 0; i < info.Symbol.Parameters.Length; i++)
			_builder.VariableTypes[info.Symbol.Parameters[i]] = info.Signature.ParameterTypes[i];
	}
	
	public void RemoveLambda(FunctionInfo info)
	{
		_lambdas.Remove(info.Symbol);
		_builder.Functions.Remove(info.Symbol);
		foreach (var parameter in info.Symbol.Parameters)
			_builder.VariableTypes.Remove(parameter);
	}
	
	public ImportEnvironment GetImports(FileSymbol file) => _builder.ImportEnvironments[file];
	
	public FunctionInfo GetFunctionInfo(FunctionSymbol function)
	{
		if (_builder.Functions.TryGetValue(function, out var info))
			return info;
		
		if (!_declarations.TryGetValue(function, out var declaration))
			return _dependencyTable.Functions[function];
		
		if (declaration.Node is ConstructorNode or DestructorNode)
		{
			if (declaration.Context.Trait is { } trait)
				CompleteTrait(trait);
			else if (declaration.Context.ContainingType is RecordSymbol record)
				CompleteRecord(record);
			
			return _builder.Functions.TryGetValue(function, out info) ? info : CreateInvalidInfo(function, declaration);
		}
		
		if (!Enter(function, false))
			return CreateInvalidInfo(function, declaration);
		
		info = declaration.Node switch
		{
			FunctionNode node => CollectFunction(function, node, declaration.Context),
			ExternalFunctionNode node => CollectExternalFunction(function, node, declaration.Context),
			_ => throw new InvalidOperationException()
		};
		
		_builder.Functions[function] = info;
		Exit();
		return info;
	}
	
	public TypeSymbol GetVariableType(VariableSymbol variable)
	{
		if (variable is GlobalSymbol global)
			return GetGlobalType(global);
		
		return _builder.VariableTypes.TryGetValue(variable, out var type)
			? type
			: _dependencyTable.VariableTypes[variable];
	}
	
	public TypeSymbol GetGlobalType(GlobalSymbol global)
	{
		if (_globalTypes.TryGetValue(global, out var type))
			return type;
		
		if (global.ContainingType is NamedTypeSymbol { IsGenericInstance: true } instance)
			return _typePool.Substitute(GetGlobalType(instance.Definition.GetStaticField(global.Name)!),
				TypePool.CreateMap(instance.Definition.TypeParameters, instance.TypeArguments));
		
		if (!_declarations.TryGetValue(global, out var declaration))
			return _dependencyTable.Globals[global].Type;
		
		if (!Enter(global, false))
			return NativeSymbols.Invalid;
		
		var node = (GlobalNode)declaration.Node;
		type = RejectValueDyn(node.Type, declaration.Context.ResolveType(node.Type));
		var visibility = global.ContainingType is { } owner
			? GetEffectiveVisibility(global.Visibility, owner)
			: global.Visibility;
		
		ReportHiddenType(node.Type, type, visibility, global.Name);
		if (node.IsMutable)
			_variables.Add((global, node.Type, type));
		
		_globalTypes[global] = type;
		Exit();
		return type;
	}
	
	public GlobalInfo? GetGlobalInfo(GlobalSymbol global)
	{
		if (_builder.Globals.TryGetValue(global, out var info))
			return info;
		
		if (global.ContainingType is NamedTypeSymbol { IsGenericInstance: true } instance)
			return GetGlobalInfo(instance.Definition.GetStaticField(global.Name)!) is null
				? null
				: _typePool.InstantiateGlobal(global, Modules);
		
		if (!_declarations.TryGetValue(global, out var declaration))
			return _dependencyTable.Globals[global];
		
		var type = GetGlobalType(global);
		if (!Enter(global, true))
			return null;
		
		var node = (GlobalNode)declaration.Node;
		var context = declaration.Context;
		var initializer = constants!.ResolveInitializer(node.Initializer, type, context);
		var value = type is InvalidType ? InvalidConstant.Instance : Evaluator.Evaluate(initializer);
		var mangledName = context.Mangle(global);
		
		info = new GlobalInfo(mangledName, global, type, initializer, value, context.File);
		_builder.Globals[global] = info;
		if (global.ContainingType is NamedTypeSymbol { IsGenericDefinition: true })
			_typePool.RegisterGenericGlobal(info);
		
		Exit();
		return info;
	}
	
	private Constant GetGlobalValue(GlobalSymbol global) =>
		GetGlobalInfo(global) is { Value: { } value } ? value : InvalidConstant.Instance;
	
	private void ReportBorrowingVariables()
	{
		foreach (var (global, node, type) in _variables)
		{
			if (TypePool.ContainsTypeParameters(type) || !_typePool.HoldsBorrows(type))
				continue;
			
			var kind = global.ContainingType is null ? "module" : "static";
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot store borrows in {kind} variables"));
		}
	}
	
	private void Register(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var context = new ResolutionContext
		{
			File = file,
			Modules = Modules,
			TypePool = _typePool,
			Diagnostics = Diagnostics,
			ExtSignatureTypes = _extSignatureTypes,
			EvaluateConstant = EvaluateConstant,
			GenericTypes = [],
			DeferConstraintCheck = DeferConstraintCheck
		};
		
		var imports = CollectImports(node, context);
		_builder.ImportEnvironments[file] = imports;
		context = context with { Imports = imports };
		
		foreach (var declaration in node.Declarations)
		{
			var symbol = _symbolTable.DeclarationSymbols[declaration];
			_declarations[symbol] = new(declaration, WithTypeParameters(context, symbol));
			if (symbol is EnumSymbol { HasPayload: false, IsMatch: false } enumType)
				_typePool.RegisterEnumConversions(enumType);
			
			if (symbol is ImplSymbol impl)
				_impls.Add(impl);
			
			IEnumerable<IDeclarationNode> members = declaration switch
			{
				RecordNode record => record.Members,
				EnumNode enumNode => enumNode.Members,
				TraitNode trait => trait.Members,
				_ => []
			};
			
			var memberContext = symbol is TraitSymbol traitSymbol
				? context with { ContainingType = traitSymbol.Self, Trait = traitSymbol }
				: context with { ContainingType = symbol as TypeSymbol };
			
			foreach (var member in members)
			{
				var memberSymbol = _symbolTable.DeclarationSymbols[member];
				_declarations[memberSymbol] = new(member, WithTypeParameters(memberContext, memberSymbol));
				if (member is not PropertyNode property)
					continue;
				
				foreach (var accessor in property.Accessors)
					_declarations[_symbolTable.DeclarationSymbols[accessor]] = new(accessor, memberContext);
			}
		}
	}
	
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
						context with { ContainingType = (RecordSymbol)symbol });
					
					RegisterMemberBounds(record.Members);
					RegisterConformances((NamedTypeSymbol)symbol, record.Traits, context);
					break;
				
				case EnumNode enumNode:
					RegisterBounds(enumNode.TypeParameters, ((EnumSymbol)symbol).TypeParameters,
						context with { ContainingType = (EnumSymbol)symbol });
					
					RegisterMemberBounds(enumNode.Members);
					RegisterConformances((NamedTypeSymbol)symbol, enumNode.Traits, context);
					break;
				
				case FunctionNode function:
					RegisterBounds(function.TypeParameters, ((FunctionSymbol)symbol).DeclaredTypeParameters, context);
					break;
				
				case TraitNode traitNode:
					var trait = (TraitSymbol)symbol;
					var traitContext = context with { ContainingType = trait.Self, Trait = trait };
					_typePool.SetBounds(trait.Self, [_typePool.GetTraitType(trait, [..trait.TypeParameters])]);
					RegisterBounds(traitNode.TypeParameters, trait.TypeParameters, traitContext);
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
			RegisterBounds(member.TypeParameters, function.DeclaredTypeParameters, _declarations[function].Context);
		}
	}
	
	private void RegisterBounds(ImmutableArray<TypeParameterNode> nodes, ImmutableArray<TypeParameterSymbol> parameters,
		ResolutionContext context)
	{
		var deferring = context with { DeferConstraintCheck = DeferAlways };
		for (var i = 0; i < nodes.Length && i < parameters.Length; i++)
		{
			ReportConstraintConflicts(nodes[i]);
			var bounds = nodes[i].Traits.Select(deferring.ResolveTraitReference).ToList();
			ReportRepeatedTraits(nodes[i].Traits, bounds);
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
				_pendingConstructorBounds.Add((nodes[i], parameters[i], context));
		}
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
		foreach (var (node, parameter, context) in _pendingConstructorBounds)
		{
			var deferring = context with { DeferConstraintCheck = DeferAlways };
			var constructors = node.Constructors
				.Select(constructor => CollectConstructorBound(constructor, parameter, deferring))
				.ToList();
			
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
				
				modes[i] = borrow.IsMutable ? ParameterMode.Mut : ParameterMode.ReadOnly;
				typeNode = borrow.Target;
			}
			else if (typeNode is FunctionTypeNode { IsRef: true } function)
				Diagnostics.Add(DiagnosticReporter.ReportRefFunctionParameter(function, null, node.Modes[i] is not null,
					false));
			
			var type = RejectValueDyn(typeNode, context.ResolveType(typeNode), modes[i]);
			types.Add(_typePool.GetPassedType(type, modes[i]));
			parameters.Add(new($"${i + 1}", typeNode.SourceLocation) { Mode = modes[i] });
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
				? $"'new({TypePool.DescribeParameters(signature)})' is required more than once"
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
		
		if (keywords.All(static keyword => keyword.Type != TokenType.KeywordDrop))
			return;
		
		foreach (var copying in keywords.Where(static k => k.Type is TokenType.KeywordCopy or TokenType.KeywordAtomic))
		{
			var message = $"Cannot combine '{copying.Text}' and 'drop'";
			foreach (var keyword in keywords.Where(k => k == copying || k.Type == TokenType.KeywordDrop))
				Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation, message));
		}
	}
	
	private void RegisterConformances(NamedTypeSymbol type, ImmutableArray<ITypeNode> traits, ResolutionContext context)
	{
		var deferring = context with { ContainingType = type, DeferConstraintCheck = DeferAlways };
		foreach (var traitNode in traits)
		{
			if (deferring.ResolveTrait(traitNode) is { } trait)
				AddConformance(new(trait.Trait, trait.Arguments, type, type.TypeParameters, null,
					traitNode.SourceLocation));
		}
	}
	
	private void AddConformance(Conformance conformance)
	{
		_typePool.AddConformance(conformance);
		_localConformances.Add(conformance);
	}
	
	private void RegisterImpl(ImplSymbol impl, ImplNode node, ResolutionContext context)
	{
		RegisterBounds(node.TypeParameters, impl.TypeParameters, context with { ImplBlock = impl });
		if (ResolveImplTarget(impl, node, context) is not { } target)
			return;
		
		_implTargets[impl] = target;
		if (node.Traits.IsEmpty)
			_typePool.AddMemberBlock(impl, target);
		
		var deferring = context with { ImplBlock = impl, DeferConstraintCheck = DeferAlways };
		foreach (var traitNode in node.Traits)
		{
			if (deferring.ResolveTrait(traitNode) is { } trait)
				AddConformance(new(trait.Trait, trait.Arguments, target, impl.TypeParameters, impl,
					traitNode.SourceLocation));
		}
	}
	
	private TypeSymbol? ResolveImplTarget(ImplSymbol impl, ImplNode node, ResolutionContext context)
	{
		var location = node.Target.SourceLocation;
		var name = location.GetText().ToString();
		switch (context.ResolveTypeName(node.Target))
		{
			case NamedTypeSymbol named when named.TypeParameters.Length == impl.TypeParameters.Length:
				return named;
			
			case NamedTypeSymbol named:
				Diagnostics.Add(ResolutionContext.ReportTypeArgumentCount(location, named));
				return null;
			
			case PrimitiveType primitive when impl.TypeParameters.IsEmpty:
				return primitive;
			
			case PrimitiveType:
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{name}' takes no type arguments"));
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
	
	private void RegisterImplMembers(ImplSymbol impl)
	{
		var declaration = _declarations[impl];
		var target = GetImplTarget(impl);
		if (target is NamedTypeSymbol { IsGenericDefinition: true } definition)
			target = _typePool.Instantiate(definition, [..impl.TypeParameters]);
		
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
	
	private Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context) =>
		constants!.EvaluateConstant(expression, context);
	
	private void Complete(Symbol symbol)
	{
		switch (symbol)
		{
			case FunctionSymbol function:
				GetFunctionInfo(function);
				break;
			
			case RecordSymbol record:
				CompleteRecord(record);
				break;
			
			case EnumSymbol enumType:
				CompleteEnum(enumType);
				break;
			
			case GlobalSymbol global:
				GetGlobalInfo(global);
				break;
			
			case PropertySymbol property:
				CompleteProperty(property);
				break;
			
			case TraitSymbol trait:
				CompleteTrait(trait);
				break;
			
			case ImplSymbol impl:
				CompleteImpl(impl);
				break;
		}
	}
	
	private void CompleteTrait(TraitSymbol trait)
	{
		if (!_declarations.TryGetValue(trait, out var declaration) || !_completedSymbols.Add(trait))
			return;
		
		var node = (TraitNode)declaration.Node;
		ReportTypeParameters(node.TypeParameters, node.Members);
		CompleteMembers(node.Members, "traits");
		foreach (var constructor in node.Members.OfType<ConstructorNode>().Where(static c => c.Body is not null))
			Diagnostics.Add(new(DiagnosticSeverity.Error, constructor.Keyword.SourceLocation,
				"Cannot declare constructor bodies in traits"));
		
		foreach (var function in node.Members.OfType<FunctionNode>().Concat(node.Members.OfType<PropertyNode>()
			         .SelectMany(static property => property.Accessors)))
		{
			var symbol = (FunctionSymbol)_symbolTable.DeclarationSymbols[function];
			if (function.Body is null && symbol.Visibility == Visibility.Private)
				Diagnostics.Add(new(DiagnosticSeverity.Error, function.Identifier.SourceLocation,
					"'pvt' members need a body"));
		}
		
		ReportMembers(trait.Name, trait.Self, node.Members);
		ReportConstructorConflicts(node.Members);
	}
	
	private void CompleteImpl(ImplSymbol impl)
	{
		if (!_declarations.TryGetValue(impl, out var declaration) || !_completedSymbols.Add(impl))
			return;
		
		var node = (ImplNode)declaration.Node;
		CompleteMembers(node.Members, "impl blocks");
		var target = GetImplTarget(impl);
		ReportMembers(target.Name, target, node.Members);
	}
	
	private void CompleteMembers(IEnumerable<IDeclarationNode> members, string kind)
	{
		foreach (var member in members)
		{
			var symbol = _symbolTable.DeclarationSymbols[member];
			var context = _declarations[symbol].Context;
			switch (member)
			{
				case FieldNode field:
					Diagnostics.Add(new(DiagnosticSeverity.Error, field.Identifier.SourceLocation,
						$"Cannot declare fields in {kind}"));
					
					break;
				
				case GlobalNode global:
					Diagnostics.Add(new(DiagnosticSeverity.Error, global.Identifier.SourceLocation,
						$"Cannot declare static fields in {kind}"));
					
					break;
				
				case DestructorNode destructor:
					Diagnostics.Add(new(DiagnosticSeverity.Error, destructor.Keyword.SourceLocation,
						$"Cannot declare destructors in {kind}"));
					
					break;
				
				case ConstructorNode constructor when context.ImplBlock is not null:
					Diagnostics.Add(new(DiagnosticSeverity.Error, constructor.Keyword.SourceLocation,
						$"Cannot declare constructors in {kind}"));
					
					break;
				
				case ConstructorNode constructor:
					if (!_builder.Functions.ContainsKey((FunctionSymbol)symbol))
						CollectConstructor((FunctionSymbol)symbol, constructor, context);
					
					break;
				
				default:
					Complete(symbol);
					break;
			}
		}
	}
	
	private void ReportMembers(string owner, TypeSymbol type, ImmutableArray<IDeclarationNode> members)
	{
		var functions = members.OfType<FunctionNode>().ToLookup(IsModeOperator);
		ReportMemberConflicts(owner, [
			..members.OfType<PropertyNode>()
				.Select(static p => (p.Identifier, (FunctionNode?)null, MemberKind.Property)),
			..functions[false].Select(static f => (f.Identifier, (FunctionNode?)f, MemberKind.Function))
		]);
		
		ReportModeOperators(type, functions[true]);
		ReportOperators(type, functions[false]);
	}
	
	private void CheckConformances()
	{
		foreach (var sameTrait in _localConformances.GroupBy(static c => (c.Target, c.Trait)))
			ReportOverlaps([..sameTrait]);
		
		foreach (var conformance in _localConformances)
		{
			var trait = DescribeTrait(conformance);
			if (conformance.Impl is not null && !IsLocal(conformance.Trait) && !IsLocal(conformance.Target))
				Diagnostics.Add(new(DiagnosticSeverity.Error, conformance.Location,
					$"'{trait}' and '{conformance.Target.Name}' are both declared in other projects"));
			
			MatchRequirements(conformance);
			CheckRequiredTraits(conformance);
		}
		
		foreach (var impl in _impls)
			ReportUnmatchedMembers(impl);
	}
	
	private void CheckRequiredTraits(Conformance conformance)
	{
		var self = GetConformanceSelf(conformance);
		if (self is InvalidType)
			return;
		
		var trait = _typePool.GetTraitType(conformance.Trait, conformance.Arguments);
		foreach (var required in _typePool.GetRequiredTraits(trait, self))
		{
			if (!_typePool.Conforms(self, required))
				ReportConformance(conformance.Location, $"'{self.Name}' needs '{required.Name}'");
		}
	}
	
	private void ReportOverlaps(List<Conformance> conformances)
	{
		foreach (var conformance in conformances)
		{
			var overlap = conformances
				.Where(other => other != conformance)
				.Select(other => FindOverlap(conformance, other))
				.FirstOrDefault(static found => found is not null);
			
			if (overlap is var (target, trait))
				Diagnostics.Add(new(DiagnosticSeverity.Error, conformance.Location,
					$"'{target.Name}' implements '{trait.Name}' more than once"));
		}
	}
	
	private (TypeSymbol Target, TraitType Trait)? FindOverlap(Conformance conformance, Conformance other)
	{
		var renamed = TypePool.CreateMap(other.Parameters, [..conformance.Parameters]);
		var bindings = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		var variables = new OrderedSet<TypeParameterSymbol>(conformance.Parameters);
		for (var i = 0; i < conformance.Arguments.Length; i++)
		{
			var argument = _typePool.Substitute(other.Arguments[i], renamed);
			if (!_typePool.TryUnify(conformance.Arguments[i], argument, variables, bindings))
				return null;
		}
		
		var definition = conformance.Target as NamedTypeSymbol;
		var display = TypePool.CreateMap(conformance.Parameters, [..definition?.TypeParameters ?? []]);
		var declared = _typePool.GetTraitType(conformance.Trait, conformance.Arguments);
		var trait = _typePool.SubstituteTrait(_typePool.SubstituteTrait(declared, bindings), display);
		
		if (bindings.Count == 0 || definition is null)
			return (conformance.Target, trait);
		
		return (_typePool.Instantiate(definition,
		[
			..conformance.Parameters.Select(parameter =>
				_typePool.Substitute(bindings.GetValueOrDefault(parameter, parameter), display))
		]), trait);
	}
	
	private string DescribeTrait(Conformance conformance) =>
		_typePool.GetTraitType(conformance.Trait, conformance.Arguments).Name;
	
	private TypeSymbol GetConformanceSelf(Conformance conformance) =>
		conformance.Impl is { } impl ? GetImplTarget(impl) : conformance.Target;
	
	private static Dictionary<TypeParameterSymbol, TypeSymbol> CreateTraitMap(Conformance conformance, TypeSymbol self)
	{
		var map = TypePool.CreateMap(conformance.Trait.TypeParameters, conformance.Arguments);
		map[conformance.Trait.Self] = self;
		return map;
	}
	
	private void MatchRequirements(Conformance conformance)
	{
		var self = GetConformanceSelf(conformance);
		if (self is InvalidType)
			return;
		
		var trait = DescribeTrait(conformance);
		var traitMap = CreateTraitMap(conformance, self);
		foreach (var (name, requirement) in GetRequirements(conformance.Trait))
		{
			var info = GetFunctionInfo(requirement);
			var candidates = FindCandidates(conformance, self, name, requirement).ToList();
			var matches = candidates
				.Where(candidate => SignaturesMatch(candidate.Signature, info.Signature, requirement,
					candidate.Function, traitMap))
				.ToList();
			
			if (matches.Count == 0)
				matches = candidates
					.Where(candidate => SignaturesMatch(candidate.Signature, info.Signature, requirement,
						candidate.Function, traitMap, true))
					.ToList();
			
			switch (matches.Count)
			{
				case 1:
					conformance.Witnesses[requirement] =
						new FunctionWitness(matches[0].Function, GetFunctionInfo(matches[0].Function));
					
					_witnessMembers.Add(matches[0].Function);
					ReportNarrowWitness(trait, self, name, matches[0].Function);
					if (matches[0].Function.Syntax is FunctionNode { When: not null })
						ReportConformance(GetFunctionLocation(matches[0].Function),
							$"Cannot implement '{trait}.{name}' with 'when' functions");
					
					break;
				
				case > 1:
					_witnessMembers.UnionWith(matches.Select(static match => match.Function));
					foreach (var match in matches)
						ReportConformance(GetFunctionLocation(match.Function),
							$"'{conformance.Target.Name}.{name}' is declared more than once with the same signature");
					
					break;
				
				case 0 when requirement.Syntax is FunctionNode { Body: not null }:
					conformance.Witnesses[requirement] = new FunctionWitness(requirement, info);
					break;
				
				case 0 when FindNativeWitness(requirement, info.Signature, traitMap, self) is { } native:
					conformance.Witnesses[requirement] = native;
					break;
				
				case 0 when candidates.Count > 0:
					foreach (var candidate in candidates)
					{
						_witnessMembers.Add(candidate.Function);
						ReportConformance(GetFunctionLocation(candidate.Function),
							$"'{name}' doesn't match '{trait}.{name}'");
					}
					
					break;
				
				default:
					ReportConformance(conformance.Location, DescribeMissingRequirement(conformance, trait, self, name,
						requirement));
					
					break;
			}
		}
		
		foreach (var constructor in conformance.Trait.Constructors)
			MatchConstructor(conformance, trait, traitMap, self, constructor);
	}
	
	private void ReportNarrowWitness(string trait, TypeSymbol self, string name, FunctionSymbol witness)
	{
		var floor = self is NamedTypeSymbol named
			? (Visibility)Math.Max((int)named.Definition.Visibility, (int)Visibility.Module)
			: Visibility.Public;
		
		if (witness.Visibility >= floor)
			return;
		
		var keyword = GetVisibilityKeyword(witness);
		var text = keyword?.Text ?? (witness.Visibility == Visibility.Private ? "pvt" : "mod");
		ReportConformance(keyword?.SourceLocation ?? GetFunctionLocation(witness),
			$"Cannot implement '{trait}.{name}' with '{text}' members");
	}
	
	private static Token? GetVisibilityKeyword(FunctionSymbol function) => function.Syntax switch
	{
		FunctionNode { Visibility: { } keyword } => keyword,
		ConstructorNode { Visibility: { } keyword } => keyword,
		_ => function.Property?.Node?.Visibility
	};
	
	private void ReportConformance(SourceLocation location, string message)
	{
		if (_conformanceErrors.Add((location, message)))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
	}
	
	private static string DescribeMissingRequirement(Conformance conformance, string trait, TypeSymbol self,
		string name, FunctionSymbol requirement)
	{
		if (requirement.Property is null || !HasProperty(conformance, name))
			return $"'{self.Name}' needs '{trait}.{name}'";
		
		var accessor = GetAccessorKind(requirement) == "get" ? "getter" : "setter";
		return $"'{self.Name}' needs a {accessor} for '{trait}.{name}'";
	}
	
	private static bool HasProperty(Conformance conformance, string name) =>
		conformance.Target.GetProperty(name) is not null ||
		conformance.Impl?.Properties.Any(property => property.Name == name) == true;
	
	private void MatchConstructor(Conformance conformance, string trait,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, TypeSymbol self, FunctionSymbol requirement)
	{
		var signature = GetFunctionInfo(requirement).Signature;
		var declared = _typePool.GetConstructors(self);
		var matches = declared
			.Where(constructor => SignaturesMatch(constructor.Signature, signature, requirement, constructor.Symbol,
				traitMap))
			.ToList();
		
		if (matches.Count == 0)
			matches = declared
				.Where(constructor => SignaturesMatch(constructor.Signature, signature, requirement, constructor.Symbol,
					traitMap, true))
				.ToList();
		
		if (matches.Count > 0)
		{
			conformance.Witnesses[requirement] = new FunctionWitness(matches[0].Symbol, matches[0]);
			ReportNarrowWitness(trait, self, "new", matches[0].Symbol);
			return;
		}
		
		var expected = _typePool.SubstituteSignature(signature, traitMap);
		var hidesConstruction = declared.Count > 0 && self is RecordSymbol;
		if (!hidesConstruction && _typePool.FindConstructionWitness(self, expected) is { } construction)
		{
			conformance.Witnesses[requirement] = construction;
			return;
		}
		
		if (_typePool.FindConversionWitness(self, expected) is { } conversion)
		{
			conformance.Witnesses[requirement] = conversion;
			return;
		}
		
		if (!hidesConstruction)
		{
			ReportConformance(conformance.Location, $"'{self.Name}' needs '{trait}.new'");
			return;
		}
		
		foreach (var constructor in declared)
			ReportConformance(constructor.Symbol.Syntax is ConstructorNode node
					? node.Keyword.SourceLocation
					: conformance.Location, $"'new' doesn't match '{trait}.new'");
	}
	
	private static IEnumerable<(string Name, FunctionSymbol Function)> GetRequirements(TraitSymbol trait) =>
	[
		..trait.Functions
			.Where(static method => method.Function.Kind != FunctionKind.Destructor &&
			                        method.Function.Visibility != Visibility.Private)
			.Select(static method => (method.Name, method.Function)),
		..trait.Properties.SelectMany(static property => new[] { property.Getter, property.Setter }
			.OfType<FunctionAccessor>()
			.Where(static accessor => accessor.Function.Visibility != Visibility.Private)
			.Select(accessor => (property.Name, accessor.Function)))
	];
	
	private static string GetAccessorKind(FunctionSymbol accessor) => ((FunctionNode)accessor.Syntax).Identifier.Text;
	
	private IEnumerable<(FunctionSymbol Function, FunctionSignature Signature)> FindCandidates(Conformance conformance,
		TypeSymbol self, string name, FunctionSymbol requirement)
	{
		var impl = conformance.Impl;
		IEnumerable<FunctionSymbol> blockMembers = [];
		IEnumerable<FunctionSymbol> typeMembers = [];
		if (requirement.Property is { } property)
		{
			var kind = GetAccessorKind(requirement);
			if (impl?.Properties.FirstOrDefault(p => p.Name == property.Name) is { } blockProperty)
				blockMembers = FindAccessor(blockProperty, kind);
			
			if (conformance.Target.GetProperty(property.Name) is { } typeProperty)
				typeMembers = FindAccessor(typeProperty, kind);
		}
		else
		{
			var memberName = name;
			if (impl is not null)
				blockMembers = impl.Functions.Where(m => m.Name == memberName).Select(static m => m.Function);
			
			typeMembers = conformance.Target.GetFunctions(memberName).Select(static m => m.Function);
		}
		
		foreach (var member in blockMembers)
			yield return (member, GetFunctionInfo(member).Signature);
		
		var map = conformance.Target is NamedTypeSymbol { IsGenericDefinition: true } definition && impl is not null
			? TypePool.CreateMap(definition.TypeParameters, self.TypeArguments)
			: [];
		
		foreach (var member in typeMembers)
		{
			var signature = GetFunctionInfo(member).Signature;
			yield return (member, map.Count == 0 ? signature : _typePool.SubstituteSignature(signature, map));
		}
	}
	
	private static IEnumerable<FunctionSymbol> FindAccessor(PropertySymbol property, string kind) =>
		new[] { property.Getter, property.Setter }
			.OfType<FunctionAccessor>()
			.Select(static accessor => accessor.Function)
			.Where(function => GetAccessorKind(function) == kind);
	
	private bool SignaturesMatch(FunctionSignature candidate, FunctionSignature requirement,
		FunctionSymbol requirementSymbol, FunctionSymbol candidateSymbol,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, bool allowsStandIns = false)
	{
		var declared = requirementSymbol.DeclaredTypeParameters;
		var own = candidateSymbol.DeclaredTypeParameters;
		if (declared.Length != own.Length)
			return false;
		
		var map = new Dictionary<TypeParameterSymbol, TypeSymbol>(traitMap);
		for (var i = 0; i < declared.Length; i++)
			map[declared[i]] = own[i];
		
		var expected = _typePool.SubstituteSignature(requirement, map);
		return expected.IsVariadic == candidate.IsVariadic &&
		       (expected.ReturnType == candidate.ReturnType ||
		        allowsStandIns && FunctionType.CanAdapt(candidate.ReturnType, expected.ReturnType)) &&
		       TypePool.ParametersMatch(candidate, expected, allowsStandIns);
	}
	
	private NativeWitness? FindNativeWitness(FunctionSymbol requirement, FunctionSignature signature,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, TypeSymbol self)
	{
		if (self is not (PrimitiveType or EnumSymbol) || requirement.Syntax is not FunctionNode node ||
		    node.Identifier.Type == TokenType.Identifier || IsConversion(node))
			return null;
		
		var expected = _typePool.SubstituteSignature(signature, traitMap);
		
		var registry = _typePool.OperatorRegistry;
		if (node.Receiver is not null && expected.ParameterTypes.Length == 2)
			return expected.GetMode(0) == ParameterMode.Mut && registry.GetBinaryCandidates(node.Identifier.Type)
				.OfType<NativeImpl>()
				.FirstOrDefault(candidate => candidate.ReturnType == self && candidate.ParameterTypes[0] == self &&
				                             candidate.ParameterTypes[1] == expected.ParameterTypes[1]) is { } compound
				? new NativeWitness(compound, true)
				: null;
		
		var candidates = node.Receiver is null
			? registry.GetBinaryCandidates(node.Identifier.Type)
			: registry.GetUnaryCandidates(node.Identifier.Type);
		
		return candidates.OfType<NativeImpl>().FirstOrDefault(candidate =>
			candidate.ReturnType == expected.ReturnType &&
			candidate.ParameterTypes.SequenceEqual(expected.ParameterTypes)) is { } native
			? new NativeWitness(native)
			: null;
	}
	
	private static SourceLocation GetFunctionLocation(FunctionSymbol function) => function.Syntax switch
	{
		FunctionNode node => node.Identifier.SourceLocation,
		_ => function.Syntax.SourceLocation
	};
	
	private void ReportUnmatchedMembers(ImplSymbol impl)
	{
		var traits = _localConformances.Where(c => c.Impl == impl).Select(c => $"'{DescribeTrait(c)}'").ToList();
		if (traits.Count == 0)
			return;
		
		var members = impl.Functions.Select(static method => (method.Name, method.Function)).Concat(impl.Properties
			.SelectMany(static property => new[] { property.Getter, property.Setter }
				.OfType<FunctionAccessor>()
				.Select(accessor => (property.Name, accessor.Function))));
		
		foreach (var (name, function) in members)
		{
			if (function.Visibility == Visibility.Private || function.Kind == FunctionKind.Destructor ||
			    _witnessMembers.Contains(function))
				continue;
			
			Diagnostics.Add(new(DiagnosticSeverity.Error, GetFunctionLocation(function),
				$"'{name}' isn't a member of {string.Join(" or ", traits)}"));
		}
	}
	
	private bool DeferConstraintCheck(ConstraintCheck check)
	{
		if (!MentionsPending(check.Argument))
			return false;
		
		_deferredChecks.Add(check);
		return true;
	}
	
	private bool MentionsPending(TypeSymbol type) => type switch
	{
		NamedTypeSymbol named => _inProgress.Any(entry => entry.Symbol == named.Definition) ||
		                         named.TypeArguments.Any(MentionsPending),
		PointerType pointer => MentionsPending(pointer.BaseType),
		BorrowType borrow => MentionsPending(borrow.Target),
		ArrayType array => MentionsPending(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(MentionsPending),
		_ => false
	};
	
	private void ReportDeferredChecks()
	{
		foreach (var check in _deferredChecks)
		{
			if (_typePool.FindConstraintViolation(check.Parameter, check.Argument, check.Map) is { } message)
				Diagnostics.Add(new(DiagnosticSeverity.Error, check.Location, message));
		}
		
		_deferredChecks.Clear();
	}
	
	private void CheckMemberBlocks()
	{
		var blocks = _impls.Where(impl => ((ImplNode)_declarations[impl].Node).Traits.IsEmpty).ToList();
		foreach (var impl in blocks)
			CheckMemberBlock(impl, (ImplNode)_declarations[impl].Node);
		
		foreach (var sameType in blocks.GroupBy(impl => GetImplTarget(impl).OriginalDefinition))
			ReportMemberBlockConflicts(sameType.Key, [..sameType]);
	}
	
	private void CheckMemberBlock(ImplSymbol impl, ImplNode node)
	{
		var target = GetImplTarget(impl).OriginalDefinition;
		if (target is InvalidType)
			return;
		
		if (!IsLocal(target))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Target.SourceLocation,
				$"'{target.Name}' is declared in another project"));
			
			return;
		}
		
		var declared = target is NamedTypeSymbol named ? named.TypeParameters : [];
		var map = TypePool.CreateMap(declared, [..impl.TypeParameters]);
		var violations = declared
			.Select((parameter, i) => (Index: i,
				Message: _typePool.FindConstraintViolation(parameter, impl.TypeParameters[i], map)))
			.Where(static violation => violation.Message is not null)
			.ToList();
		
		foreach (var (index, message) in violations)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.TypeParameters[index].SourceLocation, message!));
		
		if (violations.Count == 0 &&
		    !declared.Where((parameter, i) => AddsBound(parameter, impl.TypeParameters[i], map)).Any())
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Keyword.SourceLocation,
				"Cannot declare 'impl' blocks that add no bounds"));
	}
	
	private bool AddsBound(TypeParameterSymbol declared, TypeParameterSymbol block,
		Dictionary<TypeParameterSymbol, TypeSymbol> map) =>
		block.IsNoref && !declared.IsNoref || block.HasNull && !declared.HasNull || block.IsCopy && !declared.IsCopy ||
		block.HasDrop && !declared.HasDrop || block.HasNew && !declared.HasNew ||
		block.IsAtomic && !declared.IsAtomic ||
		_typePool.GetConstructorBounds(block).Any(constructor => !_typePool.GetConstructors(declared)
			.Any(existing => TypePool.MatchesConstructor(_typePool.SubstituteSignature(existing.Signature, map),
				constructor.Signature))) ||
		_typePool.GetBounds(block)
			.Except(_typePool.GetBounds(declared).Select(bound => _typePool.SubstituteTrait(bound, map)))
			.Any() ||
		_typePool.GetParameterBounds(block).Cast<TypeSymbol>()
			.Except(_typePool.GetParameterBounds(declared).Select(bound => map[bound]))
			.Any();
	
	private void ReportMemberBlockConflicts(TypeSymbol target, IReadOnlyList<ImplSymbol> blocks)
	{
		if (!_declarations.TryGetValue(target, out var declaration))
			return;
		
		var declared = target is NamedTypeSymbol named ? named.TypeParameters : [];
		var members = GetBodyMembers(declaration.Node)
			.Select(static member => (member.Name, member.Function, Block: (ImplSymbol?)null))
			.Concat(blocks.SelectMany(impl => GetBlockMembers(impl)
				.Select(member => (member.Name, member.Function, Block: (ImplSymbol?)impl))))
			.ToList();
		
		foreach (var sameName in members.GroupBy(static member => member.Name.Text))
		{
			if (sameName.All(static member => member.Block is null))
				continue;
			
			foreach (var member in sameName)
			{
				var others = sameName.Where(other => other.Block != member.Block).ToList();
				if (others.Count == 0)
					continue;
				
				var message = member.Function is not { } function || others.Any(static other => other.Function is null)
					? $"'{member.Name.Text}' is declared more than once in '{target.Name}'"
					: DescribeConflict(member.Name.Text, GetDeclaredSignature(function, member.Block, declared),
						others.Select(other => GetDeclaredSignature(other.Function!, other.Block, declared)));
				
				if (message is not null)
					Diagnostics.Add(new(DiagnosticSeverity.Error, member.Name.SourceLocation, message));
			}
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
	
	private (Token Name, FunctionSymbol? Function)? GetNamedMember(IDeclarationNode member) => member switch
	{
		FieldNode field => (field.Identifier, null),
		GlobalNode global => (global.Identifier, null),
		PropertyNode property => (property.Identifier, null),
		FunctionNode function when !IsModeOperator(function) =>
			(function.Identifier, (FunctionSymbol)_symbolTable.DeclarationSymbols[function]),
		_ => null
	};
	
	private FunctionSignature GetDeclaredSignature(FunctionSymbol function, ImplSymbol? block,
		ImmutableArray<TypeParameterSymbol> declared)
	{
		var signature = GetFunctionInfo(function).Signature;
		return block is null
			? signature
			: _typePool.SubstituteSignature(signature, TypePool.CreateMap(block.TypeParameters, [..declared]));
	}
	
	private void CompleteRecord(RecordSymbol record)
	{
		if (_completedTypes.Contains(record) || !_declarations.TryGetValue(record, out var declaration) ||
		    !Enter(record, false))
			return;
		
		var node = (RecordNode)declaration.Node;
		var destructors = node.Members.OfType<DestructorNode>().Select(static d => d.Keyword);
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(destructors,
			name => $"'{name}' is declared more than once in '{record.Name}'"));
		
		ReportTypeParameters(node.TypeParameters, node.Members);
		
		var storedTypes = new List<(ITypeNode Node, TypeSymbol Type)>();
		foreach (var member in node.Members)
		{
			var symbol = _symbolTable.DeclarationSymbols[member];
			var context = _declarations[symbol].Context;
			
			switch (member)
			{
				case FieldNode field:
					var fieldSymbol = (FieldSymbol)symbol;
					var fieldType = RejectValueDyn(field.Type, context.ResolveType(field.Type));
					_typePool.RegisterMember(record, fieldSymbol, fieldType);
					if (fieldSymbol.IsRequired)
						_requiredFields.Add((field, fieldType));
					
					ReportHiddenType(field.Type, fieldType, GetEffectiveVisibility(fieldSymbol.Visibility, record),
						field.Identifier.Text);
					
					storedTypes.Add((field.Type, fieldType));
					break;
				
				case ConstructorNode constructor:
					CollectConstructor((FunctionSymbol)symbol, constructor, context);
					break;
				
				case DestructorNode:
					CollectDestructor((FunctionSymbol)symbol, context);
					break;
				
				case GlobalNode:
					break;
				
				default:
					Complete(symbol);
					break;
			}
		}
		
		var functions = node.Members.OfType<FunctionNode>().ToLookup(IsModeOperator);
		ReportMemberConflicts(record.Name, [
			..node.Members.OfType<FieldNode>()
				.Select(static f => (f.Identifier, (FunctionNode?)null, MemberKind.Field)),
			..node.Members.OfType<GlobalNode>()
				.Select(static g => (g.Identifier, (FunctionNode?)null, MemberKind.Field)),
			..node.Members.OfType<PropertyNode>()
				.Select(static p => (p.Identifier, (FunctionNode?)null, MemberKind.Property)),
			..functions[false].Select(static f => (f.Identifier, (FunctionNode?)f, MemberKind.Function))
		]);
		
		ReportConstructorConflicts(node.Members);
		ReportModeOperators(record, functions[true]);
		ReportOperators(record, functions[false]);
		_typePool.RegisterRecord(record);
		_completedTypes.Add(record);
		Exit();
		
		foreach (var (typeNode, type) in storedTypes)
		{
			if (!node.IsRef && !TypePool.ContainsTypeParameters(type) && _typePool.HoldsBorrows(type))
				Diagnostics.Add(ReportStoredBorrow(typeNode, node.Identifier, node.Modifiers, "records", "rec"));
		}
		
		foreach (var field in node.Members.OfType<GlobalNode>())
			Complete(_symbolTable.DeclarationSymbols[field]);
	}
	
	private void ReportTypeParameters(ImmutableArray<TypeParameterNode> typeParameters,
		IEnumerable<IDeclarationNode> members)
	{
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(typeParameters.Select(static p => p.Identifier),
			static name => $"Type parameter '{name}' is declared more than once"));
		
		var duplicates = typeParameters
			.GroupBy(static p => p.Identifier.Text)
			.Where(static sameName => sameName.Count() > 1)
			.Select(static sameName => sameName.Key)
			.ToHashSet();
		
		var reported = new HashSet<string>();
		foreach (var parameter in members.OfType<FunctionNode>().SelectMany(static f => f.TypeParameters))
		{
			var name = parameter.Identifier.Text;
			if (typeParameters.Where(p => p.Identifier.Text == name).ToList() is not [_, ..] outer)
				continue;
			
			Diagnostics.Add(ReportDuplicateTypeParameter(parameter.Identifier));
			if (!duplicates.Contains(name) && reported.Add(name))
				Diagnostics.AddRange(outer.Select(static p => ReportDuplicateTypeParameter(p.Identifier)));
		}
	}
	
	private static Diagnostic ReportDuplicateTypeParameter(Token identifier) => new(DiagnosticSeverity.Error,
		identifier.SourceLocation, $"Type parameter '{identifier.Text}' is declared more than once");
	
	private static bool IsDereference(FunctionNode node) =>
		node is { Identifier.Type: TokenType.OpStar, Receiver: not null };
	
	private static bool IsIndexer(FunctionNode node) => node.Identifier.Type == TokenType.OpOpenBracket;
	
	private static bool IsLoopOperator(FunctionNode node) =>
		node.Identifier.Type is TokenType.KeywordIn or TokenType.KeywordFor;
	
	private static bool IsConversion(FunctionNode node) =>
		node.Identifier.Type is TokenType.KeywordAs or TokenType.KeywordNew;
	
	private static bool IsModeOperator(FunctionNode node) =>
		IsDereference(node) || IsIndexer(node) || IsLoopOperator(node) || IsConversion(node);
	
	private void ReportModeOperators(TypeSymbol type, IEnumerable<FunctionNode> operators)
	{
		var kinds = operators.ToLookup(static o => o.Identifier.Type);
		ReportDereferences(type, [..kinds[TokenType.OpStar]]);
		ReportIndexers(type, [..kinds[TokenType.OpOpenBracket]]);
		ReportLoopOperators(type, [..kinds[TokenType.KeywordIn], ..kinds[TokenType.KeywordFor]]);
		ReportConversions(type, [..kinds[TokenType.KeywordAs], ..kinds[TokenType.KeywordNew]]);
	}
	
	private void ReportConversions(TypeSymbol type, List<FunctionNode> conversions)
	{
		var outward = new List<(FunctionNode Node, FunctionSignature Signature)>();
		var inward = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var conversion in conversions)
		{
			var info = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[conversion]];
			var signature = info.Signature;
			if (signature.ReturnType is InvalidType || signature.ParameterTypes.Any(static p => p is InvalidType))
				continue;
			
			if (FindConversionError(type, conversion, signature) is var (location, message))
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
				continue;
			}
			
			(conversion.Receiver is null ? inward : outward).Add((conversion, signature));
			if (conversion.Identifier.Type == TokenType.KeywordNew &&
			    type is (RecordSymbol or EnumSymbol) and not NamedTypeSymbol { IsGenericInstance: true })
				_typePool.AddExplicitConversion(type, info);
		}
		
		foreach (var sameTarget in outward.GroupBy(static c => GetIndexedType(c.Signature)))
		{
			if (sameTarget.Select(static c => c.Node.Identifier.Type).Distinct().Count() > 1)
			{
				var message = $"Cannot declare both 'as' and 'new' conversions to '{sameTarget.Key.Name}'";
				Diagnostics.AddRange(sameTarget.Select(c =>
					new Diagnostic(DiagnosticSeverity.Error, c.Node.Identifier.SourceLocation, message)));
				
				continue;
			}
			
			var sameModes = sameTarget.GroupBy(static c => c.Signature.GetMode(0)).Where(static g => g.Count() > 1);
			foreach (var sameMode in sameModes)
			{
				var receiver = DescribeConversionReceiver(sameMode.Key);
				var message = $"Conversion to '{sameTarget.Key.Name}' with '{receiver}' is declared more than " +
				              $"once in '{type.Name}'";
				
				Diagnostics.AddRange(sameMode.Select(c =>
					new Diagnostic(DiagnosticSeverity.Error, c.Node.Identifier.SourceLocation, message)));
			}
		}
		
		var sameSources = inward.GroupBy(static c => c.Signature.ParameterTypes[0]).Where(static g => g.Count() > 1);
		foreach (var sameSource in sameSources)
		{
			var message = $"Conversion from '{sameSource.Key.Name}' is declared more than once in '{type.Name}'";
			Diagnostics.AddRange(sameSource.Select(c =>
				new Diagnostic(DiagnosticSeverity.Error, c.Node.Identifier.SourceLocation, message)));
		}
	}
	
	private static (SourceLocation Location, string Message)? FindConversionError(TypeSymbol type, FunctionNode node,
		FunctionSignature signature)
	{
		var name = node.Identifier.Text;
		var result = signature.ReturnType;
		var resultLocation = node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation;
		if (node.Receiver is null)
		{
			return node switch
			{
				{ Parameters.Length: not 1 } =>
					(node.Identifier.SourceLocation, $"'{name}' operators need 'self' or one parameter"),
				{ Parameters: [{ Mode.Type: TokenType.KeywordMut } parameter] } =>
					(parameter.SourceLocation, $"Cannot take 'mut' parameters in '{name}' operators"),
				{ ReturnType: null } => (node.Identifier.SourceLocation, $"'{name}' operators need a return type"),
				_ when result != type =>
					(resultLocation, $"Cannot return '{result.Name}' from '{name}' operators without 'self'"),
				_ when signature.ParameterTypes[0] == type =>
					(node.Parameters[0].SourceLocation, $"Cannot convert '{type.Name}' to itself"),
				_ => null
			};
		}
		
		var mode = signature.GetMode(0);
		return node switch
		{
			{ Parameters: [var parameter, ..] } =>
				(parameter.SourceLocation, $"Cannot take parameters in '{name}' operators with 'self'"),
			{ ReturnType: null } => (node.Identifier.SourceLocation, $"'{name}' operators need a return type"),
			_ when result is BorrowType { IsMutable: var isMutable } && (isMutable != (mode == ParameterMode.Mut) ||
			                                                             mode == ParameterMode.Own) ||
			       result is not BorrowType && mode == ParameterMode.Mut =>
				(resultLocation,
					$"Cannot return '{result.Name}' from '{name}' with '{DescribeConversionReceiver(mode)}'"),
			_ when GetIndexedType(signature) == type => (resultLocation, $"Cannot convert '{type.Name}' to itself"),
			_ => null
		};
	}
	
	private static string DescribeConversionReceiver(ParameterMode mode) => mode switch
	{
		ParameterMode.Mut => "mut self",
		ParameterMode.Own => "own self",
		_ => "self"
	};
	
	private void ReportLoopOperators(TypeSymbol type, List<FunctionNode> operators)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var node in operators)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[node]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindLoopOperatorError(node, signature) is var (location, message))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((node, signature));
		}
		
		var sameModes = valid
			.GroupBy(static o => (o.Node.Identifier.Text, o.Signature.GetMode(0)))
			.Where(static g => g.Count() > 1);
		
		foreach (var sameMode in sameModes)
		{
			var (name, mode) = sameMode.Key;
			var message = $"'{name}' with '{DescribeReceiver(mode)}' is declared more than once in '{type.Name}'";
			Diagnostics.AddRange(sameMode.Select(o =>
				new Diagnostic(DiagnosticSeverity.Error, o.Node.Identifier.SourceLocation, message)));
		}
	}
	
	private static (SourceLocation Location, string Message)? FindLoopOperatorError(FunctionNode node,
		FunctionSignature signature)
	{
		var name = node.Identifier.Text;
		var isStep = node.Identifier.Type == TokenType.KeywordFor;
		return node switch
		{
			{ Receiver: null } => (node.Identifier.SourceLocation,
				$"Cannot declare '{name}' operators without '{(isStep ? "mut self" : "self")}'"),
			{ Receiver: { Mode.Type: TokenType.KeywordOwn } receiver } =>
				(receiver.SourceLocation, $"Cannot take 'own self' in '{name}' operators"),
			{ Receiver: { Mode: null } receiver } when isStep =>
				(receiver.SourceLocation, "Cannot take 'self' in 'for' operators"),
			{ Parameters: [var parameter, ..] } =>
				(parameter.SourceLocation, $"Cannot take parameters in '{name}' operators"),
			_ when !IsLoopOperatorResult(isStep, signature.ReturnType) =>
				(node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
					$"Cannot return '{signature.ReturnType.Name}' from '{name}' operators"),
			_ => null
		};
	}
	
	private static bool IsLoopOperatorResult(bool isStep, TypeSymbol type) => isStep
		? type == NativeSymbols.Bool
		: type != NativeSymbols.Void && type is not BorrowType;
	
	private void ReportIndexers(TypeSymbol type, List<FunctionNode> indexers)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var indexer in indexers)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[indexer]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindIndexerError(indexer, signature) is var (location, message))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((indexer, signature));
		}
		
		var sameModes = valid.GroupBy(static i => i.Signature.GetMode(0)).Where(static g => g.Count() > 1).ToList();
		foreach (var sameMode in sameModes)
		{
			var message = $"'[]' with '{DescribeReceiver(sameMode.Key)}' is declared more than once in '{type.Name}'";
			Diagnostics.AddRange(sameMode.Select(i =>
				new Diagnostic(DiagnosticSeverity.Error, i.Node.Identifier.SourceLocation, message)));
		}
		
		if (sameModes.Count > 0 || valid is not [var first, var second])
			return;
		
		if (!HaveSameIndices(first.Signature, second.Signature))
			Diagnostics.AddRange(valid.Select(static i => new Diagnostic(DiagnosticSeverity.Error,
				i.Node.Identifier.SourceLocation, "Cannot declare '[]' operators with different parameters")));
		
		if (GetIndexedType(first.Signature) != GetIndexedType(second.Signature))
			Diagnostics.AddRange(valid.Select(static i => new Diagnostic(DiagnosticSeverity.Error,
				i.Node.ReturnType!.SourceLocation, "Cannot declare '[]' operators with different target types")));
	}
	
	private static TypeSymbol GetIndexedType(FunctionSignature signature) =>
		signature.ReturnType is BorrowType borrow ? borrow.Target : signature.ReturnType;
	
	private static bool IsIndexerResult(FunctionSignature signature) => signature.ReturnType switch
	{
		BorrowType { IsMutable: var isMutable } => isMutable == (signature.GetMode(0) == ParameterMode.Mut),
		var type => signature.GetMode(0) != ParameterMode.Mut && type != NativeSymbols.Void
	};
	
	private static bool HaveSameIndices(FunctionSignature first, FunctionSignature second) =>
		first.ParameterTypes.Length == second.ParameterTypes.Length &&
		Enumerable.Range(1, first.ParameterTypes.Length - 1).All(i =>
			first.GetDeclaredType(i) == second.GetDeclaredType(i) && first.GetMode(i) == second.GetMode(i));
	
	private static (SourceLocation Location, string Message)? FindIndexerError(FunctionNode node,
		FunctionSignature signature) => node switch
	{
		{ Receiver: null } => (node.Identifier.SourceLocation, "Cannot declare '[]' operators without 'self'"),
		{ Receiver: { Mode: { Type: TokenType.KeywordOwn } } receiver } =>
			(receiver.SourceLocation, "Cannot take 'own self' in '[]' operators"),
		{ Parameters: [] } => (node.Identifier.SourceLocation, "'[]' operators need at least one parameter"),
		_ when node.Parameters.FirstOrDefault(static p => p.Mode?.Type == TokenType.KeywordMut) is { } parameter =>
			(parameter.SourceLocation, "Cannot take 'mut' parameters in '[]' operators"),
		_ when IsIndexerResult(signature) => null,
		_ => (node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
			$"Cannot return '{signature.ReturnType.Name}' from '[]' with '{DescribeReceiver(signature.GetMode(0))}'")
	};
	
	private void ReportDereferences(TypeSymbol type, List<FunctionNode> dereferences)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var dereference in dereferences)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[dereference]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindDereferenceError(dereference, signature) is var (location, message))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((dereference, signature));
		}
		
		foreach (var sameMode in valid.GroupBy(static d => d.Signature.GetMode(0)).Where(static g => g.Count() > 1))
		{
			var message = $"'*' with '{DescribeReceiver(sameMode.Key)}' is declared more than once in '{type.Name}'";
			Diagnostics.AddRange(sameMode.Select(d =>
				new Diagnostic(DiagnosticSeverity.Error, d.Node.Identifier.SourceLocation, message)));
		}
		
		if (valid.Select(static d => ((BorrowType)d.Signature.ReturnType).Target).Distinct().Count() > 1)
			Diagnostics.AddRange(valid.Select(static d => new Diagnostic(DiagnosticSeverity.Error,
				d.Node.ReturnType!.SourceLocation, "Cannot declare '*' operators with different target types")));
	}
	
	private static (SourceLocation Location, string Message)? FindDereferenceError(FunctionNode node,
		FunctionSignature signature) => node switch
	{
		{ Receiver: { Mode: { Type: TokenType.KeywordOwn } } receiver } =>
			(receiver.SourceLocation, "Cannot take 'own self' in '*' operators"),
		{ Parameters: [var parameter, ..] } =>
			(parameter.SourceLocation, "Cannot take parameters in '*' operators with 'self'"),
		_ when signature.ReturnType is BorrowType { IsMutable: var isMutable } &&
		       isMutable == (signature.GetMode(0) == ParameterMode.Mut) => null,
		_ => (node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
			$"Cannot return '{signature.ReturnType.Name}' from '*' with '{DescribeReceiver(signature.GetMode(0))}'")
	};
	
	private static string DescribeReceiver(ParameterMode mode) => mode == ParameterMode.Mut ? "mut self" : "self";
	
	private static string DescribeReceiver(ReceiverNode receiver) =>
		receiver.Mode is { } mode ? $"{mode.Text} self" : "self";
	
	private void ReportOperators(TypeSymbol type, IEnumerable<FunctionNode> functions)
	{
		foreach (var node in functions.Where(static f => f.Identifier.Type != TokenType.Identifier))
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[node]].Signature;
			if (FindOperatorError(type, node, signature) is var (location, message))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
		}
	}
	
	private static (SourceLocation Location, string Message)? FindOperatorError(TypeSymbol type, FunctionNode node,
		FunctionSignature signature)
	{
		var name = node.Identifier.Text;
		if (_compoundOperators.Contains(node.Identifier.Type))
			return FindCompoundOperatorError(node);
		
		if (node.Receiver is { } receiver)
		{
			if (!_unaryOperators.Contains(node.Identifier.Type))
				return (receiver.SourceLocation, $"Cannot take '{DescribeReceiver(receiver)}' in '{name}' operators");
			
			if (node.Parameters is [var parameter, ..])
				return (parameter.SourceLocation, $"Cannot take parameters in '{name}' operators with 'self'");
			
			return receiver.Mode?.Type == TokenType.KeywordMut
				? (receiver.SourceLocation, $"Cannot take 'mut self' in '{name}' operators")
				: null;
		}
		
		if (node.Identifier.Type == TokenType.OpTilde)
			return (node.Identifier.SourceLocation, $"Cannot declare '{name}' operators without 'self'");
		
		if (node.Parameters.Length != 2)
			return (node.Identifier.SourceLocation,
				_unaryOperators.Contains(node.Identifier.Type) || node.Identifier.Type == TokenType.OpStar
					? $"'{name}' operators need 'self' or two parameters"
					: $"'{name}' operators need two parameters");
		
		if (node.Parameters.FirstOrDefault(static p => p.Mode?.Type == TokenType.KeywordMut) is { } mutParameter)
			return (mutParameter.SourceLocation, $"Cannot take 'mut' parameters in '{name}' operators");
		
		return signature.GetDeclaredType(0) == type || signature.GetDeclaredType(1) == type
			? null
			: (node.Identifier.SourceLocation, $"'{name}' operators need a '{type.Name}' parameter");
	}
	
	private static (SourceLocation Location, string Message)? FindCompoundOperatorError(FunctionNode node)
	{
		var name = node.Identifier.Text;
		return node switch
		{
			{ Receiver: null } =>
				(node.Identifier.SourceLocation, $"Cannot declare '{name}' operators without 'mut self'"),
			{ Receiver: { } receiver } when receiver.Mode?.Type != TokenType.KeywordMut =>
				(receiver.SourceLocation, $"Cannot take '{DescribeReceiver(receiver)}' in '{name}' operators"),
			{ Parameters.Length: not 1 } => (node.Identifier.SourceLocation, $"'{name}' operators need one parameter"),
			{ ReturnType: { } returnType } =>
				(returnType.SourceLocation, $"Cannot return values from '{name}' operators"),
			{ Parameters: [{ Mode.Type: TokenType.KeywordMut } parameter] } =>
				(parameter.SourceLocation, $"Cannot take 'mut' parameters in '{name}' operators"),
			_ => null
		};
	}
	
	private void ReportMemberConflicts(string owner,
		IEnumerable<(Token Name, FunctionNode? Function, MemberKind Kind)> members)
	{
		var sameNames = members
			.GroupBy(static member => member.Name.Text)
			.Where(static sameName => sameName.Count() > 1);
		
		foreach (var sameName in sameNames)
		{
			if (sameName.All(static member => member.Kind == MemberKind.Function))
			{
				var functions = sameName.Select(static member => member.Function!).ToList();
				Diagnostics.AddRange(functions
					.Select(function => FindConflict(function, functions))
					.OfType<Diagnostic>());
				
				continue;
			}
			
			if (sameName.All(static member => member.Kind == MemberKind.Case))
				continue;
			
			var message = sameName.All(static member => member.Kind == MemberKind.Field)
				? $"Field '{sameName.Key}' is declared more than once in '{owner}'"
				: $"'{sameName.Key}' is declared more than once in '{owner}'";
			
			Diagnostics.AddRange(sameName.Select(member =>
				new Diagnostic(DiagnosticSeverity.Error, member.Name.SourceLocation, message)));
		}
	}
	
	private void ReportConstructorConflicts(ImmutableArray<IDeclarationNode> members)
	{
		List<IDeclarationNode> constructors = [..members.OfType<ConstructorNode>()];
		Diagnostics.AddRange(constructors
			.Select(constructor => FindConflict(constructor, constructors))
			.OfType<Diagnostic>());
	}
	
	private void CompleteEnum(EnumSymbol enumType)
	{
		if (_completedTypes.Contains(enumType) || !_declarations.TryGetValue(enumType, out var declaration) ||
		    !Enter(enumType, false))
			return;
		
		var node = enumType.Node;
		var context = declaration.Context;
		var memberContext = context with { ContainingType = enumType };
		if (node.Cases.IsEmpty)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs at least one case"));
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Cases.Select(static c => c.Identifier),
			name => $"Case '{name}' is declared more than once in '{enumType.Name}'"));
		
		ReportTypeParameters(node.TypeParameters, node.Members);
		
		var matchedType = node.MatchedType is { } matchedNode
			? ResolveMatchedType(enumType, matchedNode, memberContext)
			: null;
		
		var payloads = new Dictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>>();
		foreach (var enumCase in enumType.Cases)
		{
			Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(
				enumCase.Node.Payload.Select(static f => f.Identifier),
				name => $"Payload '{name}' is declared more than once in '{enumCase.Name}'"));
			
			if (matchedType is not null)
			{
				foreach (var field in enumCase.Fields)
					_typePool.RegisterPayloadField(field, matchedType);
				
				continue;
			}
			
			ImmutableArray<TypeSymbol> types =
			[
				..enumCase.Fields.Select(f => RejectValueDyn(f.Node!.Type, memberContext.ResolveType(f.Node!.Type)))
			];
			
			for (var i = 0; i < types.Length; i++)
			{
				_typePool.RegisterPayloadField(enumCase.Fields[i], types[i]);
				ReportHiddenType(enumCase.Fields[i].Node!.Type, types[i], enumType.Visibility, enumType.Name);
				if (!node.IsRef && !TypePool.ContainsTypeParameters(types[i]) && _typePool.HoldsBorrows(types[i]))
					Diagnostics.Add(ReportStoredBorrow(enumCase.Fields[i].Node!.Type, node.Identifier, node.Modifiers,
						"enums", "enum"));
			}
			
			payloads[enumCase] = types;
		}
		
		if (matchedType is null)
			CompletePlainEnum(enumType, context, payloads);
		else
			CompleteMatchEnum(enumType, matchedType, context);
		
		_completedTypes.Add(enumType);
		Exit();
		
		foreach (var member in node.Members)
			Complete(_symbolTable.DeclarationSymbols[member]);
		
		var functions = node.Members.OfType<FunctionNode>().ToLookup(IsModeOperator);
		ReportMemberConflicts(enumType.Name, [
			..node.Cases.Select(static c => (c.Identifier, (FunctionNode?)null, MemberKind.Case)),
			..node.Members.OfType<GlobalNode>()
				.Select(static g => (g.Identifier, (FunctionNode?)null, MemberKind.Field)),
			..node.Members.OfType<PropertyNode>()
				.Select(static p => (p.Identifier, (FunctionNode?)null, MemberKind.Property)),
			..functions[false].Select(static f => (f.Identifier, (FunctionNode?)f, MemberKind.Function))
		]);
		
		ReportModeOperators(enumType, functions[true]);
		ReportOperators(enumType, functions[false]);
	}
	
	private void CompletePlainEnum(EnumSymbol enumType, ResolutionContext context,
		IReadOnlyDictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>> payloads)
	{
		var node = enumType.Node;
		foreach (var enumCase in node.Cases)
		{
			if (enumCase.Values is [_, var second, ..])
				Diagnostics.Add(new(DiagnosticSeverity.Error, second.SourceLocation,
					"Cannot list several values outside 'enum match'"));
			
			if (enumCase.Else is { } elseKeyword)
				Diagnostics.Add(new(DiagnosticSeverity.Error, elseKeyword.SourceLocation,
					"Cannot use 'else' outside 'enum match'"));
		}
		
		var values = CollectCaseValues(enumType, context);
		var tagType = GetTagType(enumType, context, values);
		ReportSharedValues(enumType, values, payloads);
		if (!enumType.IsExternal && !node.Cases.IsEmpty && !values.Contains(BigInteger.Zero))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs a case with the value 0"));
		
		_typePool.RegisterEnum(enumType, tagType, values);
	}
	
	private TypeSymbol ResolveMatchedType(EnumSymbol enumType, ITypeNode node, ResolutionContext context)
	{
		var type = context.ResolveType(node);
		if (!IsMatchable(type))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot declare 'enum match {type.Name}'"));
			
			return NativeSymbols.Invalid;
		}
		
		ReportHiddenType(node, type, enumType.Visibility, enumType.Name);
		if (!enumType.IsRef && !TypePool.ContainsTypeParameters(type) && _typePool.HoldsBorrows(type))
			Diagnostics.Add(ReportStoredBorrow(node, enumType.Node.Identifier, enumType.Node.Modifiers, "enums",
				"enum match"));
		
		return type;
	}
	
	private static bool IsMatchable(TypeSymbol type) =>
		type is InvalidType or IntegerType or PointerType or FunctionType or BorrowType or TypeParameterSymbol ||
		type == NativeSymbols.Bool || type == NativeSymbols.CStr;
	
	private void CompleteMatchEnum(EnumSymbol enumType, TypeSymbol matchedType, ResolutionContext context)
	{
		var values = new List<ImmutableArray<BigInteger>>();
		var listings = new List<(BigInteger Value, IExpressionNode Node)>();
		foreach (var enumCase in enumType.Cases)
		{
			var node = enumCase.Node;
			if (node.Else is { } elseKeyword && enumCase.Fields.IsEmpty)
				Diagnostics.Add(new(DiagnosticSeverity.Error, elseKeyword.SourceLocation,
					"'else' cases need a payload"));
			else if (node.Else is null && node.Values.IsEmpty)
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
					$"Case '{enumCase.Name}' needs a value"));
			
			var caseValues = new List<BigInteger>();
			foreach (var expression in node.Values)
			{
				if (EvaluateMatchValue(enumCase, expression, matchedType, context) is not { } value)
					continue;
				
				caseValues.Add(value);
				listings.Add((value, expression));
			}
			
			values.Add([..caseValues]);
		}
		
		ReportMatchListings(enumType, matchedType, listings);
		_typePool.RegisterMatchEnum(enumType, matchedType, values);
	}
	
	private BigInteger? EvaluateMatchValue(EnumCaseSymbol enumCase, IExpressionNode expression,
		TypeSymbol matchedType, ResolutionContext context)
	{
		var isNull = expression is LiteralExpressionNode { Token.Type: TokenType.KeywordNull };
		if (matchedType is not (IntegerType or InvalidType) && matchedType != NativeSymbols.Bool)
		{
			if (isNull && TypePool.HasNull(matchedType))
				return BigInteger.Zero;
			
			Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation, isNull
				? $"'{matchedType.Name}' has no null"
				: $"Only 'null' can be listed for '{matchedType.Name}'"));
			
			return null;
		}
		
		if (isNull)
		{
			if (matchedType is not InvalidType)
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"'{matchedType.Name}' has no null"));
			
			return null;
		}
		
		var value = constants!.ResolveInitializer(expression, matchedType, context);
		switch (Evaluator.Evaluate(value))
		{
			case IntegerConstant constant:
				return constant.Value;
			
			case BoolConstant flag:
				return flag.Value ? BigInteger.One : BigInteger.Zero;
			
			case null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"The value of '{enumCase.Name}' must be a constant"));
				
				return null;
			
			default:
				return null;
		}
	}
	
	private void ReportMatchListings(EnumSymbol enumType, TypeSymbol matchedType,
		IReadOnlyList<(BigInteger Value, IExpressionNode Node)> listings)
	{
		foreach (var sameValue in listings.GroupBy(static listing => listing.Value).Where(static g => g.Count() > 1))
		{
			var message = $"{DescribeMatchValue(sameValue.Key, matchedType)} is listed more than once";
			Diagnostics.AddRange(sameValue.Select(listing =>
				new Diagnostic(DiagnosticSeverity.Error, listing.Node.SourceLocation, message)));
		}
		
		var elseKeywords = enumType.Node.Cases.Select(static c => c.Else).OfType<Token>().ToList();
		if (elseKeywords.Count > 1)
			Diagnostics.AddRange(elseKeywords.Select(keyword => new Diagnostic(DiagnosticSeverity.Error,
				keyword.SourceLocation, $"'{enumType.Name}' can have only one 'else' case")));
		else if (elseKeywords.Count == 0 && matchedType is not InvalidType &&
		         !CoversEveryValue(matchedType, listings.Select(static listing => listing.Value).ToHashSet()))
			Diagnostics.Add(new(DiagnosticSeverity.Error, enumType.Node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs an 'else' case"));
	}
	
	private static string DescribeMatchValue(BigInteger value, TypeSymbol matchedType) => matchedType switch
	{
		IntegerType => value.ToString(),
		_ when matchedType == NativeSymbols.Bool => value.IsZero ? "false" : "true",
		_ => "null"
	};
	
	private bool CoversEveryValue(TypeSymbol matchedType, IReadOnlySet<BigInteger> values) => matchedType switch
	{
		IntegerType => values.Count == BigInteger.One << (int)_typePool.SizeTable.GetSize(matchedType)
			.CountBits(_pointerBitSize),
		_ when matchedType == NativeSymbols.Bool => values.Count == 2,
		_ => false
	};
	
	private void CompleteProperty(PropertySymbol property)
	{
		var node = property.Node!;
		var type = node.Type is { } typeNode ? GetPropertyType(property, typeNode) : null;
		if (node.Keyword is not null)
			ReportMissingAccessors(property);
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Accessors.Select(static a => a.Identifier),
			name => $"'{name}' is declared more than once in '{property.Name}'"));
		
		if (node.Accessors.Select(static accessor => accessor.Receiver is null).Distinct().Count() > 1)
			Diagnostics.AddRange(node.Accessors.Select(static accessor => new Diagnostic(DiagnosticSeverity.Error,
				accessor.Identifier.SourceLocation, "Cannot combine static and instance accessors")));
		
		foreach (var accessor in node.Accessors)
		{
			var info = GetFunctionInfo((FunctionSymbol)_symbolTable.DeclarationSymbols[accessor]);
			ReportAccessor(accessor, info.Signature, type, node.Type);
		}
	}
	
	private void ReportMissingAccessors(PropertySymbol property)
	{
		var missing = (property.Getter, property.Setter) switch
		{
			(null, null) => "a getter and a setter",
			(null, _) => "a getter",
			(_, null) => "a setter",
			_ => null
		};
		
		if (missing is not null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, property.Node!.Identifier.SourceLocation,
				$"'{property.Name}' needs {missing}"));
	}
	
	private void ReportAccessor(FunctionNode accessor, FunctionSignature signature, TypeSymbol? type,
		ITypeNode? shared)
	{
		var isGetter = accessor.Identifier.Type == TokenType.KeywordGet;
		if (accessor.Receiver is { Mode: { Type: TokenType.KeywordOwn } } receiver)
			Diagnostics.Add(new(DiagnosticSeverity.Error, receiver.SourceLocation,
				$"Cannot take 'own self' in {(isGetter ? "getters" : "setters")}"));
		
		if (!isGetter && accessor.Parameters is [{ Mode: { Type: TokenType.KeywordMut } mode }])
			Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
				"Cannot take 'mut' parameters in setters"));
		
		if (type is null or InvalidType)
			return;
		
		if (isGetter && accessor.ReturnType is { } returnType && returnType != shared &&
		    signature.ReturnType is not InvalidType && signature.ReturnType != type)
			Diagnostics.Add(new(DiagnosticSeverity.Error, returnType.SourceLocation,
				$"'get' must return '{type.Name}'"));
		
		if (isGetter || accessor.Parameters is not [var value] || value.Type == shared)
			return;
		
		var valueType = signature.GetDeclaredType(signature.ParameterTypes.Length - 1);
		if (valueType is not InvalidType && valueType != type)
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Type.SourceLocation,
				$"'{value.Identifier.Text}' must be '{type.Name}'"));
	}
	
	private TypeSymbol GetPropertyType(PropertySymbol property, ITypeNode typeNode)
	{
		if (_propertyTypes.TryGetValue(property, out var type))
			return type;
		
		var context = _declarations[property].Context;
		var owner = GetVisibilityOwner(context)!;
		type = RejectValueDyn(typeNode, context.ResolveType(typeNode));
		_propertyTypes[property] = type;
		
		var visibility = property.Node!.Accessors
			.Select(accessor => (FunctionSymbol)_symbolTable.DeclarationSymbols[accessor])
			.Select(accessor => GetEffectiveVisibility(accessor.Visibility, owner))
			.DefaultIfEmpty(GetEffectiveVisibility(property.Visibility, owner))
			.Max();
		
		ReportHiddenType(typeNode, type, visibility, property.Name);
		return type;
	}
	
	private TypeSymbol ResolveSignatureType(FunctionSymbol function, ITypeNode node, ResolutionContext context) =>
		function.Property is { Node.Type: { } shared } property && node == shared
			? GetPropertyType(property, shared)
			: context.ResolveType(node);
	
	private TypeSymbol RejectValueDyn(ITypeNode node, TypeSymbol type, ParameterMode mode = ParameterMode.Own)
	{
		if (type is DynType && mode != ParameterMode.Own || TypePool.FindValueDyn(type) is not { } dyn)
			return type;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"Cannot use '{dyn.Name}' by value"));
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol RejectExtDyn(ITypeNode node, TypeSymbol type)
	{
		if (FindDyn(type) is not { } dyn)
			return type;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
			$"Cannot use '{dyn.Name}' in 'ext' signatures"));
		
		return NativeSymbols.Invalid;
	}
	
	private static TypeSymbol? FindDyn(TypeSymbol type) => type switch
	{
		DynType or FStrType => type,
		PointerType pointer => FindDyn(pointer.BaseType),
		BorrowType borrow => FindDyn(borrow.Target),
		ArrayType array => FindDyn(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(FindDyn)
			.FirstOrDefault(static dyn => dyn is not null),
		_ => null
	};
	
	private void ReportHiddenType(ITypeNode node, TypeSymbol type, Visibility visibility, string name)
	{
		if (FindHiddenType(type, visibility) is { } hidden)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"'{hidden.Name}' is less visible than '{name}'"));
	}
	
	private static TypeSymbol? FindHiddenType(TypeSymbol type, Visibility visibility) => type switch
	{
		DynType { Instance: { } trait } dyn =>
			trait.Trait.Visibility < visibility ? dyn : FindHiddenArgument(trait, visibility),
		FStrType { Value.Instance: { } trait } fstr =>
			trait.Trait.Visibility < visibility ? fstr : FindHiddenArgument(trait, visibility),
		TraitType trait => trait.Trait.Visibility < visibility ? trait : FindHiddenArgument(trait, visibility),
		PointerType pointer => FindHiddenType(pointer.BaseType, visibility),
		BorrowType borrow => FindHiddenType(borrow.Target, visibility),
		ArrayType array => FindHiddenType(array.ElementType, visibility),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(part => FindHiddenType(part, visibility))
			.FirstOrDefault(static hidden => hidden is not null),
		NamedTypeSymbol { IsGenericInstance: true } instance => FindHiddenType(instance.Definition, visibility) ??
		                                                        instance.TypeArguments
			                                                        .Select(argument =>
				                                                        FindHiddenType(argument, visibility))
			                                                        .FirstOrDefault(static hidden =>
				                                                        hidden is not null),
		IExportable exportable when exportable.Visibility < visibility => type,
		_ => null
	};
	
	private static TypeSymbol? FindHiddenArgument(TraitType trait, Visibility visibility) => trait.Arguments
		.Select(argument => FindHiddenType(argument, visibility))
		.FirstOrDefault(static hidden => hidden is not null);
	
	private static Visibility GetEffectiveVisibility(Visibility member, Symbol owner)
	{
		var ownerVisibility = ModuleIndex.GetVisibility(owner);
		return member < Visibility.Project && member < ownerVisibility ? member : ownerVisibility;
	}
	
	private static Visibility GetEffectiveVisibility(FunctionSymbol function, ResolutionContext context) =>
		GetVisibilityOwner(context) is { } owner
			? GetEffectiveVisibility(function.Visibility, owner)
			: function.Visibility;
	
	private static Symbol? GetVisibilityOwner(ResolutionContext context) =>
		(Symbol?)context.Trait ?? context.ContainingType;
	
	private static Diagnostic ReportStoredBorrow(ITypeNode type, Token name, IEnumerable<Token> modifiers,
		string kinds, string keyword)
	{
		var prefix = string.Concat(modifiers.Select(static modifier => $"{modifier.Text} "));
		return new(DiagnosticSeverity.Error, type.SourceLocation, $"Cannot store borrows in plain {kinds}")
		{
			Hints = [$"Did you mean '{name.Text}: {prefix}ref {keyword}'?"]
		};
	}
	
	private ImmutableArray<BigInteger> CollectCaseValues(EnumSymbol enumType, ResolutionContext context)
	{
		var scope = new Scope();
		var values = ImmutableArray.CreateBuilder<BigInteger>(enumType.Cases.Length);
		var next = BigInteger.Zero;
		foreach (var enumCase in enumType.Cases)
		{
			var value = enumCase.Node.Value is { } expression
				? EvaluateCaseValue(enumCase, expression, context with { LocalScope = scope }) ?? next
				: next;
			
			values.Add(value);
			scope.Define(new LocalVariableSymbol(enumCase.Node.Identifier, NativeSymbols.Int128, false)
			{
				ConstantValue = new IntegerConstant(NativeSymbols.Int128, value)
			});
			
			next = value + 1;
		}
		
		return values.MoveToImmutable();
	}
	
	private BigInteger? EvaluateCaseValue(EnumCaseSymbol enumCase, IExpressionNode expression,
		ResolutionContext context)
	{
		var value = constants!.ResolveInitializer(expression, NativeSymbols.Int128, context);
		switch (Evaluator.Evaluate(value))
		{
			case IntegerConstant constant:
				return constant.Value;
			
			case null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"The value of '{enumCase.Name}' must be a constant"));
				
				return null;
			
			default:
				return null;
		}
	}
	
	private IntegerType GetTagType(EnumSymbol enumType, ResolutionContext context, IReadOnlyList<BigInteger> values)
	{
		var declared = enumType.Node.TagType is { } typeNode ? ResolveTagType(typeNode, context) : null;
		var tagType = declared ?? (enumType.IsExternal ? NativeSymbols.Int32 : null);
		if (tagType is null)
			return SmallestTagType(values);
		
		for (var i = 0; i < values.Count; i++)
		{
			if (Evaluator.Fits(values[i], tagType))
				continue;
			
			var enumCase = enumType.Cases[i];
			var location = enumCase.Node.Value?.SourceLocation ?? enumCase.Node.Identifier.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"'{enumCase.Name}' is {values[i]}, which doesn't fit in '{tagType.Name}'"));
		}
		
		return tagType;
	}
	
	private IntegerType? ResolveTagType(ITypeNode typeNode, ResolutionContext context)
	{
		var type = context.ResolveType(typeNode);
		if (type is IntegerType tagType && NativeSymbols.PureIntegerTypes.Contains(tagType))
			return tagType;
		
		if (type is not InvalidType)
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation,
				$"'{type.Name}' isn't an integer type"));
		
		return null;
	}
	
	private IntegerType SmallestTagType(IReadOnlyList<BigInteger> values)
	{
		IntegerType[] candidates = values.Any(static value => value.Sign < 0)
			? [NativeSymbols.Int8, NativeSymbols.Int16, NativeSymbols.Int32, NativeSymbols.Int64]
			: [NativeSymbols.UInt8, NativeSymbols.UInt16, NativeSymbols.UInt32, NativeSymbols.UInt64];
		
		return candidates.FirstOrDefault(type => values.All(value => Evaluator.Fits(value, type)))
		       ?? NativeSymbols.Int128;
	}
	
	private void ReportSharedValues(EnumSymbol enumType, IReadOnlyList<BigInteger> values,
		IReadOnlyDictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>> payloads)
	{
		var groups = enumType.Cases
			.Select((enumCase, i) => (Case: enumCase, Value: values[i]))
			.GroupBy(static c => c.Value);
		
		foreach (var group in groups)
		{
			var shape = payloads[group.First().Case];
			if (group.All(c => payloads[c.Case].SequenceEqual(shape)))
				continue;
			
			var names = DiagnosticReporter.JoinNames([..group.Select(static c => c.Case.Name)]);
			foreach (var (enumCase, value) in group)
				Diagnostics.Add(new(DiagnosticSeverity.Error, enumCase.Node.Identifier.SourceLocation,
					$"{names} share the value {value} but have different payloads"));
		}
	}
	
	private void CollectConstructor(FunctionSymbol function, ConstructorNode node, ResolutionContext context)
	{
		var containingType = context.ContainingType!;
		var visibility = GetEffectiveVisibility(function, context);
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, false);
		
		var scope = new Scope();
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length + 1);
		
		var selfSymbol = function.Parameters[0];
		var selfType = _typePool.GetPointerType(containingType);
		
		paramTypes.Add(selfType);
		_builder.VariableTypes[selfSymbol] = selfType;
		scope.Define(selfSymbol);
		
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + 1]; // + 1 due to implicit self parameter
			var paramType = _typePool.GetPassedType(
				RejectValueDyn(param.Type, context.ResolveType(param.Type), paramSymbol.Mode), paramSymbol.Mode);
			
			ReportHiddenType(param.Type, paramType, visibility, node.Keyword.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		var signature = new FunctionSignature(paramTypes, NativeSymbols.Void, false, GetModes(function));
		var mangledName = context.Mangle(function, signature);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		
		_builder.Functions[function] = info;
		if (context.Trait is { } trait)
			_typePool.AddTraitConstructor(trait, info);
		else
			_typePool.AddConstructor(containingType, info);
	}
	
	private void CollectDestructor(FunctionSymbol function, ResolutionContext context)
	{
		var self = function.Parameters[0];
		var selfType = _typePool.GetPointerType(context.ContainingType!);
		_builder.VariableTypes[self] = selfType;
		
		var scope = new Scope();
		scope.Define(self);
		
		var signature = new FunctionSignature([selfType], NativeSymbols.Void, false, GetModes(function));
		var mangledName = context.Mangle(function, signature);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		_builder.Functions[function] = info;
		_typePool.SetDestructor(context.ContainingType!, info);
	}
	
	private FunctionInfo CollectFunction(FunctionSymbol function, FunctionNode node, ResolutionContext context)
	{
		ReportWhenFunction(node, context);
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.TypeParameters.Select(static p => p.Identifier),
			static name => $"Type parameter '{name}' is declared more than once"));
		
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, node.IsExternal, function.Property?.Node?.Type);
		
		var scope = new Scope();
		
		var paramTypes = new List<TypeSymbol>(function.Parameters.Length);
		if (node.Receiver is { } receiver)
		{
			var self = function.Parameters[0];
			var selfType = context.ContainingType is { } owner
				? _typePool.GetPassedType(owner, self.Mode)
				: NativeSymbols.Invalid;
			
			if (context.ContainingType is null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, receiver.SourceLocation,
					"Cannot take 'self' outside types"));
			
			paramTypes.Add(selfType);
			_builder.VariableTypes[self] = selfType;
			scope.Define(self);
		}
		
		var visibility = GetEffectiveVisibility(function, context);
		var name = function.Property?.Name ?? node.Identifier.Text;
		var reportsHiddenTypes = function.Property?.Node?.Keyword is null;
		var offset = paramTypes.Count;
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + offset];
			var paramType = _typePool.GetPassedType(
				RejectValueDyn(param.Type, ResolveSignatureType(function, param.Type, context), paramSymbol.Mode),
				paramSymbol.Mode);
			
			if (reportsHiddenTypes)
				ReportHiddenType(param.Type, paramType, visibility, name);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = RejectValueDyn(returnTypeSyntax, ResolveSignatureType(function, returnTypeSyntax, context));
		
		if (reportsHiddenTypes && node.ReturnType is { } returnTypeNode)
			ReportHiddenType(returnTypeNode, returnType, visibility, name);
		
		var signature = new FunctionSignature(paramTypes, returnType, false, GetModes(function));
		
		// TODO Disable mangling if indicated
		var mangledName = context.Mangle(function, signature);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		
		if (_entryPointName is not null && function.Name == _entryPointName && function.TypeParameters.IsEmpty &&
		    IsEntryPoint(signature))
		{
			_entryPoints.Add(info);
			if (node.When is { } when)
				Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation,
					$"Cannot use 'when' on '{_entryPointName}'"));
		}
		
		return info;
	}
	
	private void ReportWhenFunction(FunctionNode node, ResolutionContext context)
	{
		if (node.When is not { } when)
			return;
		
		if (node.Identifier.Type != TokenType.Identifier)
			Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation,
				"Cannot use 'when' on operators"));
		else if (context.Trait is not null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, when.Keyword.SourceLocation, "Cannot use 'when' in traits"));
		
		if (node.ReturnType is { } returnType)
			Diagnostics.Add(new(DiagnosticSeverity.Error, returnType.SourceLocation,
				"Cannot return values from 'when' functions"));
		
		var modes = node.Parameters.Select(static parameter => parameter.Mode).Prepend(node.Receiver?.Mode);
		foreach (var mode in modes.OfType<Token>().Where(static mode => mode.Type == TokenType.KeywordOwn))
			Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
				"Cannot take 'own' parameters in 'when' functions"));
	}
	
	private FunctionInfo CollectExternalFunction(FunctionSymbol function, ExternalFunctionNode node,
		ResolutionContext context)
	{
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, true);
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = _typePool.GetPassedType(RejectExtDyn(param.Type, context.ResolveType(param.Type)),
				paramSymbol.Mode);
			
			ReportHiddenType(param.Type, paramType, function.Visibility, node.Identifier.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = RejectExtDyn(returnTypeSyntax, context.ResolveType(returnTypeSyntax));
		
		if (node.ReturnType is { } returnTypeNode)
			ReportHiddenType(returnTypeNode, returnType, function.Visibility, node.Identifier.Text);
		
		var signature = new FunctionSignature(paramTypes, returnType, node.IsVariadic, GetModes(function));
		return new(null, function, signature, null, node.Origin, context.File);
	}
	
	private static FunctionInfo CreateInvalidInfo(FunctionSymbol function, Declaration declaration)
	{
		var parameterTypes = function.Parameters.Select(static _ => (TypeSymbol)NativeSymbols.Invalid);
		var signature = new FunctionSignature(parameterTypes, NativeSymbols.Invalid, false, GetModes(function));
		return new(null, function, signature, new Scope(), null, declaration.Context.File);
	}
	
	private static IEnumerable<ParameterMode> GetModes(FunctionSymbol function) =>
		function.Parameters.Select(static p => p.Mode);
	
	private bool Enter(Symbol symbol, bool isValue)
	{
		var index = _inProgress.IndexOf((symbol, isValue));
		if (index < 0)
		{
			_inProgress.Add((symbol, isValue));
			return true;
		}
		
		foreach (var (member, _) in _inProgress.Skip(index))
			if (_cyclic.Add(member))
				ReportCycle(member);
		
		return false;
	}
	
	private void Exit() => _inProgress.RemoveAt(_inProgress.Count - 1);
	
	private void ReportCycle(Symbol symbol)
	{
		var (identifier, message) = symbol switch
		{
			GlobalSymbol global => (global.Syntax.Identifier, $"'{global.Name}' depends on its own value"),
			RecordSymbol record => (record.Node.Identifier, $"'{record.Name}' depends on its own layout"),
			EnumSymbol enumType => (enumType.Node.Identifier, $"'{enumType.Name}' depends on its own layout"),
			FunctionSymbol function => (GetIdentifier(function.Syntax),
				$"'{function.Name}' depends on its own signature"),
			_ => throw new InvalidOperationException()
		};
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, identifier.SourceLocation, message));
	}
	
	private void ReportDuplicateParameters(IEnumerable<ParameterNode> parameters) =>
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(parameters.Select(static p => p.Identifier),
			static name => $"Parameter '{name}' is declared more than once"));
	
	private void ReportBorrowParameters(IEnumerable<ParameterNode> parameters, bool isExternal,
		ITypeNode? sharedType = null)
	{
		foreach (var parameter in parameters)
		{
			var diagnostic = parameter.Type switch
			{
				BorrowTypeNode borrow => DiagnosticReporter.ReportBorrowParameter(borrow, parameter.Identifier.Text,
					parameter.Mode is not null, isExternal),
				FunctionTypeNode { IsRef: true } function => DiagnosticReporter.ReportRefFunctionParameter(function,
					parameter.Identifier.Text, parameter.Mode is not null, isExternal),
				_ => null
			};
			
			if (diagnostic is null)
				continue;
			
			Diagnostics.Add(parameter.Type == sharedType
				? new(DiagnosticSeverity.Error, parameter.Identifier.SourceLocation, diagnostic.Message)
				: diagnostic);
		}
	}
	
	private void ReportDuplicateDeclarations()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var declarationsByName = module.Files
				.SelectMany(static file => file.Syntax.Declarations.Select(declaration => (file, declaration)))
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d.declaration) && d.declaration is not ImplNode)
				.Select(d => (d.file, d.declaration, symbol: _symbolTable.DeclarationSymbols[d.declaration]))
				.ToLookup(static d => d.symbol.Name);
			
			foreach (var sameName in declarationsByName)
			{
				foreach (var (file, declaration, symbol) in sameName)
				{
					var visibleTogether = sameName
						.Where(other => AreVisibleTogether(symbol, file, other.symbol, other.file))
						.Select(static other => other.declaration);
					
					if (FindConflict(declaration, visibleTogether) is { } diagnostic)
						Diagnostics.Add(diagnostic);
				}
			}
		}
	}
	
	private static bool AreVisibleTogether(Symbol first, FileSymbol firstFile, Symbol second, FileSymbol secondFile) =>
		firstFile.Module == secondFile.Module && (firstFile == secondFile ||
		                                          ModuleIndex.GetVisibility(first) != Visibility.Private ||
		                                          ModuleIndex.GetVisibility(second) != Visibility.Private);
	
	private Diagnostic? FindConflict(IDeclarationNode declaration, IEnumerable<IDeclarationNode> sameName)
	{
		var symbol = _symbolTable.DeclarationSymbols[declaration];
		var others = sameName
			.Where(other => other != declaration)
			.Select(other => _symbolTable.DeclarationSymbols[other])
			.ToList();
		
		if (others.Count == 0)
			return null;
		
		var location = GetIdentifier(declaration).SourceLocation;
		if (symbol is not FunctionSymbol function || others.Any(static s => s is not FunctionSymbol))
			return new(DiagnosticSeverity.Error, location,
				$"'{symbol.Name}' is declared more than once in this module");
		
		var isExternal = function.Kind == FunctionKind.External;
		if (isExternal && others.Any(static s => s is FunctionSymbol { Kind: FunctionKind.External }))
			return new(DiagnosticSeverity.Error, location, "'ext' functions cannot be overloaded");
		
		var message = DescribeConflict(symbol.Name, _builder.Functions[function].Signature,
			others.Select(other => _builder.Functions[(FunctionSymbol)other].Signature));
		
		return message is null ? null : new(DiagnosticSeverity.Error, location, message);
	}
	
	private static string? DescribeConflict(string name, FunctionSignature signature,
		IEnumerable<FunctionSignature> others)
	{
		var sameParameters = others.Where(other => HasSameParameters(other, signature)).ToList();
		if (sameParameters.Count == 0)
			return null;
		
		var sameModes = sameParameters
			.Where(other => other.ParameterModes.SequenceEqual(signature.ParameterModes))
			.ToList();
		
		var sameReturn = sameParameters.Any(other => other.ReturnType == signature.ReturnType);
		return (sameModes.Count > 0, sameReturn) switch
		{
			(true, _) when sameModes.Any(other => other.ReturnType == signature.ReturnType) =>
				$"'{name}' is declared more than once with the same signature",
			(true, _) => $"'{name}' overloads cannot differ only in return type",
			(false, true) => $"'{name}' overloads cannot differ only in parameter modes",
			(false, false) => $"'{name}' overloads cannot differ only in parameter modes and return type"
		};
	}
	
	private void ReportModuleConflicts()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var path = Modules.Find(module.Name)!;
			var declarations = module.Files
				.SelectMany(static f => f.Syntax.Declarations)
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d) && d is not ImplNode);
			
			foreach (var declaration in declarations)
			{
				var name = _symbolTable.DeclarationSymbols[declaration].Name;
				if (path.Children.ContainsKey(name))
					Diagnostics.Add(ReportModuleConflict(GetIdentifier(declaration).SourceLocation, path, name));
			}
			
			if (path.Parent is not { } parent || !parent.Members.ContainsKey(path.Name))
				continue;
			
			foreach (var file in module.Files)
				Diagnostics.Add(ReportModuleConflict(file.Syntax.ModuleName.SourceLocation, parent, path.Name));
		}
	}
	
	private static Diagnostic ReportModuleConflict(SourceLocation location, ModulePathSymbol parent, string name) =>
		new(DiagnosticSeverity.Error, location,
			$"'{parent.Path}.{name}' is both a module and a member of '{parent.Path}'");
	
	private static bool HasSameParameters(FunctionSignature first, FunctionSignature second) =>
		first.IsVariadic == second.IsVariadic && first.ParameterTypes.Length == second.ParameterTypes.Length &&
		Enumerable.Range(0, first.ParameterTypes.Length).All(i =>
			first.GetDeclaredType(i) is not InvalidType && first.GetDeclaredType(i) == second.GetDeclaredType(i));
	
	private static Token GetIdentifier(IDeclarationNode declaration) => declaration switch
	{
		FunctionNode node => node.Identifier,
		ExternalFunctionNode node => node.Identifier,
		ConstructorNode node => node.Keyword,
		RecordNode node => node.Identifier,
		EnumNode node => node.Identifier,
		GlobalNode node => node.Identifier,
		TraitNode node => node.Identifier,
		_ => throw new InvalidOperationException()
	};
	
	private void ReportEntryPoint()
	{
		if (_entryPointName is null || _entryPoints.Count == 1)
			return;
		
		if (_entryPoints.Count > 1)
		{
			var reportedAsDuplicates = _entryPoints.All(entryPoint => _entryPoints.Any(other => other != entryPoint &&
				AreVisibleTogether(entryPoint.Symbol, entryPoint.File!, other.Symbol, other.File!)));
			
			if (reportedAsDuplicates)
				return;
			
			foreach (var entryPoint in _entryPoints)
				Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(entryPoint.Symbol.Syntax).SourceLocation,
					$"The program has more than one '{_entryPointName}' function"));
			
			return;
		}
		
		var candidates = _builder.Functions.Values
			.Where(f => f.Symbol is { Kind: FunctionKind.Free } && f.Symbol.Name == _entryPointName)
			.ToList();
		
		if (candidates.Count == 0)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, SourceLocation.None,
				$"The program has no '{_entryPointName}' function"));
			
			return;
		}
		
		foreach (var candidate in candidates)
		{
			Diagnostics.Add(candidate.Symbol.Syntax is FunctionNode { TypeParameters: [var first, ..] }
				? new(DiagnosticSeverity.Error, first.Identifier.SourceLocation,
					$"Cannot declare type parameters on '{_entryPointName}'")
				: new(DiagnosticSeverity.Error, GetIdentifier(candidate.Symbol.Syntax).SourceLocation,
					$"'{_entryPointName}' must have no parameters and return 'i32' or nothing"));
		}
	}
	
	private void ReportPlainEnumsInExtSignatures()
	{
		foreach (var (type, node) in GetExtSignatureTypes())
			ReportPlainEnum(type, node);
	}
	
	private void ReportDestructorsInExtSignatures()
	{
		foreach (var (type, node) in GetExtSignatureTypes())
			_extSignatureTypes.Add(type, node);
		
		_extSignatureTypes.ReportDestructors(Diagnostics);
	}
	
	private IEnumerable<(TypeSymbol Type, ITypeNode Node)> GetExtSignatureTypes()
	{
		foreach (var (function, info) in _builder.Functions)
		{
			if (!function.IsExternal)
				continue;
			
			var (parameters, returnType) = function.Syntax switch
			{
				FunctionNode node => (node.Parameters, node.ReturnType),
				ExternalFunctionNode node => (node.Parameters, node.ReturnType),
				_ => ([], null)
			};
			
			for (var i = 0; i < parameters.Length; i++)
				yield return (info.Signature.GetDeclaredType(i), parameters[i].Type);
			
			if (returnType is not null)
				yield return (info.Signature.ReturnType, returnType);
		}
	}
	
	private void ReportPlainEnum(TypeSymbol type, ITypeNode node)
	{
		if (!HasPlainEnum(type))
			return;
		
		var (located, location) = ExtSignatureTypes.Locate(type, node, HasPlainEnum);
		Diagnostics.Add(ExtSignatureTypes.Report(location, "Cannot use plain enums in 'ext' signatures", located,
			FindPlainEnum(located, [])!));
	}
	
	private bool HasPlainEnum(TypeSymbol type) => FindPlainEnum(type, []) is not null;
	
	private EnumSymbol? FindPlainEnum(TypeSymbol type, HashSet<TypeSymbol> visited)
	{
		if (type is EnumSymbol { IsExternal: false, IsMatch: false } plainEnum)
			return plainEnum;
		
		if (!visited.Add(type))
			return null;
		
		IEnumerable<TypeSymbol> parts = type switch
		{
			EnumSymbol enumType => enumType.Cases.SelectMany(c => _typePool.GetPayloadTypes(enumType, c)),
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>().Select(_typePool.GetTypeOfMember),
			ArrayType array => [array.ElementType],
			FunctionType { IsExternal: true } function =>
				[..function.ParameterTypes.Select((_, i) => function.GetDeclaredType(i)), function.ReturnType],
			_ => []
		};
		
		return parts.Select(part => FindPlainEnum(part, visited)).FirstOrDefault(static found => found is not null);
	}
	
	private void ReportLayoutCycles()
	{
		foreach (var type in _symbolTable.DeclarationSymbols.Values.OfType<TypeSymbol>())
		{
			foreach (var (field, fieldType) in GetStoredFields(type))
			{
				if (!ContainsType(fieldType, type))
					continue;
				
				var kind = type is EnumSymbol ? "Payload" : "Field";
				Diagnostics.Add(new(DiagnosticSeverity.Error, field.Node!.Type.SourceLocation,
					$"{kind} '{field.Name}' of type '{fieldType.Name}' causes a cycle in the memory layout"));
			}
		}
	}
	
	private bool ContainsType(TypeSymbol type, TypeSymbol target)
	{
		var visited = new HashSet<TypeSymbol>();
		var pending = new Stack<TypeSymbol>([type]);
		
		while (pending.TryPop(out var current))
		{
			if (GetStoredType(current) is not { } stored || !visited.Add(stored))
				continue;
			
			if (stored.OriginalDefinition == target)
				return true;
			
			foreach (var (_, fieldType) in GetStoredFields(stored))
				pending.Push(fieldType);
		}
		
		return false;
	}
	
	private IEnumerable<(FieldSymbol Field, TypeSymbol Type)> GetStoredFields(TypeSymbol type)
	{
		IEnumerable<FieldSymbol> fields = type switch
		{
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>(),
			EnumSymbol enumType => _typePool.GetPayloadFields(enumType),
			_ => []
		};
		
		foreach (var field in fields)
			if (_typePool.TryGetTypeOfMember(field, out var fieldType))
				yield return (field, fieldType);
	}
	
	private static TypeSymbol? GetStoredType(TypeSymbol type) => type switch
	{
		RecordSymbol or EnumSymbol => type,
		ArrayType array => GetStoredType(array.ElementType),
		_ => null
	};
	
	private static bool IsEntryPoint(FunctionSignature signature)
	{
		// To be an entry point, it must return void or i32 and have either no parameters or take an array of strings
		// TODO Not complete; also, allow async returns?
		
		// TODO Do we allow other integer return types?
		var returnType = signature.ReturnType;
		if (returnType != NativeSymbols.Void && returnType != NativeSymbols.Int32)
			return false;
		
		// TODO Allow view of strings as parameter
		var paramTypes = signature.ParameterTypes;
		if (paramTypes.Length > 0)
			return false;
		
		return true;
	}
	
	private ImportEnvironment CollectImports(FileNode node, ResolutionContext context)
	{
		var imports = new List<Symbol>();
		foreach (var importExpression in node.Imports)
			imports.AddRange(CollectImport(importExpression, context));
		
		return new(imports);
	}
	
	private List<Symbol> CollectImport(ImportExpression import, ResolutionContext context)
	{
		var path = import.ModuleName.Parts;
		var symbol = context.ResolveMembers(Modules.Root, path, 0);
		if (symbol is ModulePathSymbol module)
			return CollectImport(module, import.Import, context);
		
		if (symbol is not null)
			Diagnostics.Add(ResolutionContext.ReportNotModule(symbol, path));
		
		return [];
	}
	
	private List<Symbol> CollectImport(ModulePathSymbol module, IImport import, ResolutionContext context) =>
		import switch
		{
			FullImport => [..module.Members.Values.SelectMany(static members => members).Where(context.IsVisible)],
			TokenImport i => ImportMember(module, i.Token, context),
			ListImport i => [..i.Tokens.SelectMany(token => ImportMember(module, token, context))],
			_ => []
		};
	
	private List<Symbol> ImportMember(ModulePathSymbol module, Token name, ResolutionContext context)
	{
		if (module.Members.TryGetValue(name.Text, out var members) &&
		    members.Where(context.IsVisible).ToList() is { Count: > 0 } visible)
			return visible;
		
		if (module.Children.TryGetValue(name.Text, out var child))
			return [child];
		
		Diagnostics.Add(context.ReportUndefinedMember(name.SourceLocation, module, name.Text));
		return [];
	}
}