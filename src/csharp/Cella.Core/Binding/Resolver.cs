using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver : IStatementNodeVisitor<IResolvedStatementNode>,
	IExpressionNodeVisitor<IResolvedExpressionNode>, IDeclarationNodeVisitor<IResolvedDeclarationNode>,
	IConstantResolver
{
	// Used to fold unary operations on literals
	private sealed class UnaryOpJob(TokenType op)
	{
		public TokenType Op { get; } = op;
		public bool Consumed { get; set; }
	}
	
	public DiagnosticList Diagnostics { get; } = new();
	public ErrorContainment? Containment { get; init; }
	
	private const int UserConversionCost = 100;
	
	private static readonly NativeConversion _boolPromotion =
		new(NativeSymbols.Bool, NativeSymbols.Int32, ConversionKind.Implicit, 0);
	
	private readonly SymbolTable _symbolTable;
	private readonly SignatureCollector _signatures;
	private readonly ConstantEvaluator _evaluator;
	private readonly PatternCoverage _coverage;
	private readonly TypePool _typePool;
	private readonly ConversionTable _conversionTable;
	private readonly OperatorRegistry _operatorRegistry;
	private readonly uint _pointerBitSize;
	private readonly BigInteger _isizeMinValue;
	private readonly BigInteger _isizeMaxValue;
	private readonly BigInteger _usizeMinValue;
	private readonly BigInteger _usizeMaxValue;
	private readonly Dictionary<FunctionSymbol, FunctionInfo> _importedFunctions = [];
	private readonly Stack<UnaryOpJob> _unaryOpJobs = [];
	private readonly Stack<Expectation> _expectations = [];
	private readonly Stack<ResolutionContext> _resolutionContexts = [];
	private readonly HashSet<LocalVariableSymbol> _repeatedBindings = [];
	private readonly Dictionary<IResolvedExpressionNode, ImmutableArray<LocalVariableSymbol?>> _failedPatterns = [];
	private readonly Dictionary<ResolvedCaseNameExpressionNode, IResolvedExpressionNode> _caseNameFallbacks = [];
	private readonly Dictionary<ResolvedInterpolatedStringExpressionNode, IResolvedExpressionNode> _foldedStrings = [];
	private readonly Dictionary<ResolvedLiteralExpressionNode, LiteralFold> _literalFolds = [];
	private readonly Dictionary<(IResolvedExpressionNode, FStrType), IResolvedExpressionNode> _templates = [];
	private readonly ExtSignatureTypes _extSignatureTypes;
	private readonly CopiedParameters _copiedParameters;
	private readonly TypeInference _inference;
	private readonly List<Instantiation> _instantiations = [];
	private readonly HashSet<Diagnostic> _inferenceFailures = new(ReferenceEqualityComparer.Instance);
	private readonly List<ResolvedFunctionNode> _lambdas = [];
	private readonly HashSet<IExpressionNode> _resolvingFallbacks = [];
	private readonly List<LambdaFrame> _lambdaFrames = [];
	private readonly HashSet<SourceLocation> _sizeCheckLocations = [];
	private IExpressionNode? storeTarget;
	private List<Action>? journal;
	private int openCheckpoints;
	private int lambdaCount;
	private LocalSurvey? survey;
	private IReadOnlyDictionary<ISyntaxNode, TypeSymbol>? settledTypes;
	private IExpressionNode? placeTarget;
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	private TypeSymbol? CurrentTargetType => _expectations.TryPeek(out var top) && !top.IsHint ? top.Type : null;
	private TypeSymbol? ExpectedType => _expectations.TryPeek(out var top) ? top.Type : null;
	
	public Resolver(SymbolTable symbolTable, SignatureCollector signatures, TypePool typePool, uint pointerBitSize)
	{
		_typePool = typePool;
		_extSignatureTypes = new(typePool);
		_copiedParameters = new(typePool);
		_conversionTable = typePool.ConversionTable;
		_inference = new(typePool);
		_operatorRegistry = typePool.OperatorRegistry;
		_pointerBitSize = pointerBitSize;
		_symbolTable = symbolTable;
		_signatures = signatures;
		_evaluator = signatures.Evaluator;
		_coverage = new PatternCoverage(typePool, _evaluator);
		
		var ptrBits = (int)pointerBitSize;
		_isizeMinValue = -BigInteger.Pow(2, ptrBits - 1);
		_isizeMaxValue = BigInteger.Pow(2, ptrBits - 1) - 1;
		_usizeMinValue = BigInteger.Zero;
		_usizeMaxValue = BigInteger.Pow(2, ptrBits) - 1;
	}
	
	public ResolvedFileNode Resolve(FileNode root) => (ResolvedFileNode)Visit(root);
	
	public IResolvedExpressionNode ResolveInitializer(IExpressionNode initializer, TypeSymbol? type,
		ResolutionContext context)
	{
		_resolutionContexts.Push(context);
		var result = InferLocals(() => VisitNode(initializer, type));
		_resolutionContexts.Pop();
		return result;
	}
	
	public Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context, TypeSymbol? targetType)
	{
		_resolutionContexts.Push(context);
		var result = VisitNode(expression, targetType);
		_resolutionContexts.Pop();
		if (targetType is null || _evaluator.FindConstantError(result) is not { } error)
			return _evaluator.Evaluate(result);
		
		Diagnostics.Add(error);
		return InvalidConstant.Instance;
	}
	
	private IResolvedDeclarationNode VisitNode(IDeclarationNode node) =>
		((IDeclarationNodeVisitor<IResolvedDeclarationNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		((IStatementNodeVisitor<IResolvedStatementNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) => VisitNode(node, null);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node, TypeSymbol? targetType)
	{
		_expectations.Push(new(targetType, false));
		try
		{
			var result = ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(node);
			if (result.Type == NativeSymbols.Void)
				return ReportVoidValue(result, targetType);
			
			return targetType is null
				? result
				: CoerceToType(result, targetType);
		}
		finally
		{
			_expectations.Pop();
		}
	}
	
	private IResolvedExpressionNode VisitDiscarded(IExpressionNode node)
	{
		_expectations.Push(new(null, false));
		var result = ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(node);
		_expectations.Pop();
		return result is ResolvedInterpolatedStringExpressionNode ? MaterializeAsDefault(result) : result;
	}
	
	private ResolvedInvalidExpressionNode ReportVoidValue(IResolvedExpressionNode node, TypeSymbol? type)
	{
		var function = node.Syntax is CallExpressionNode call ? call.Target : node.Syntax;
		var text = function is LambdaExpressionNode
			? "fun"
			: string.Join(' ', function.SourceLocation.GetText().ToString()
				.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
		
		return Error(node.Syntax, $"'{text}' doesn't return a value", type);
	}
	
	private TypeSymbol GetMemberType(MemberSymbol member) =>
		_typePool.TryGetTypeOfMember(member, out var type) ? type : NativeSymbols.Invalid;
	
	private IEnumerable<string> GetVisibleSymbolNames() =>
		CurrentResolutionContext.GetAllSymbols().Select(static s => s.Name).Distinct();
	
	private ResolvedInvalidExpressionNode Error(IExpressionNode node, Diagnostic diagnostic, TypeSymbol? type)
	{
		Diagnostics.Add(diagnostic);
		return new(node, type);
	}
	
	private ResolvedInvalidExpressionNode Error(IExpressionNode node, string message, TypeSymbol? type,
		ISyntaxNode? source = null) => Error(node, message, type, source?.SourceLocation ?? node.SourceLocation);
	
	private ResolvedInvalidExpressionNode Error(IExpressionNode node, string message, TypeSymbol? type,
		SourceLocation sourceLocation)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, sourceLocation, message));
		return new(node, type);
	}
	
	private ResolvedInvalidExpressionNode Error(IExpressionNode node, DiagnosticText text, TypeSymbol? type,
		SourceLocation sourceLocation) => Error(node, text.At(sourceLocation), type);
	
	private static bool IsInvalid(TypeSymbol type) => type is InvalidType;
	private static bool AnyInvalid(params TypeSymbol[] types) => types.Any(IsInvalid);
	private static bool IsInvalid(IResolvedExpressionNode expression) => expression.Type is InvalidType;
	private static bool AnyInvalid(params IResolvedExpressionNode[] expressions) => expressions.Any(IsInvalid);
	
	private FunctionInfo GetFunctionInfo(FunctionSymbol function) => _signatures.GetFunctionInfo(function);
	
	private FunctionInfo GetFunctionInfo(FunctionSymbol function, TypeSymbol owner,
		ImmutableArray<TypeSymbol> traitArguments = default) => _typePool.InstantiateFunction(GetFunctionInfo(function),
		_typePool.GetWitnessArguments(owner, function, traitArguments, function.DeclaredTypeParameters));
	
	private FunctionInfo GetFunctionInfo(MethodSymbol method, TypeSymbol owner) =>
		GetFunctionInfo(method.Function, owner, method.TraitArguments);
	
	private TypeSymbol RequireTypeArguments(TypeSymbol type, IExpressionNode node)
	{
		if (type is not NamedTypeSymbol { IsGenericDefinition: true } generic || node is IndexerExpressionNode)
			return type;
		
		Diagnostics.Add(ResolutionContext.ReportGenericArgumentCount(node.SourceLocation, generic));
		return NativeSymbols.Invalid;
	}
	
	private enum CaseChoice
	{
		Case,
		Symbol,
		Ambiguous
	}
	
	private readonly record struct CallableResolution
	(
		ICallable Callable,
		CallableCost Cost,
		Conversion? ResultConversion,
		Conversion?[] ArgumentConversions
	);
	
	private const int VoidResultRank = 2;
	
	private readonly record struct CallableCost
	(
		int ResultRank,
		int ConversionCost,
		int Distance,
		int MaterializationCost,
		int ResultCost,
		int GenericRank
	) : IComparable<CallableCost>
	{
		public int CompareTo(CallableCost other)
		{
			var voidComparison = (ResultRank == VoidResultRank).CompareTo(other.ResultRank == VoidResultRank);
			if (voidComparison != 0)
				return voidComparison;
			
			var conversionCostComparison = ConversionCost.CompareTo(other.ConversionCost);
			if (conversionCostComparison != 0)
				return conversionCostComparison;
			
			var materializationCostComparison = MaterializationCost.CompareTo(other.MaterializationCost);
			if (materializationCostComparison != 0)
				return materializationCostComparison;
			
			var distanceComparison = Distance.CompareTo(other.Distance);
			if (distanceComparison != 0)
				return distanceComparison;
			
			var resultRankComparison = ResultRank.CompareTo(other.ResultRank);
			if (resultRankComparison != 0)
				return resultRankComparison;
			
			var resultCostComparison = ResultCost.CompareTo(other.ResultCost);
			return resultCostComparison != 0 ? resultCostComparison : GenericRank.CompareTo(other.GenericRank);
		}
	}
	
	private readonly struct ResolutionSet
	{
		public static ResolutionSet None => default;
		public static ResolutionSet Single(CallableResolution resolution) => new(resolution);
		public static ResolutionSet Ambiguous(params IEnumerable<CallableResolution> resolutions) => new(resolutions);
		
		public int Count { get; }
		public bool IsAmbiguous => Count > 1;
		public bool HasResult => Count > 0;
		public CallableResolution this[int index] => _resolutions.IsDefaultOrEmpty ? default : _resolutions[index];
		
		private readonly ImmutableArray<CallableResolution> _resolutions;
		
		private ResolutionSet(params IEnumerable<CallableResolution> resolutions)
		{
			_resolutions = resolutions.ToImmutableArray();
			Count = _resolutions.Length;
		}
	}
	
	private readonly record struct Expectation(TypeSymbol? Type, bool IsHint);
	
	private readonly record struct LiteralFold(TokenType Op, ImmutableArray<ResolvedLiteralExpressionNode> Operands);
	
	private sealed record CallShape
	(
		IReadOnlyList<TypeSymbol> Parameters,
		ImmutableArray<TypeParameterSymbol> Open,
		TypeSymbol? ReturnType,
		ImmutableArray<ParameterMode> Modes
	)
	{
		public IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> Outer { get; init; } =
			new Dictionary<TypeParameterSymbol, TypeSymbol>();
	}
	
	private sealed record LambdaAnnotations
	(
		ImmutableArray<TypeSymbol?> Types,
		ImmutableArray<ParameterMode?> Modes,
		TypeSymbol? ReturnType
	);
	
	private sealed class LambdaFrame(ResolutionContext outer, FunctionType? target)
	{
		public ResolutionContext Outer { get; } = outer;
		public FunctionType? Target { get; } = target;
		public Dictionary<string, LocalVariableSymbol> Bindings { get; } = [];
		public List<(LocalVariableSymbol Binding, VarExpressionNode Use)> Captures { get; } = [];
		public bool RejectsCaptures { get; set; }
	}
	
	private readonly record struct Redirection
	(
		DiagnosticList List,
		List<Diagnostic>? Previous,
		List<Diagnostic> Captured
	);
	
	private sealed class Checkpoint(int journalLength, List<Redirection> redirections)
	{
		public int JournalLength { get; } = journalLength;
		public List<Redirection> Redirections { get; } = redirections;
		
		public bool HasErrors => Redirections.Any(static redirection =>
			redirection.Captured.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
	}
	
	private readonly record struct TypeArgumentList
	(
		ImmutableArray<TypeSymbol> Types,
		ImmutableArray<SourceLocation> Locations,
		SourceLocation Location
	);
	
	private static string DescribeInstantiated(Symbol owner) => owner switch
	{
		FunctionSymbol { Property: { ContainingType: { } type } property } => $"{type.Name}.{property.Name}",
		FunctionSymbol { Property: { } property } => property.Name,
		_ => owner.Name
	};
	
	private readonly record struct Instantiation
	(
		(Symbol Owner, TypeParameterSymbol Parameter) From,
		(Symbol Owner, TypeParameterSymbol Parameter) To,
		bool IsExpansive,
		SourceLocation Location
	);
	
	private readonly record struct CallForm(int Omitted, TypeSymbol? Element, int Packed);
	
	private static ImmutableArray<TypeSymbol> Expand(ImmutableArray<TypeSymbol> parameters, CallForm? form)
	{
		if (form is not { } shape)
			return parameters;
		
		var fixedCount = parameters.Length - (shape.Element is null ? 0 : 1);
		return
		[
			..parameters[..(fixedCount - shape.Omitted)],
			..shape.Element is { } element ? Enumerable.Repeat(element, shape.Packed) : []
		];
	}
	
	private sealed class FunctionCallable(FunctionInfo info, CallForm? form = null) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public CallForm? Form { get; } = form;
		public bool IsGeneric { get; init; }
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } = Expand(info.Signature.ParameterTypes, form);
		public TypeSymbol ReturnType => Info.Signature.ReturnType;
		public bool IsVariadic => Info.Signature.IsVariadic;
		public bool HasVariadicParameter => Info.Signature.HasVariadicParameter;
		public ParameterMode GetMode(int index) => Info.Signature.GetMode(Unpack(index));
		public string GetParameterName(int index) => Info.Symbol.Parameters[Unpack(index)].Name;
		
		private int Unpack(int index) =>
			Form is null ? index : Math.Min(index, Info.Signature.ParameterTypes.Length - 1);
	}
	
	private sealed class FunctionTypeCallable(FunctionType type, CallForm? form = null) : ICallable
	{
		public FunctionType Type { get; } = type;
		public CallForm? Form { get; } = form;
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } = Expand(type.ParameterTypes, form);
		public TypeSymbol ReturnType => Type.ReturnType;
		public bool IsVariadic => Type.IsVariadic;
		public bool HasVariadicParameter => Type.HasVariadicParameter;
		
		public ParameterMode GetMode(int index) =>
			Type.ParameterModes[Form is null ? index : Math.Min(index, Type.ParameterModes.Length - 1)];
	}
	
	private sealed class ConversionCallable(FunctionInfo info, TypeSymbol target) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public ImmutableArray<TypeSymbol> ParameterTypes => Info.Signature.ParameterTypes;
		public TypeSymbol ReturnType { get; } = target;
		public ParameterMode GetMode(int index) => Info.Signature.GetMode(index);
	}
	
	private sealed class FieldsCallable(ImmutableArray<TypeSymbol> fields, TypeSymbol record) : ICallable
	{
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } = fields;
		public TypeSymbol ReturnType { get; } = record;
		public ParameterMode GetMode(int index) => ParameterMode.Own;
	}
	
	private sealed class ReceiverCallable(FunctionInfo info, TypeSymbol type, CallForm? form = null) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public CallForm? Form { get; } = form;
		public bool IsGeneric { get; init; }
		
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } =
			Expand([..info.Signature.ParameterTypes.Skip(1)], form); // Skip implicit self
		
		public TypeSymbol ReturnType { get; } = type;
		public bool HasVariadicParameter => Info.Signature.HasVariadicParameter;
		public ParameterMode GetMode(int index) => Info.Signature.GetMode(Unpack(index) + 1);
		
		public string? GetParameterName(int index) =>
			Info.Symbol.Syntax is ConstructorConstraintNode ? null : Info.Symbol.Parameters[Unpack(index) + 1].Name;
		
		private int Unpack(int index) =>
			Form is null ? index : Math.Min(index, Info.Signature.ParameterTypes.Length - 2);
	}
}

public interface ICallable
{
	ImmutableArray<TypeSymbol> ParameterTypes { get; }
	TypeSymbol ReturnType { get; }
	bool IsVariadic => false;
	bool HasVariadicParameter => false;
	bool IsGeneric => false;
	ParameterMode GetMode(int index) => ParameterMode.ReadOnly;
	string? GetParameterName(int index) => null;
}