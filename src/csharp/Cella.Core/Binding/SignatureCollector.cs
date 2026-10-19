using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public interface IConstantResolver
{
	IResolvedExpressionNode ResolveInitializer(IExpressionNode initializer, TypeSymbol? type,
		ResolutionContext context);
	
	Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context, TypeSymbol? targetType);
	
	ValueParameterUse? FindValueParameterUse(IExpressionNode expression, ResolutionContext context);
}

public sealed partial class SignatureCollector
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
	private bool dropsEntryPoint;
	private readonly SymbolTable _symbolTable;
	private readonly TypePool _typePool;
	private readonly SignatureTable _dependencyTable;
	private readonly SignatureTable.Builder _builder = new();
	private readonly Dictionary<Symbol, Declaration> _declarations = [];
	private readonly HashSet<(SourceLocation, string)> _reportedImportErrors = [];
	
	private readonly List<(List<(Symbol Symbol, Token Name, string Module)> Imports, ResolutionContext Context)>
		_explicitImports = [];
	
	private readonly Dictionary<GlobalSymbol, TypeSymbol> _globalTypes = [];
	private readonly Dictionary<PropertySymbol, TypeSymbol> _propertyTypes = [];
	private readonly HashSet<TypeSymbol> _completedTypes = [];
	private readonly HashSet<Symbol> _completedSymbols = [];
	private readonly List<(Symbol Symbol, bool IsValue)> _inProgress = [];
	private readonly HashSet<Symbol> _cyclic = [];
	private readonly List<FunctionInfo> _entryPoints = [];
	private readonly HashSet<Symbol> _conflictingDeclarations = [];
	private readonly ExtSignatureTypes _extSignatureTypes;
	private readonly CopiedParameters _copiedParameters;
	private readonly uint _pointerBitSize;
	private readonly List<ImplSymbol> _impls = [];
	private readonly Dictionary<ImplSymbol, TypeSymbol> _implTargets = [];
	private readonly List<Conformance> _localConformances = [];
	private readonly Dictionary<Conformance, string> _foreignErrors = [];
	private readonly HashSet<FunctionSymbol> _witnessMembers = [];
	private readonly HashSet<GlobalSymbol> _witnessConstants = [];
	
	private readonly List<(TypeSymbol Type, GlobalSymbol Constant, GlobalSymbol Witness)>
		_traitConstantsInProgress = [];
	
	private readonly List<ConstraintCheck> _deferredChecks = [];
	
	private readonly List<(TypeParameterNode, TypeParameterSymbol, ResolutionContext, Symbol?)>
		_pendingConstructorBounds = [];
	
	private readonly List<(ImmutableArray<ITypeNode>, List<TypeSymbol?>, TypeSymbol)> _boundLists = [];
	private readonly List<(FieldNode, TypeSymbol)> _requiredFields = [];
	private readonly List<(GlobalSymbol Global, ITypeNode Node, TypeSymbol Type)> _variables = [];
	private readonly List<(TypeSymbol Type, ITypeNode Node)> _variadicTypes = [];
	private readonly HashSet<FunctionSymbol> _lambdas = [];
	private IConstantResolver? constants;
	
	public DiagnosticList Diagnostics { get; } = new();
	public ConstantEvaluator Evaluator { get; }
	public ModuleIndex Modules { get; }
	public bool QualifiesEntryPoint { get; init; }
	public Action<Token, Symbol>? RecordName { get; init; }
	public CompletionProbe? Probe { get; init; }
	
	public SignatureCollector(string assemblyName, string? entryPointName, SymbolTable symbolTable,
		TypePool typePool, IEnumerable<AssemblySymbol> dependencies, IEnumerable<AssemblySymbol> indirectDependencies,
		IReadOnlyDictionary<string, string> aliases, uint pointerBitSize)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
		_typePool = typePool;
		_extSignatureTypes = new(typePool);
		_copiedParameters = new(typePool);
		_pointerBitSize = pointerBitSize;
		var dependencyList = dependencies.ToImmutableArray();
		var indirectList = indirectDependencies.ToImmutableArray();
		_dependencyTable = SignatureTable.Combine(dependencyList.Concat(indirectList)
			.Select(static a => a.SignatureTable));
		
		Evaluator = new ConstantEvaluator(typePool, pointerBitSize, GetGlobalValue);
		typePool.FoldParametric = Evaluator.EvaluateInstance;
		typePool.TraitConstantValue = EvaluateTraitConstant;
		typePool.GlobalTypeOf = GetGlobalType;
		typePool.FindParametricFault = Evaluator.FindInstanceFault;
		typePool.IntegerFits = Evaluator.Fits;
		Modules = new ModuleIndex(assemblyName, symbolTable, dependencyList, indirectList, aliases);
	}
	
	public void Collect(IReadOnlyCollection<FileNode> files, IConstantResolver constantResolver)
	{
		constants = constantResolver;
		dropsEntryPoint = files.Any(file => file.DroppedNames.Any(name => IsEntryPointName(file, name)));
		_typePool.TypeCompleter = Complete;
		_typePool.TraitCompleter = CompleteTrait;
		_typePool.SizeTable.Measurer = Measure;
		
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
		CheckImplBlocks();
		ReportVariadicTypes();
		_typePool.TypeCompleter = null;
		_typePool.TraitCompleter = null;
		_typePool.SizeTable.Measurer = null;
	}
	
	private ISize? Measure(TypeSymbol type, Func<ISize?> computeSize)
	{
		var entry = ((Symbol)type, false);
		if (type is not (RecordSymbol or EnumSymbol) || _inProgress.Contains(entry))
			return computeSize();
		
		_inProgress.Add(entry);
		try
		{
			return computeSize();
		}
		finally
		{
			_inProgress.Remove(entry);
		}
	}
	
	public TypeSymbol GetImplTarget(ImplSymbol impl) => _implTargets.GetValueOrDefault(impl, NativeSymbols.Invalid);
	
	public AssemblySymbol FinishAssembly(string name)
	{
		ReportDuplicateDeclarations();
		var importers = new Dictionary<Symbol, HashSet<string>>();
		foreach (var (imports, context) in _explicitImports)
			ReportOnce(() => ReportImportConflicts(imports, context, importers));
		
		ReportImportedDeclarations(importers);
		
		ReportPlainNameConflicts();
		ReportModuleConflicts();
		ReportLayoutCycles();
		ReportEntryPoint();
		ReportExtSignatureTypes();
		_copiedParameters.Report(Diagnostics);
		foreach (var (symbol, declaration) in _declarations)
		{
			if (declaration.Node is FunctionNode { Identifier.Type: TokenType.OpQuestion })
				_builder.InlineContexts[(FunctionSymbol)symbol] = new(declaration.Context);
		}
		
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
	
	public IEnumerable<KeyValuePair<string, FunctionSymbol>> PanicTargets =>
		_dependencyTable.PanicTargets.Concat(_builder.PanicTargets);
	
	public void AddPanicTarget(string path, FunctionSymbol target) => _builder.PanicTargets[path] = target;
	
	public ResolutionContext? FindInlineContext(FunctionSymbol function) =>
		_builder.InlineContexts.TryGetValue(function, out var declaration) ? declaration.Context
		: _dependencyTable.InlineContexts.TryGetValue(function, out declaration) ? declaration.Context
		: null;
	
	public FunctionInfo? FindFunctionInfo(FunctionSymbol function) =>
		_builder.Functions.ContainsKey(function) || _declarations.ContainsKey(function) ||
		_dependencyTable.Functions.ContainsKey(function)
			? GetFunctionInfo(function)
			: null;
	
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
	
	public Constant? EvaluateTraitConstant(TypeSymbol type, GlobalSymbol requirement)
	{
		if (TypePool.ContainsTypeParameters(type) || requirement.Trait is not { } trait ||
		    _typePool.FindConformances(type).FirstOrDefault(conformance => conformance.Trait == trait) is not
			    { } conformance ||
		    FindConstantWitness(conformance, requirement) is not { } witness)
			return null;
		
		var index = _traitConstantsInProgress.FindIndex(entry => entry.Type == type && entry.Constant == requirement);
		if (index >= 0)
		{
			foreach (var (_, _, member) in _traitConstantsInProgress.Skip(index))
				if (_cyclic.Add(member))
					Diagnostics.Add(new(DiagnosticSeverity.Error, member.Syntax.Identifier.SourceLocation,
						$"'{member.Name}' depends on its own value"));
			
			return InvalidConstant.Instance;
		}
		
		_traitConstantsInProgress.Add((type, requirement, witness));
		try
		{
			var declared = witness.Trait is null && witness.Impl is null
				? type.GetStaticField(witness.Name) ?? witness
				: witness;
			
			var map = witness.Impl is not null
				? _typePool.GetConformanceMap(conformance, type)
				: CreateTraitMap(conformance, type);
			
			return GetGlobalInfo(declared)?.Value switch
			{
				ParametricConstant parametric => Evaluator.EvaluateInstance(parametric.Value, map),
				var value => value
			};
		}
		finally
		{
			_traitConstantsInProgress.RemoveAt(_traitConstantsInProgress.Count - 1);
		}
	}
	
	private static GlobalSymbol? FindConstantWitness(Conformance conformance, GlobalSymbol requirement) =>
		conformance.ConstantWitnesses.GetValueOrDefault(requirement) ??
		(FindConstantCandidate(conformance, requirement.Name) is { IsConstant: true } candidate ? candidate : null) ??
		(requirement.Syntax.Initializer is not null ? requirement : null);
	
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
		var initializer = node.Initializer is { } expression
			? constants!.ResolveInitializer(expression, type, context)
			: null;
		
		var value = type is InvalidType
			? InvalidConstant.Instance
			: initializer is null
				? null
				: Evaluator.EvaluateStandalone(initializer) ??
				  ((global.ContainingType is NamedTypeSymbol { IsGenericDefinition: true } ||
				    global.Trait is not null ||
				    global.Impl is not null) && IsParametric(initializer)
					  ? new ParametricConstant(initializer)
					  : null);
		
		if (node.IsConstant && type is not InvalidType && _typePool.NeedsDrop(type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Type.SourceLocation,
				"Cannot store values with destructors in constants"));
		
		var mangledName = global.IsImport ? global.ExternalName! : context.Mangle(global);
		
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
			if (!_typePool.HoldsBorrows(type))
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
			CopiedParameters = _copiedParameters,
			EvaluateConstant = EvaluateConstant,
			FindValueParameterUse = FindValueParameterUse,
			GenericTypes = [],
			DeferConstraintCheck = DeferConstraintCheck,
			RecordName = RecordName,
			Probe = Probe
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
}