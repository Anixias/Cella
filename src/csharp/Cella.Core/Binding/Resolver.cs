using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class Resolver : IStatementNodeVisitor<IResolvedStatementNode>,
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
	public IReadOnlySet<Symbol> GenericReferences => _genericReferences;
	
	private const int UserConversionCost = 100;
	
	private static readonly NativeConversion _boolPromotion =
		new(NativeSymbols.Bool, NativeSymbols.Int32, ConversionKind.Implicit, 0);
	
	private readonly SymbolTable _symbolTable;
	private readonly SignatureCollector _signatures;
	private readonly ConstantEvaluator _evaluator;
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
	private readonly TypeInference _inference;
	private readonly List<Instantiation> _instantiations = [];
	private readonly HashSet<Symbol> _genericReferences = [];
	private readonly List<ResolvedFunctionNode> _lambdas = [];
	private readonly List<LambdaFrame> _lambdaFrames = [];
	private IExpressionNode? storeTarget;
	private List<Action>? _journal;
	private int _openCheckpoints;
	private int _lambdaCount;
	private LocalSurvey? _survey;
	private IReadOnlyDictionary<ISyntaxNode, TypeSymbol>? _settledTypes;
	private IExpressionNode? _placeTarget;
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	private TypeSymbol? CurrentTargetType => _expectations.TryPeek(out var top) && !top.IsHint ? top.Type : null;
	private TypeSymbol? ExpectedType => _expectations.TryPeek(out var top) ? top.Type : null;
	
	public Resolver(SymbolTable symbolTable, SignatureCollector signatures, TypePool typePool, uint pointerBitSize)
	{
		_typePool = typePool;
		_extSignatureTypes = new(typePool);
		_conversionTable = typePool.ConversionTable;
		_inference = new(typePool);
		_operatorRegistry = typePool.OperatorRegistry;
		_pointerBitSize = pointerBitSize;
		_symbolTable = symbolTable;
		_signatures = signatures;
		_evaluator = signatures.Evaluator;
		
		var ptrBits = (int)pointerBitSize;
		_isizeMinValue = -BigInteger.Pow(2, ptrBits - 1);
		_isizeMaxValue = BigInteger.Pow(2, ptrBits - 1) - 1;
		_usizeMinValue = BigInteger.Zero;
		_usizeMaxValue = BigInteger.Pow(2, ptrBits) - 1;
	}
	
	public ResolvedFileNode Resolve(FileNode root) => (ResolvedFileNode)Visit(root);
	
	public IResolvedExpressionNode ResolveInitializer(IExpressionNode initializer, TypeSymbol type,
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
		return _evaluator.Evaluate(result);
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
		return Error(node.Syntax, $"'{function.SourceLocation.GetText()}' doesn't return a value", type);
	}
	
	public IResolvedDeclarationNode Visit(FieldNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var type = resolutionContext.ResolveType(node.Type);
		var field = (FieldSymbol)_symbolTable.DeclarationSymbols[node];
		
		return new ResolvedFieldNode(field, type, node);
	}
	
	public IResolvedDeclarationNode Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = _signatures.GetImports(file);
		
		var resolutionContext = new ResolutionContext
		{
			File = file,
			Imports = imports,
			Modules = _signatures.Modules,
			TypePool = _typePool,
			Diagnostics = Diagnostics,
			ExtSignatureTypes = _extSignatureTypes,
			EvaluateConstant = EvaluateConstant,
			GenericTypes = []
		};
		
		_importedFunctions.Clear();
		_resolutionContexts.Push(resolutionContext);
		
		var resolvedDeclarations = new List<IResolvedDeclarationNode>(node.Declarations.Length);
		foreach (var declaration in node.Declarations)
			resolvedDeclarations.Add(VisitNode(declaration));
		
		resolvedDeclarations.AddRange(_lambdas.Where(lambda => lambda.FunctionInfo.File == file));
		_lambdas.RemoveAll(lambda => lambda.FunctionInfo.File == file);
		_resolutionContexts.Pop();
		_extSignatureTypes.ReportDestructors(Diagnostics);
		
		var result = new ResolvedFileNode(file, resolvedDeclarations, _importedFunctions.Values, node);
		_importedFunctions.Clear();
		return result;
	}
	
	public IResolvedDeclarationNode Visit(ConstructorNode node) => ResolveFunction(node, node.Body!);
	
	public IResolvedDeclarationNode Visit(DestructorNode node) => ResolveFunction(node, node.Body);
	
	public IResolvedDeclarationNode Visit(FunctionNode node) => ResolveFunction(node, node.Body!);
	
	private ResolvedFunctionNode ResolveFunction(IDeclarationNode node, IStatementNode body)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetFunctionInfo(function);
		
		var resolutionContext = CurrentResolutionContext with
		{
			ContainingFunction = info,
			LocalScope = info.Scope,
			TypeParameters = function.DeclaredTypeParameters
		};
		
		_resolutionContexts.Push(resolutionContext);
		var resolvedBody = InferLocals(() => VisitNode(body));
		_resolutionContexts.Pop();
		
		return new ResolvedFunctionNode(info, resolvedBody, node);
	}
	
	private T InferLocals<T>(Func<T> resolve)
	{
		if (_survey is not null || _settledTypes is not null)
			return resolve();
		
		var genericTypes = CurrentResolutionContext.GenericTypes;
		var cachedTypes = genericTypes?.Keys.ToHashSet();
		var lambdaCount = _lambdaCount;
		var checkpoint = OpenCheckpoint();
		var survey = _survey = new LocalSurvey();
		var surveyed = resolve();
		_survey = null;
		if (!survey.HasLocals)
		{
			Commit(checkpoint);
			return surveyed;
		}
		
		var settlement = survey.Settle(ConvertsInOneStep, CastsInOneStep);
		Rollback(checkpoint);
		_lambdaCount = lambdaCount;
		foreach (var key in genericTypes?.Keys.Except(cachedTypes!).ToList() ?? [])
			genericTypes!.Remove(key);
		
		foreach (var (local, types) in settlement.Conflicts)
			Diagnostics.Add(new(DiagnosticSeverity.Error, local.Identifier.SourceLocation,
				$"Cannot infer the type of '{local.Identifier.Text}' from {JoinNames(types)}"));
		
		_settledTypes = settlement.Types;
		var result = resolve();
		_settledTypes = null;
		return result;
	}
	
	private bool ConvertsInOneStep(TypeSymbol from, TypeSymbol to) =>
		from == to || _conversionTable.FindImplicit(from, to) is not null ||
		FindUserConversion(from, null, ParameterMode.ReadOnly, to) is { IsAmbiguous: false };
	
	private bool CastsInOneStep(TypeSymbol from, TypeSymbol to) =>
		from == to || _conversionTable.FindExplicit(from, to) is not null ||
		FindUserConversion(from, null, ParameterMode.ReadOnly, to, true) is { IsAmbiguous: false } ||
		_typePool.GetConstructors(to).Any(constructor => constructor.Signature.ParameterTypes is [var parameter] &&
		                                                 parameter == from);
	
	private OpenLocal DeclareOpen(OpenLocal local, IEnumerable<IResolvedExpressionNode> values)
	{
		var survey = _survey!;
		local = survey.Declare(local);
		var linked = values
			.SelectMany(value => survey.GetTracked(value))
			.Where(static tracked => tracked.IsWhole)
			.Select(static tracked => tracked.Local)
			.Distinct();
		
		foreach (var other in linked)
			Link(local, other);
		
		return local;
	}
	
	private void Link(OpenLocal local, OpenLocal other)
	{
		if (_survey is not { } survey || local == other)
			return;
		
		survey.Add(new(local, LocalUseKind.Link, null, other));
		Journal(survey.RemoveLast);
	}
	
	private void RecordUse(OpenLocal local, TypeSymbol type, LocalUseKind kind)
	{
		if (_survey is not { } survey || IsInvalid(type) || ContainsUntyped(type))
			return;
		
		survey.Add(new(local, kind, type, null));
		Journal(survey.RemoveLast);
	}
	
	private static bool ContainsUntyped(TypeSymbol type) => type switch
	{
		UntypedType => true,
		PointerType pointer => ContainsUntyped(pointer.BaseType),
		BorrowType borrow => ContainsUntyped(borrow.Target),
		ArrayType array => ContainsUntyped(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(ContainsUntyped),
		_ => type.TypeArguments.Any(ContainsUntyped)
	};
	
	private void RecordMaterialization(IResolvedExpressionNode node, TypeSymbol target)
	{
		if (_survey is not { } survey || survey.IsDefaulted(node))
			return;
		
		var type = target is BorrowType { IsMutable: false } borrow ? borrow.Target : target;
		if (!CouldBecome(node, type))
			return;
		
		foreach (var tracked in survey.GetTracked(node))
			RecordUse(tracked.Local, Wrap(type, tracked.Lengths), LocalUseKind.Read);
	}
	
	private void RecordCast(IResolvedExpressionNode node, TypeSymbol target)
	{
		if (_survey is not { } survey)
			return;
		
		survey.MarkDefaulted(node);
		foreach (var tracked in survey.GetTracked(node).Where(static tracked => tracked.IsWhole))
			RecordUse(tracked.Local, target, LocalUseKind.Cast);
	}
	
	private TypeSymbol Wrap(TypeSymbol type, ImmutableArray<BigInteger> lengths)
	{
		for (var i = lengths.Length - 1; i >= 0; i--)
			type = _typePool.GetArrayType(type, lengths[i]);
		
		return type;
	}
	
	private OpenLocal? FindElement(IResolvedExpressionNode array)
	{
		foreach (var tracked in _survey?.GetTracked(array) ?? [])
		{
			var syntax = tracked.Local.Initializer as ArrayExpressionNode;
			for (var i = 0; i < tracked.Lengths.Length && syntax is not null; i++)
				syntax = syntax.Values is [ArrayExpressionNode inner, ..] ? inner : null;
			
			if (syntax is { Values: [var element, ..] })
				return new(tracked.Local.Declaration, tracked.Local.Identifier, element, tracked.Local.Context)
				{
					Owner = tracked.Local,
					Lengths = [..tracked.Lengths, syntax.Values.Length]
				};
		}
		
		return null;
	}
	
	private static bool CouldBecome(IResolvedExpressionNode node, TypeSymbol type) => node switch
	{
		ResolvedCaseNameExpressionNode => type is EnumSymbol,
		_ => node.Type is UntypedType untyped &&
		     untyped.MaterializationCost(type, MaterializationMode.Overload) != int.MaxValue
	};
	
	private bool IsOpenValue(IResolvedExpressionNode value) =>
		!IsInvalid(value) &&
		value.Type is UntypedType and not (NeverType or FunctionGroupType { Functions.Length: 1 }) ||
		!_survey!.GetTracked(value).IsEmpty;
	
	private bool IsOpenSyntax(IExpressionNode node) => node switch
	{
		LiteralExpressionNode { Token: { Suffix: null, Type: var type } } => type is TokenType.IntegerLiteral
			or TokenType.FloatLiteral or TokenType.StringLiteral or TokenType.KeywordNull,
		InterpolatedStringExpressionNode => true,
		ArrayExpressionNode array => !array.Values.IsEmpty && array.Values.All(IsOpenSyntax),
		LambdaExpressionNode lambda => lambda.Parameters.Any(static parameter => parameter.Type is null),
		UnaryOpExpressionNode { Op.Type: TokenType.OpMinus } unary => IsOpenSyntax(unary.Operand),
		VarExpressionNode name when FindOpenLocal(name) is not null => true,
		_ => IsCaseNameSyntax(node)
	};
	
	private OpenLocal? FindOpenLocal(VarExpressionNode name) =>
		_survey is { } survey && CurrentResolutionContext.Resolve(name.Identifier.Text) is LocalVariableSymbol local
			? survey.Find(local)
			: null;
	
	private bool IsCaseNameSyntax(IExpressionNode node) => GetCaseToken(node) is { } name &&
	                                                       CurrentResolutionContext.Resolve(name.Text) is null;
	
	private static Token? GetCaseToken(IExpressionNode node) => node switch
	{
		VarExpressionNode variable => variable.Identifier,
		CallExpressionNode { Target: VarExpressionNode callee } => callee.Identifier,
		_ => null
	};
	
	private bool NeedsTarget(IExpressionNode initializer) => initializer switch
	{
		ArrayExpressionNode or LambdaExpressionNode => true,
		VarExpressionNode name when FindOpenLocal(name) is { } other => other.IsCaseName ||
		                                                                NeedsTarget(other.Initializer),
		_ => false
	};
	
	private OpenLocal? FindOpenPlace(IResolvedExpressionNode node) =>
		_survey is { } survey && node is ResolvedVarExpressionNode { Symbol: LocalVariableSymbol local }
			? survey.Find(local)
			: null;
	
	private IResolvedExpressionNode Substitute(OpenLocal local)
	{
		var expected = ExpectedType;
		_resolutionContexts.Push(local.Context);
		var value = local switch
		{
			{ IsCaseName: true } => CreateCaseName(GetCaseToken(local.Initializer)!.Value, local.Initializer),
			{ Initializer: LambdaExpressionNode lambda } => ResolveLambda(lambda, expected as FunctionType),
			_ => ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(local.Initializer)
		};
		
		_resolutionContexts.Pop();
		var tracked = local.Tracked;
		if (expected is not null && !IsInvalid(value) && value.Type is not UntypedType)
			RecordUse(tracked.Local,
				Wrap(local.Initializer is LambdaExpressionNode ? expected : value.Type, tracked.Lengths),
				LocalUseKind.Read);
		
		_survey!.Track(value, [tracked]);
		return value;
	}
	
	private ImmutableArray<TrackedLocal> FindStoredLocals(IResolvedExpressionNode node) =>
		FindOpenPlace(node) is { } open ? [open.Tracked] : _survey?.GetTracked(node) ?? [];
	
	private IResolvedExpressionNode RecordStore(BinaryOpExpressionNode node, ImmutableArray<TrackedLocal> places)
	{
		var value = VisitNode(node.Right, null);
		var linked = _survey!.GetTracked(value);
		foreach (var place in places.Where(static place => place.IsWhole))
		{
			foreach (var other in linked.Where(static other => other.IsWhole))
				Link(place.Local, other.Local);
		}
		
		if (linked.IsEmpty && !IsInvalid(value))
		{
			foreach (var place in places)
				RecordUse(place.Local, Wrap(Decay(value).Type, place.Lengths), LocalUseKind.Write);
		}
		
		return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	public IResolvedDeclarationNode Visit(ExternalFunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetFunctionInfo(function);
		return new ResolvedExternalFunctionNode(info, node);
	}
	
	public IResolvedDeclarationNode Visit(GlobalNode node)
	{
		var global = (GlobalSymbol)_symbolTable.DeclarationSymbols[node];
		return new ResolvedGlobalNode(_signatures.GetGlobalInfo(global)!.Value, node);
	}
	
	public IResolvedDeclarationNode Visit(ParameterNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(LambdaDeclarationNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(NativeConstructorNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(ConstructorConstraintNode node) => throw new InvalidOperationException();
	
	public IResolvedDeclarationNode Visit(PropertyNode node) => throw new InvalidOperationException();
	
	private IEnumerable<IResolvedDeclarationNode> VisitMember(IDeclarationNode member) =>
		member is PropertyNode property
			? property.Accessors.Select(accessor => VisitNode(accessor))
			: [VisitNode(member)];
	
	public IResolvedDeclarationNode Visit(RecordNode node)
	{
		var record = (RecordSymbol)_symbolTable.DeclarationSymbols[node];
		var members = new List<IResolvedDeclarationNode>();
		
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = record });
		foreach (var member in node.Members)
			members.AddRange(VisitMember(member));
		
		_resolutionContexts.Pop();
		return new ResolvedRecordNode(record, members, node);
	}
	
	public IResolvedDeclarationNode Visit(EnumNode node)
	{
		var enumType = (EnumSymbol)_symbolTable.DeclarationSymbols[node];
		var members = new List<IResolvedDeclarationNode>();
		
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = enumType });
		foreach (var member in node.Members)
			members.AddRange(VisitMember(member));
		
		_resolutionContexts.Pop();
		return new ResolvedEnumNode(enumType, members, node);
	}
	
	public IResolvedDeclarationNode Visit(TraitNode node)
	{
		var trait = (TraitSymbol)_symbolTable.DeclarationSymbols[node];
		GetDynMembers(trait);
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = trait.Self, Trait = trait });
		var members = node.Members.SelectMany(VisitBodyMember).ToList();
		_resolutionContexts.Pop();
		return new ResolvedTraitNode(trait, members, node);
	}
	
	public IResolvedDeclarationNode Visit(ImplNode node)
	{
		var impl = (ImplSymbol)_symbolTable.DeclarationSymbols[node];
		var target = _signatures.GetImplTarget(impl);
		_resolutionContexts.Push(CurrentResolutionContext with { ContainingType = target, ImplBlock = impl });
		var members = node.Members.SelectMany(VisitBodyMember).ToList();
		_resolutionContexts.Pop();
		return new ResolvedImplNode(impl, target, members, node);
	}
	
	private IEnumerable<IResolvedDeclarationNode> VisitBodyMember(IDeclarationNode member) => member switch
	{
		PropertyNode property => property.Accessors.Where(static a => a.Body is not null).Select(a => VisitNode(a)),
		FunctionNode { Body: not null } function => [VisitNode(function)],
		_ => []
	};
	
	public IResolvedStatementNode Visit(BlockStatementNode node)
	{
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(
			node.StatementNodes.OfType<VarStatementNode>().Select(static v => v.Identifier),
			static name => $"'{name}' is declared more than once in this scope"));
		
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		var scope = CurrentScope?.CreateChild();
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		_resolutionContexts.Push(resolutionContext);
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		_resolutionContexts.Pop();
		
		return new ResolvedBlockStatementNode(statements, node);
	}
	
	public IResolvedExpressionNode Visit(ReturnExpressionNode node)
	{
		if (CurrentResolutionContext.ContainingFunction is not { } function)
			return Error(node, "Returns are only allowed within functions", null);
		
		if (node.Value is not { } expression)
			return new ResolvedReturnExpressionNode(null, node);
		
		var returnType = function.Signature.ReturnType;
		if (returnType != NativeSymbols.Void)
			return new ResolvedReturnExpressionNode(VisitNode(expression, returnType), node);
		
		if (IsInvalid(VisitDiscarded(expression)))
			return new ResolvedInvalidExpressionNode(node);
		
		return Error(node, $"Cannot return a value from '{function.Symbol.Name}'", null, expression);
	}
	
	public IResolvedExpressionNode Visit(BreakExpressionNode node)
	{
		var (label, error) = ResolveLabel(node, node.Label);
		return error ?? (IResolvedExpressionNode)new ResolvedBreakExpressionNode(label, node);
	}
	
	public IResolvedExpressionNode Visit(ContinueExpressionNode node)
	{
		var (label, error) = ResolveLabel(node, node.Label);
		return error ?? (IResolvedExpressionNode)new ResolvedContinueExpressionNode(label, node);
	}
	
	private (LabelSymbol? Label, ResolvedInvalidExpressionNode? Error) ResolveLabel(IExpressionNode node,
		IExpressionNode? labelNode)
	{
		if (labelNode is null)
			return (null, null);
		
		if (labelNode is not VarExpressionNode varExpr)
			return (null, Error(node, "Expression must be a label", null, labelNode));
		
		var name = varExpr.Identifier.Text;
		if (CurrentResolutionContext.Resolve(name) is not { } symbol)
		{
			var diagnostic = DiagnosticReporter.ReportUndefinedSymbol(node, name, GetVisibleSymbolNames());
			return (null, Error(node, diagnostic, null));
		}
		
		return symbol is LabelSymbol label
			? (label, null)
			: (null, Error(node, $"Symbol '{name}' is not a label", null, varExpr));
	}
	
	public IResolvedStatementNode Visit(ExpressionStatementNode node) =>
		new ResolvedExpressionStatementNode(VisitDiscarded(node.ExpressionNode), node);
	
	public IResolvedStatementNode Visit(DropStatementNode node) =>
		new ResolvedDropStatementNode(VisitNode(node.Target, null), node);
	
	public IResolvedExpressionNode Visit(CallExpressionNode node)
	{
		if (node.Target is VarExpressionNode callee && ResolveTargetCase(callee.Identifier, node) is { } targetCase)
			return targetCase;
		
		if (node.Target is AccessExpressionNode access && ResolveEnumType(access.Target) is { } enumType)
			return DeclaresNonCaseMember(enumType, access.Member.Text)
				? VisitStaticCall(node, access, RequireTypeArguments(enumType, access.Target))
				: VisitEnumCase(access, enumType, node);
		
		if (CurrentResolutionContext.TryResolveExpressionAsType(node.Target) is not { } targetType)
			return VisitFunctionCall(node);
		
		if (node.Target is not IndexerExpressionNode &&
		    targetType is RecordSymbol { IsGenericDefinition: true } generic)
			return VisitInferredConstruction(node, generic);
		
		return RequireTypeArguments(targetType, node.Target) is InvalidType
			? new ResolvedInvalidExpressionNode(node, CurrentTargetType)
			: VisitTypeCall(node, targetType);
	}
	
	private EnumSymbol? ResolveEnumType(IExpressionNode node) =>
		CurrentResolutionContext.TryResolveExpressionAsType(node) as EnumSymbol;
	
	private IResolvedExpressionNode VisitEnumCase(AccessExpressionNode access, EnumSymbol enumType,
		CallExpressionNode? call)
	{
		if (enumType.IsGenericDefinition && access.Target is not IndexerExpressionNode)
			return VisitInferredEnumCase(access, enumType, call);
		
		return ResolveCase(access.Member, enumType, call, call is null ? access : call);
	}
	
	private IResolvedExpressionNode ResolveCase(Token name, EnumSymbol enumType, CallExpressionNode? call,
		IExpressionNode node)
	{
		var arguments = call?.Arguments ?? [];
		var enumCase = FindCase(enumType, name);
		ImmutableArray<TypeSymbol> payloadTypes = enumCase is null ? [] : _typePool.GetPayloadTypes(enumType, enumCase);
		
		if (enumCase is null || arguments.Length != payloadTypes.Length || call is not null && payloadTypes.IsEmpty)
		{
			var values = arguments.Select(argument => VisitNode(argument, null)).ToArray();
			if (enumCase is not null && !AnyInvalid(values))
				Diagnostics.Add(ReportPayloadCount(node.SourceLocation, enumType, enumCase));
			
			return new ResolvedInvalidExpressionNode(node, enumType);
		}
		
		var payload = arguments.Select((argument, i) => VisitNode(argument, payloadTypes[i]));
		return new ResolvedEnumCaseExpressionNode(enumType, enumCase, payload, node);
	}
	
	private IResolvedExpressionNode VisitInferredEnumCase(AccessExpressionNode access, EnumSymbol definition,
		CallExpressionNode? call)
	{
		IExpressionNode node = call is null ? access : call;
		var named = definition.Cases.FirstOrDefault(c => c.Name == access.Member.Text);
		var shape = CreateShape(named is null ? [] : _typePool.GetPayloadTypes(definition, named), definition);
		var args = ResolveArguments(call?.Arguments ?? [], [shape], out _, values => AcceptsInferred(shape, values),
			(_, values) => AcceptsInferred(shape, values));
		
		if (FindCase(definition, access.Member) is not { } enumCase || AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var payloadTypes = _typePool.GetPayloadTypes(definition, enumCase);
		if (args.Length != payloadTypes.Length || call is not null && payloadTypes.IsEmpty)
			return Error(node, ReportPayloadCount(node.SourceLocation, definition, enumCase), CurrentTargetType);
		
		var inputs = args.Select((arg, i) => CreateInferenceInput(payloadTypes[i], ParameterMode.Own, arg));
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			ExpectedType);
		
		var name = GetName(access.Target);
		if (InstantiateInferred(result, definition, name, access.Target.SourceLocation) is not EnumSymbol instance)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var instanceCase = instance.Cases[enumCase.Index];
		var instanceTypes = _typePool.GetPayloadTypes(instance, instanceCase);
		var payload = args.Select((arg, i) => CoerceToType(arg, instanceTypes[i])!);
		return new ResolvedEnumCaseExpressionNode(instance, instanceCase, payload, node);
	}
	
	private EnumCaseSymbol? FindCase(EnumSymbol enumType, Token name)
	{
		if (enumType.Cases.FirstOrDefault(c => c.Name == name.Text) is { } enumCase)
			return enumCase;
		
		Diagnostics.Add(DiagnosticReporter.ReportUndefinedCase(name.SourceLocation, enumType.Name, name.Text,
			enumType.Cases.Select(static c => c.Name)));
		
		return null;
	}
	
	private static Diagnostic ReportPayloadCount(SourceLocation location, EnumSymbol enumType,
		EnumCaseSymbol enumCase)
	{
		var name = $"'{enumType.Name}.{enumCase.Name}'";
		var fields = string.Join(", ", enumCase.Fields.Select(static f => f.Name));
		var message = enumCase.Fields.Length switch
		{
			0 => $"{name} holds no values",
			1 => $"{name} holds 1 value: {fields}",
			var count => $"{name} holds {count} values: {fields}"
		};
		
		return new Diagnostic(DiagnosticSeverity.Error, location, message);
	}
	
	private (IResolvedExpressionNode Value, EnumSymbol? Type, DynType? Dyn) ResolveMatchedValue(IExpressionNode node)
	{
		var value = VisitNode(node, null);
		if (value is ResolvedOwnExpressionNode { Type: BorrowType } owned)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Cannot move borrowed values"));
			value = owned.Value;
		}
		
		value = Decay(value);
		if (value.Type is EnumSymbol enumType)
			return (value, enumType, null);
		
		if (value.Type is DynType dyn)
			return (value, null, dyn);
		
		if (!IsInvalid(value))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{value.Type.Name}' is not an enum"));
		
		return (value, null, null);
	}
	
	private IResolvedExpressionNode TakeOwnership(IResolvedExpressionNode value, IExpressionNode syntax,
		EnumSymbol? enumType, bool isMut, IEnumerable<PatternNode?> patterns)
	{
		if (isMut)
			return MakeWritable(value);
		
		return enumType is not null && IsStored(value) && patterns.Any(pattern => MovesPayload(pattern, enumType))
			? new ResolvedOwnExpressionNode(value, syntax)
			: value;
	}
	
	private bool MovesPayload(PatternNode? pattern, EnumSymbol enumType)
	{
		if (pattern is null || enumType.Cases.FirstOrDefault(c => c.Name == pattern.CaseName.Text) is not { } enumCase)
			return false;
		
		var payloadTypes = _typePool.GetPayloadTypes(enumType, enumCase);
		for (var i = 0; i < pattern.BindingModes.Length && i < payloadTypes.Length; i++)
		{
			if (pattern.BindingModes[i]?.Type is TokenType.KeywordOwn or TokenType.KeywordVar &&
			    !_typePool.IsCopy(payloadTypes[i]))
				return true;
		}
		
		return false;
	}
	
	private bool OwnsValue(IResolvedExpressionNode value, EnumSymbol? enumType) =>
		enumType is not null && !_typePool.IsCopy(enumType) && !IsStored(value);
	
	private static bool IsStored(IResolvedExpressionNode value) => value switch
	{
		ResolvedVarExpressionNode or ResolvedGlobalExpressionNode => true,
		ResolvedAccessExpressionNode { Member: FieldSymbol } e => IsStored(e.Target),
		ResolvedIndexerExpressionNode e => IsStored(e.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
		ResolvedAssignmentExpressionNode e => IsStored(e.Left),
		_ => false
	};
	
	private ResolvedPattern? ResolvePattern(PatternNode pattern, EnumSymbol enumType, bool isMut, bool ownsValue)
	{
		if (pattern.TypePath.Length > 0 && !IsEnumName(pattern.TypePath, enumType))
			return null;
		
		if (FindCase(enumType, pattern.CaseName) is not { } enumCase)
			return null;
		
		var payloadTypes = _typePool.GetPayloadTypes(enumType, enumCase);
		if (pattern.Bindings.Length != payloadTypes.Length || pattern.HasParentheses == payloadTypes.IsEmpty)
		{
			Diagnostics.Add(ReportPayloadCount(pattern.SourceLocation, enumType, enumCase));
			return null;
		}
		
		var bindings = CreateBindings(pattern, payloadTypes, isMut, ownsValue);
		ReportRepeatedBindings(bindings.OfType<LocalVariableSymbol>());
		if (enumType.IsMatch)
		{
			foreach (var binding in bindings.OfType<LocalVariableSymbol>().Where(static b => b.IsMutBinding))
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					$"Cannot mutably borrow '{enumType.Name}' payloads"));
		}
		
		return new ResolvedPattern(enumCase, bindings);
	}
	
	private ImmutableArray<LocalVariableSymbol?> CreateBindings(PatternNode pattern, IReadOnlyList<TypeSymbol>? types,
		bool isMut, bool ownsValue) => pattern.TypeBinding is { } typeBinding
		? [CreateBinding(typeBinding, null, NativeSymbols.Invalid, isMut, ownsValue)]
		:
		[
			..pattern.Bindings.Select((token, i) =>
				CreateBinding(token, pattern.BindingModes[i], types?[i] ?? NativeSymbols.Invalid, isMut, ownsValue))
		];
	
	private ResolvedPattern? ResolveTypePattern(PatternNode pattern, DynType dyn, bool isMut)
	{
		if (pattern.HasParentheses)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, pattern.SourceLocation, "Expected a type"));
			return null;
		}
		
		var context = CurrentResolutionContext;
		var typeNode = pattern.Type ?? (pattern.TypePath.IsEmpty
			? new IdentifierTypeNode(pattern.CaseName)
			: new QualifiedTypeNode(pattern.SourceLocation, [..pattern.TypePath, pattern.CaseName]));
		
		var type = context.ResolveType(typeNode);
		if (IsInvalid(type))
			return null;
		
		if (type is DynType)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation, $"Cannot test for '{type.Name}'"));
			return null;
		}
		
		if (!_typePool.Conforms(type, dyn))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation,
				$"'{type.Name}' doesn't implement '{dyn.TraitName}'"));
			
			return null;
		}
		
		var binding = pattern.TypeBinding is { Text: not "_" } token
			? new LocalVariableSymbol(token, _typePool.GetPointerType(type), false)
			{
				IsPatternBinding = true,
				IsBorrowBinding = true,
				IsMutBinding = isMut
			}
			: null;
		
		return new ResolvedPattern(null, [binding]) { TestedType = type };
	}
	
	private LocalVariableSymbol? CreateBinding(Token token, Token? mode, TypeSymbol type, bool isMut, bool ownsValue)
	{
		if (token.Text == "_")
			return null;
		
		var modeType = mode?.Type;
		if (modeType == TokenType.KeywordMut && ownsValue)
			Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation,
				"Cannot mutably borrow unstored values"));
		else if (modeType is TokenType.KeywordOwn or TokenType.KeywordVar && isMut)
			Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation,
				"Cannot move mutably borrowed payloads"));
		
		var isMutBinding = !ownsValue && (isMut || modeType == TokenType.KeywordMut);
		var isBorrowBinding = !ownsValue && (isMutBinding || !_typePool.IsCopy(type));
		var bindingType = isBorrowBinding && type is not InvalidType ? _typePool.GetPointerType(type) : type;
		return new LocalVariableSymbol(token, bindingType, modeType == TokenType.KeywordVar && !isBorrowBinding)
		{
			IsPatternBinding = true,
			IsBorrowBinding = isBorrowBinding,
			IsMutBinding = isMutBinding
		};
	}
	
	private bool IsEnumName(ImmutableArray<Token> typePath, EnumSymbol enumType)
	{
		var context = CurrentResolutionContext;
		var symbol = typePath is [var name] ? context.Resolve(name.Text) : context.ResolveQualifiedName(typePath);
		switch (symbol)
		{
			case not null when symbol == enumType || symbol == enumType.Definition:
				return true;
			
			case null when typePath is [var typeName]:
				var typeNames = context.GetAllSymbols()
					.OfType<TypeSymbol>()
					.Select(static t => t.Name)
					.Distinct();
				
				Diagnostics.Add(context.ReportHidden(typeName.Text, typeName.SourceLocation) ??
				                DiagnosticReporter.ReportUndefinedType(typeName.SourceLocation, typeName.Text,
					                typeNames));
				
				return false;
			
			case null:
				return false;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, ResolutionContext.GetSpan(typePath),
					$"Expected a case of '{enumType.Name}'"));
				
				return false;
		}
	}
	
	private void ReportRepeatedBindings(IEnumerable<LocalVariableSymbol> bindings)
	{
		foreach (var sameName in bindings.GroupBy(static b => b.Name).Where(static g => g.Count() > 1))
		{
			foreach (var binding in sameName)
			{
				if (!_repeatedBindings.Add(binding))
					continue;
				
				Journal(() => _repeatedBindings.Remove(binding));
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					$"'{binding.Name}' is bound more than once"));
			}
		}
	}
	
	private IEnumerable<LocalVariableSymbol> GetTrueBindings(IResolvedExpressionNode condition) =>
		condition switch
		{
			ResolvedIsExpressionNode node => node.Pattern.Bindings.OfType<LocalVariableSymbol>(),
			ResolvedInvalidExpressionNode node when _failedPatterns.TryGetValue(node, out var bindings) =>
				bindings.OfType<LocalVariableSymbol>(),
			ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpAmpersandAmpersand } } node =>
				GetTrueBindings(node.Left).Concat(GetTrueBindings(node.Right)),
			_ => []
		};
	
	private void ReportDeclarationBody(IStatementNode body)
	{
		if (body is VarStatementNode declaration)
			Diagnostics.Add(new(DiagnosticSeverity.Error, declaration.SourceLocation,
				"Declarations cannot be whole bodies"));
	}
	
	private Scope CreateScope(IEnumerable<LocalVariableSymbol?> bindings)
	{
		var scope = CurrentScope?.CreateChild() ?? new Scope();
		foreach (var binding in bindings.OfType<LocalVariableSymbol>())
			scope.Define(binding);
		
		return scope;
	}
	
	private IResolvedStatementNode VisitInScope(IStatementNode node, IEnumerable<LocalVariableSymbol?> bindings)
	{
		_resolutionContexts.Push(CurrentResolutionContext with { LocalScope = CreateScope(bindings) });
		var result = VisitNode(node);
		_resolutionContexts.Pop();
		return result;
	}
	
	private IResolvedExpressionNode VisitInScope(IExpressionNode node, IEnumerable<LocalVariableSymbol?> bindings,
		TypeSymbol? target = null)
	{
		_resolutionContexts.Push(CurrentResolutionContext with { LocalScope = CreateScope(bindings) });
		var result = VisitNode(node, target);
		_resolutionContexts.Pop();
		return result;
	}
	
	public IResolvedExpressionNode Visit(IsExpressionNode node)
	{
		var isMut = node.Mode is not null;
		var (value, enumType, dyn) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, enumType, isMut, [node.Pattern]);
		var ownsValue = OwnsValue(value, enumType);
		if (enumType is not null && ResolvePattern(node.Pattern, enumType, isMut, ownsValue) is { } pattern)
			return new ResolvedIsExpressionNode(value, pattern, isMut || pattern.HasMutBindings, ownsValue, node);
		
		if (dyn is not null && ResolveTypePattern(node.Pattern, dyn, isMut) is { } typePattern)
			return new ResolvedIsExpressionNode(value, typePattern, isMut, false, node);
		
		var invalid = new ResolvedInvalidExpressionNode(node, NativeSymbols.Bool);
		_failedPatterns[invalid] = CreateBindings(node.Pattern, null, isMut, ownsValue);
		return invalid;
	}
	
	public IResolvedStatementNode Visit(MatchStatementNode node)
	{
		var isMut = node.Mode is not null;
		var (value, enumType, dyn) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, enumType, isMut, node.Arms.Select(static arm => arm.Pattern));
		var ownsValue = OwnsValue(value, enumType);
		var arms = new List<ResolvedMatchArm>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var pattern = arm.Pattern is { } syntax ? ResolveArmPattern(syntax, enumType, dyn, isMut, ownsValue) : null;
			var bindings = pattern?.Bindings ??
			               (arm.Pattern is { } failed ? CreateBindings(failed, null, isMut, ownsValue) : []);
			
			ReportDeclarationBody(arm.Body);
			arms.Add(new ResolvedMatchArm(pattern, VisitInScope(arm.Body, bindings)));
			summaries.Add(SummarizeArm(arm.Pattern, pattern, enumType, arm.SourceLocation));
		}
		
		ReportArmConflicts(summaries);
		var borrowsMut = isMut || arms.Any(static arm => arm.Pattern is { HasMutBindings: true });
		return new ResolvedMatchStatementNode(value, arms, borrowsMut, ownsValue, node);
	}
	
	public IResolvedExpressionNode Visit(MatchExpressionNode node)
	{
		var target = CurrentTargetType;
		var isMut = node.Mode is not null;
		var (value, enumType, dyn) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, enumType, isMut, node.Arms.Select(static arm => arm.Pattern));
		var ownsValue = OwnsValue(value, enumType);
		var patterns = new List<ResolvedPattern?>(node.Arms.Length);
		var values = new List<IResolvedExpressionNode>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var pattern = arm.Pattern is { } syntax ? ResolveArmPattern(syntax, enumType, dyn, isMut, ownsValue) : null;
			var bindings = pattern?.Bindings ??
			               (arm.Pattern is { } failed ? CreateBindings(failed, null, isMut, ownsValue) : []);
			
			patterns.Add(pattern);
			values.Add(VisitInScope(arm.Value, bindings, target));
			summaries.Add(SummarizeArm(arm.Pattern, pattern, enumType, arm.SourceLocation));
		}
		
		ReportArmConflicts(summaries);
		if (enumType is not null)
			ReportMissingCases(node.Keyword, enumType, summaries);
		else if (dyn is not null && !summaries.Any(static arm => arm.IsElse))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Keyword.SourceLocation,
				$"This match doesn't handle every value of '{dyn.Name}'"));
		
		var type = target ?? UnifyTypes(values);
		if (type is null)
		{
			var types = string.Join(", ", values.Select(static v => $"'{v.Type.Name}'").Distinct());
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Keyword.SourceLocation,
				$"The arms of this match have incompatible types: {types}"));
			
			type = NativeSymbols.Invalid;
		}
		
		var arms = values.Select((v, i) => new ResolvedMatchExpressionArm(patterns[i], CoerceToType(v, type)));
		var borrowsMut = isMut || patterns.Any(static pattern => pattern is { HasMutBindings: true });
		return new ResolvedMatchExpressionNode(value, arms, borrowsMut, ownsValue, type, node);
	}
	
	private readonly record struct MatchArmSummary
	(
		bool IsElse,
		EnumCaseSymbol? Case,
		BigInteger? Value,
		SourceLocation Location
	)
	{
		public TypeSymbol? Type { get; init; }
	}
	
	private ResolvedPattern? ResolveArmPattern(PatternNode syntax, EnumSymbol? enumType, DynType? dyn, bool isMut,
		bool ownsValue) => enumType is not null ? ResolvePattern(syntax, enumType, isMut, ownsValue)
		: dyn is not null ? ResolveTypePattern(syntax, dyn, isMut)
		: null;
	
	private MatchArmSummary SummarizeArm(PatternNode? syntax, ResolvedPattern? pattern, EnumSymbol? enumType,
		SourceLocation location) => new(syntax is null, pattern?.Case,
		pattern?.Case is { } enumCase ? _typePool.GetCaseValue(enumType!, enumCase) : null, location)
	{
		Type = pattern?.TestedType
	};
	
	private void ReportArmConflicts(IReadOnlyList<MatchArmSummary> arms)
	{
		var elseArms = arms.Where(static arm => arm.IsElse).ToList();
		if (elseArms.Count > 1)
		{
			foreach (var arm in elseArms)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location, "A match can have only one 'else' arm"));
		}
		else if (elseArms.Count == 1 && !arms[^1].IsElse)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, elseArms[0].Location, "'else' must be the last arm"));
		}
		
		var repeatedTypes = arms
			.Where(static arm => arm.Type is not null)
			.GroupBy(static arm => arm.Type)
			.Where(static sameType => sameType.Count() > 1);
		
		foreach (var sameType in repeatedTypes)
		{
			foreach (var arm in sameType)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location,
					$"'{sameType.Key!.Name}' is matched more than once"));
		}
		
		var repeated = arms
			.Where(static arm => arm.Value is not null)
			.GroupBy(static arm => arm.Value)
			.Where(static sameCase => sameCase.Count() > 1);
		
		foreach (var sameCase in repeated)
		{
			List<string> names = [..sameCase.Select(static arm => arm.Case!.Name).Distinct()];
			var message = names is [var name]
				? $"'{name}' is matched more than once"
				: $"{DiagnosticReporter.JoinNames(names)} are the same case";
			
			foreach (var arm in sameCase)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location, message));
		}
	}
	
	private void ReportMissingCases(Token keyword, EnumSymbol enumType, IReadOnlyList<MatchArmSummary> arms)
	{
		if (arms.Any(static arm => arm.IsElse || arm.Case is null))
			return;
		
		if (enumType.IsExternal)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation,
				$"This match doesn't handle every value of '{enumType.Name}'"));
			
			return;
		}
		
		var missing = enumType.Cases
			.Where(enumCase => arms.All(arm => arm.Value != _typePool.GetCaseValue(enumType, enumCase)))
			.DistinctBy(enumCase => _typePool.GetCaseValue(enumType, enumCase))
			.ToList();
		
		if (missing.Count == 0)
			return;
		
		var names = string.Join(", ", missing.Select(static enumCase => enumCase.Name));
		Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation,
			$"This match doesn't handle every case of '{enumType.Name}': {names}"));
	}
	
	private IResolvedExpressionNode VisitTypeCall(CallExpressionNode node, TypeSymbol targetType)
	{
		if (targetType is DynType dyn)
			return VisitDynConversion(node, dyn);
		
		if (targetType is FStrType && node.Arguments is [var text])
			return VisitNode(text, targetType);
		
		if (targetType is RecordSymbol record)
			return _typePool.GetConstructors(record).Count == 0
				? VisitRecordConversion(node, record) ?? VisitRecordConstruction(node, record)
				: VisitConstructorCall(node, record, null);
		
		if (targetType is TypeParameterSymbol { HasNew: true } && node.Arguments.Length == 0)
			return new ResolvedNewExpressionNode(targetType, node);
		
		if (targetType is TypeParameterSymbol && _typePool.GetConstructors(targetType).Count > 0)
			return VisitConstructorCall(node, targetType, null);
		
		if (node.Arguments.Length != 1)
			return VisitConstructorCall(node, targetType, null);
		
		// Don't push targetType; we're trying to find a CAST to targetType, not a targetType itself
		var arg = VisitNode(node.Arguments[0], null);
		RecordCast(arg, targetType);
		
		// If the argument has an invalid type, we don't want to cascade useless errors; assume identity conversion
		if (IsInvalid(arg))
			return new ResolvedConversionExpressionNode(arg, new IdentityConversion(targetType), node);
		
		if (arg is ResolvedFunctionGroupExpressionNode group && targetType is FunctionType)
		{
			var reference = MaterializeFunction(group, targetType);
			return reference == group ? ReportFunctionMismatch(group, targetType) : reference;
		}
		
		if (arg.Type is UntypedType)
		{
			arg = MaterializeExpression(arg, targetType is EnumSymbol { IsMatch: true } matchEnum
				? _typePool.GetMatchedType(matchEnum)
				: targetType);
			
			if (arg.Type is UntypedType && FindUserConversion(arg, targetType, true) is { } literalConversion)
				return ApplyUserConversion(arg, literalConversion, node);
			
			if (arg.Type is UntypedType)
				arg = MaterializeAsDefault(arg);
		}
		
		if (arg.Type == targetType)
			return arg;
		
		var value = targetType is BorrowType ? arg : Decay(arg);
		if (value.Type == targetType)
			return value;
		
		if (_conversionTable.FindExplicit(value.Type, targetType) is { } conversion)
			return new ResolvedConversionExpressionNode(value, conversion, node);
		
		if (FindUserConversion(arg, targetType, true) is { } userConversion)
			return ApplyUserConversion(arg, userConversion, node);
		
		return VisitConstructorCall(node, targetType, arg);
	}
	
	private IResolvedExpressionNode? VisitRecordConversion(CallExpressionNode node, RecordSymbol record)
	{
		if (node.Arguments is not [var argument] || argument is LambdaExpressionNode or BorrowExpressionNode ||
		    record.IsGenericDefinition)
			return null;
		
		var checkpoint = OpenCheckpoint();
		var arg = VisitNode(argument, null);
		var conversions = FindConversionCallables([arg], record);
		if (conversions.Length == 0)
		{
			Rollback(checkpoint);
			return null;
		}
		
		Commit(checkpoint);
		var fields = _typePool.GetMembers(record).OfType<FieldSymbol>().ToArray();
		ICallable[] candidates = fields is [var field]
			? [new FieldsCallable([GetMemberType(field)], record), ..conversions]
			: conversions;
		
		var resolutionSet = PreferConstruction(ResolveCallable(candidates, [arg], MaterializationMode.Overload,
			record));
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Conversion to '{record.Name}' is ambiguous", record, node);
		
		return resolutionSet is { HasResult: true } && resolutionSet[0].Callable is ConversionCallable conversion
			? CallConversion(conversion.Info, ApplyArgumentResolution([arg], resolutionSet[0])[0], node)
			: VisitRecordConstruction(node, record, [arg]);
	}
	
	private IResolvedExpressionNode VisitDynConversion(CallExpressionNode node, DynType dyn)
	{
		if (node.Arguments.Length != 1)
			return VisitConstructorCall(node, dyn, null);
		
		var argument = VisitArgument(node.Arguments[0]);
		if (IsInvalid(argument))
			return new ResolvedInvalidExpressionNode(node, dyn);
		
		if (argument is ResolvedMutArgumentExpressionNode mutArgument)
		{
			var place = Decay(mutArgument.Place);
			if (place.Type == dyn)
				return place;
			
			if (CanErase(place.Type, dyn))
				return Decay(Erase(new ResolvedBorrowExpressionNode(place, _typePool.GetBorrowType(place.Type, true),
					false, mutArgument.Syntax), _typePool.GetBorrowType(dyn, true)));
			
			return Error(node, $"'{place.Type.Name}' doesn't implement '{dyn.TraitName}'", dyn, node.Arguments[0]);
		}
		
		return ConvertToDyn(argument, dyn) ?? Error(node,
			$"'{Decay(argument).Type.Name}' doesn't implement '{dyn.TraitName}'", dyn, node.Arguments[0]);
	}
	
	private static bool IsDynTarget(TypeSymbol type) =>
		type is DynType or BorrowType { Target: DynType } or PointerType { BaseType: DynType };
	
	private bool CanErase(TypeSymbol type, DynType dyn) => type switch
	{
		DynType { Instance: { } from } => dyn.Instance is { } to &&
		                                  _typePool.FindDynPath(from, to) is { IsEmpty: false },
		DynType or InvalidType => false,
		_ => _typePool.Conforms(type, dyn)
	};
	
	private bool CanConvertToDyn(TypeSymbol source, TypeSymbol target) => target switch
	{
		DynType dyn => source == dyn || source switch
		{
			BorrowType { Target: var objectType } => objectType == dyn || CanErase(objectType, dyn),
			var type => CanErase(type, dyn)
		},
		BorrowType { Target: DynType dyn, IsMutable: var isMutable } => source switch
		{
			BorrowType { Target: var objectType } borrow => (borrow.IsMutable || !isMutable) &&
			                                                (objectType == dyn || CanErase(objectType, dyn)),
			var type => !isMutable && (type == dyn || CanErase(type, dyn))
		},
		PointerType { BaseType: DynType dyn } => source is PointerType { BaseType: var baseType } &&
		                                         CanErase(baseType, dyn),
		_ => false
	};
	
	private IResolvedExpressionNode? ConvertToDyn(IResolvedExpressionNode source, TypeSymbol target)
	{
		if (source.Type is UntypedType && CanConvertToDyn(GetDefaultType(source), target))
			source = MaterializeAsDefault(source);
		
		if (!CanConvertToDyn(source.Type, target))
			return null;
		
		var syntax = source.Syntax;
		switch (target)
		{
			case DynType dyn when source.Type == dyn:
				return source;
			
			case DynType dyn when source.Type is BorrowType { Target: var objectType, IsMutable: var isMutable }:
				return Decay(objectType == dyn ? source : Erase(source, _typePool.GetBorrowType(dyn, isMutable)));
			
			case DynType dyn:
				var borrow = _typePool.GetBorrowType(source.Type, false);
				var borrowed = new ResolvedBorrowExpressionNode(source, borrow, true, syntax);
				return Decay(Erase(borrowed, _typePool.GetBorrowType(dyn, false)));
			
			case BorrowType { Target: DynType dyn } borrowTarget when source.Type is BorrowType { Target: var t }:
				return t == dyn
					? new ResolvedConversionExpressionNode(source,
						_conversionTable.FindImplicit(source.Type, borrowTarget)!, syntax)
					: Erase(source, borrowTarget);
			
			case BorrowType { Target: DynType dyn } borrowTarget when source.Type == dyn:
				return new ResolvedBorrowExpressionNode(source, borrowTarget, true, syntax);
			
			case BorrowType borrowTarget:
				return Erase(new ResolvedBorrowExpressionNode(source, _typePool.GetBorrowType(source.Type, false), true,
					syntax), borrowTarget);
			
			default:
				return Erase(source, target);
		}
	}
	
	private IResolvedExpressionNode Erase(IResolvedExpressionNode pointer, TypeSymbol target)
	{
		var objectType = pointer.Type switch
		{
			BorrowType borrow => borrow.Target,
			PointerType p => p.BaseType,
			_ => throw new InvalidOperationException()
		};
		
		var dyn = target switch
		{
			BorrowType { Target: DynType d } => d,
			PointerType { BaseType: DynType d } => d,
			_ => throw new InvalidOperationException()
		};
		
		if (objectType is DynType)
			return new ResolvedConversionExpressionNode(pointer, new DynUpcastConversion(pointer.Type, target),
				pointer.Syntax);
		
		ImmutableArray<FunctionInfo> members = dyn.Trait is { } trait
			? [..GetDynMembers(trait).Select(member => GetFunctionInfo(member, objectType, dyn.TraitArguments))]
			: [];
		
		return new ResolvedConversionExpressionNode(pointer,
			new DynConversion(pointer.Type, target, objectType, members), pointer.Syntax);
	}
	
	private ImmutableArray<FunctionSymbol> GetDynMembers(TraitSymbol trait)
	{
		if (_typePool.FindDynMembers(trait) is { } existing)
			return existing;
		
		ImmutableArray<FunctionInfo> members =
		[
			..trait.Functions
				.Select(static method => method.Function)
				.Concat(trait.Properties.SelectMany(GetAccessors))
				.Where(IsDynCallable)
				.Select(GetFunctionInfo)
		];
		
		_typePool.SetDynMembers(trait, members);
		return _typePool.FindDynMembers(trait)!.Value;
	}
	
	private bool IsDynMember(FunctionSymbol function) =>
		function.Trait is { } trait && GetDynMembers(trait).Contains(function);
	
	private IEnumerable<TraitType> GetDynTraits(DynType dyn) =>
		dyn.Instance is { } trait ? _typePool.GetDynTraits(trait) : [];
	
	private bool IsDynCallable(FunctionSymbol function)
	{
		if (function.Visibility == Visibility.Private || function.Kind != FunctionKind.Method ||
		    !function.DeclaredTypeParameters.IsEmpty)
			return false;
		
		var signature = GetFunctionInfo(function).Signature;
		return signature.ParameterTypes.Length > 0 && signature.GetMode(0) != ParameterMode.Own && !signature
			.ParameterTypes.Skip(1)
			.Append(signature.ReturnType)
			.Any(type => TypePool.FindTypeParameters(type).Contains(function.Trait!.Self));
	}
	
	private IResolvedExpressionNode VisitDynCall(CallExpressionNode node, AccessExpressionNode access,
		IResolvedExpressionNode target, DynType dyn, IndexerExpressionNode? indexer)
	{
		var name = access.Member.Text;
		var member = access.Member.SourceLocation;
		if (GetPropertyMember(dyn, name) is not null)
			return VisitIndirectCall(node, Index(indexer, ResolveAccess(access, target)));
		
		var functions = GetDynTraits(dyn).SelectMany(trait => GetInstanceMethods(trait, name)).ToArray();
		if (functions.Length == 0)
			return Error(node, $"Type '{dyn.Name}' has no member '{name}'", CurrentTargetType, member);
		
		var accessible = functions.Where(method => CanAccess(dyn, method.Function)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(member, name, functions.Select(static method => method.Function)),
				CurrentTargetType);
		
		var callable = accessible.Where(method => IsDynMember(method.Function)).ToArray();
		if (callable.Length == 0)
			return Error(node, accessible.Any(static method => method.HasReceiver)
				? $"Cannot call '{name}' through '{dyn.Name}'"
				: "Cannot use static functions through values", CurrentTargetType, member);
		
		ICallable[] candidates =
		[
			..callable
				.Select(method => GetFunctionInfo(method, dyn))
				.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (indexer is null)
			return ResolveCall(node, name, candidates, target);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, name, candidates, target, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IResolvedExpressionNode VisitRecordConstruction(CallExpressionNode node, RecordSymbol record,
		IResolvedExpressionNode[]? resolvedArgs = null)
	{
		var fields = _typePool.GetMembers(record).OfType<FieldSymbol>().ToArray();
		var hasDefault = fields.All(_typePool.HasDefault);
		if (node.Arguments.Length != fields.Length && (node.Arguments.Length > 0 || !hasDefault))
		{
			var args = resolvedArgs ?? node.Arguments.Select(a => VisitNode(a, null)).ToArray();
			if (AnyInvalid(args))
				return new ResolvedInvalidExpressionNode(node, record);
			
			var names = string.Join(", ", fields.Select(static f => f.Name));
			var diagnostic = new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
				$"No constructor for '{record.Name}' accepts these arguments")
			{
				Hints =
				[
					(fields.Length, hasDefault) switch
					{
						(0, _) => $"'{record.Name}' has no fields",
						(_, true) => $"'{record.Name}' takes no arguments, or one for each field: {names}",
						_ => $"'{record.Name}' takes one argument for each field: {names}"
					}
				]
			};
			
			return Error(node, diagnostic, record);
		}
		
		var values = resolvedArgs is null
			? node.Arguments.Select((a, i) => VisitNode(a, GetMemberType(fields[i]))).ToArray()
			: resolvedArgs.Select((a, i) => CoerceToType(a, GetMemberType(fields[i]))).ToArray();
		
		return new ResolvedRecordExpressionNode(record, fields.Zip(values), node);
	}
	
	private IResolvedExpressionNode VisitInferredConstruction(CallExpressionNode node, RecordSymbol definition)
	{
		var constructors = _typePool.GetConstructors(definition);
		var accessible = constructors.Where(info => CanAccess(definition, info.Symbol.Visibility)).ToArray();
		var fields = _typePool.GetMembers(definition).OfType<FieldSymbol>().ToArray();
		var name = GetName(node.Target);
		var location = node.Target.SourceLocation;
		CallShape[] shapes = constructors.Count > 0
			?
			[
				..accessible.Select(constructor =>
					CreateShape(new ReceiverCallable(constructor, definition), definition.TypeParameters))
			]
			: [CreateShape([..fields.Select(GetMemberType)], definition)];
		
		var args = ResolveArguments(node.Arguments, shapes, out var isAmbiguous, Succeeds, Accepts);
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (isAmbiguous)
			return Error(node, $"Conversion to '{definition.Name}' is ambiguous", definition, node);
		
		if (constructors.Count > 0)
			return ResolveInferredConstructor(node, definition, name, args, constructors, accessible);
		
		if (args.Length != fields.Length && args.Length > 0)
			return VisitRecordConstruction(node, definition, args);
		
		var inputs = args.Length == 0
			? []
			: fields.Select((field, i) => CreateInferenceInput(GetMemberType(field), ParameterMode.Own, args[i]));
		
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			ExpectedType);
		
		if (InstantiateInferred(result, definition, name, location) is not RecordSymbol instance)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		return VisitRecordConstruction(node, instance, args);
		
		bool Succeeds(IResolvedExpressionNode[] values) => constructors.Count > 0
			? ResolveCallable(InstantiateConstructors(values), values, MaterializationMode.Overload).HasResult
			: AcceptsInferred(shapes[0], values);
		
		bool Accepts(int index, IResolvedExpressionNode[] values) => constructors.Count > 0
			? InstantiateConstructor(definition, accessible[index], values, name, location, []) is { } callable &&
			  ResolveCallable([callable], values, MaterializationMode.Overload).HasResult
			: AcceptsInferred(shapes[0], values);
		
		ICallable[] InstantiateConstructors(IResolvedExpressionNode[] values) =>
		[
			..accessible
				.Select(constructor => InstantiateConstructor(definition, constructor, values, name, location, []))
				.OfType<ICallable>()
		];
	}
	
	private IResolvedExpressionNode ResolveInferredConstructor(CallExpressionNode node, RecordSymbol definition,
		string name, IResolvedExpressionNode[] args, IReadOnlyList<FunctionInfo> constructors,
		FunctionInfo[] accessible)
	{
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(node.SourceLocation, "new",
				constructors.Select(static info => info.Symbol)), CurrentTargetType);
		
		var failures = new List<Diagnostic>();
		var location = node.Target.SourceLocation;
		ICallable[] candidates =
		[
			..accessible
				.Select(constructor => InstantiateConstructor(definition, constructor, args, name, location, failures))
				.OfType<ICallable>()
		];
		
		if (candidates.Length == 0 && accessible.Length == 1 && failures is [var failure])
		{
			Diagnostics.Add(failure);
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		return ResolveConstructorCall(node, definition, args, candidates, null);
	}
	
	private ReceiverCallable? InstantiateConstructor(RecordSymbol definition, FunctionInfo constructor,
		IReadOnlyList<IResolvedExpressionNode> args, string name, SourceLocation location, List<Diagnostic> failures)
	{
		var callable = new ReceiverCallable(constructor, definition);
		var inputs = args
			.Take(callable.ParameterTypes.Length)
			.Select((arg, i) => CreateInferenceInput(callable.ParameterTypes[i], callable.GetMode(i), arg));
		
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			ExpectedType);
		
		if (InstantiateInferred(result, definition, name, location, failures) is not { } instance)
			return null;
		
		var info = _typePool.GetConstructors(instance).First(info => info.Symbol == constructor.Symbol);
		return new ReceiverCallable(info, instance);
	}
	
	private NamedTypeSymbol? InstantiateInferred(InferenceResult result, NamedTypeSymbol definition, string name,
		SourceLocation location, List<Diagnostic>? failures = null)
	{
		var diagnostic = result.Succeeded
			? ReportConstraintViolation(definition.TypeParameters, result.Arguments, _ => location)
			: ReportInferenceFailure(result, name, location);
		
		if (diagnostic is null)
			return _typePool.Instantiate(definition, result.Arguments) as NamedTypeSymbol;
		
		if (failures is null)
			Diagnostics.Add(diagnostic);
		else
			failures.Add(diagnostic);
		
		return null;
	}
	
	private IResolvedExpressionNode VisitConstructorCall(CallExpressionNode node, TypeSymbol targetType,
		IResolvedExpressionNode? firstArg)
	{
		var constructors = _typePool.GetConstructors(targetType);
		var ctorCandidates = constructors
			.Where(info => CanAccess(targetType, info.Symbol))
			.Select(info => new ReceiverCallable(info, targetType))
			.ToArray();
		
		var shapes = ctorCandidates.Select(static candidate => CreateShape(candidate, [])).ToArray();
		var args = ResolveArguments(node.Arguments, shapes, out var isAmbiguous,
			values => Accepts(ctorCandidates, values), (index, values) => Accepts([ctorCandidates[index]], values),
			firstArg);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, targetType);
		
		if (ctorCandidates.Length == 0 && constructors.Count > 0)
			return Error(node, ReportHiddenMember(node.SourceLocation, "new",
				constructors.Select(static info => info.Symbol)), targetType);
		
		if (isAmbiguous)
			return Error(node, $"Conversion to '{targetType.Name}' is ambiguous", targetType, node);
		
		return ResolveConstructorCall(node, targetType, args, ctorCandidates, targetType);
		
		bool Accepts(ICallable[] candidates, IResolvedExpressionNode[] values) =>
			ResolveCallable(candidates, values, MaterializationMode.Overload, targetType).HasResult;
	}
	
	private IResolvedExpressionNode ResolveConstructorCall(CallExpressionNode node, TypeSymbol targetType,
		IResolvedExpressionNode[] args, ICallable[] ctorCandidates, TypeSymbol? target)
	{
		ICallable[] candidates = [..ctorCandidates, ..FindConversionCallables(args, targetType)];
		var resolutionSet = PreferConstruction(ResolveCallable(candidates, args, MaterializationMode.Overload, target));
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Conversion to '{targetType.Name}' is ambiguous", targetType, node);
		
		if (!resolutionSet.HasResult)
		{
			if (ReportArgumentModes(ctorCandidates, args, target))
				return new ResolvedInvalidExpressionNode(node, targetType);
			
			var message = args is [not ResolvedCaseNameExpressionNode]
				? $"No constructor for '{targetType.Name}' accepts argument of type '{GetArgumentType(args[0]).Name}'"
				: $"No constructor for '{targetType.Name}' accepts these arguments";
			
			return Error(node, message, targetType, node);
		}
		
		var resolution = resolutionSet[0];
		if (resolution.Callable is ConversionCallable conversion)
			return CallConversion(conversion.Info, ApplyArgumentResolution(args, resolution)[0], node);
		
		var callable = (ReceiverCallable)resolution.Callable;
		var info = callable.Info;
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		var result = new ResolvedConstructorCallExpressionNode(info, resolvedArgs, callable.ReturnType, node);
		
		if (resolution.ResultConversion is { } resultConversion && resultConversion.To != targetType)
			throw new InvalidOperationException(); // Should be impossible
		
		TrackFunctionUse(info, node.Target);
		
		return result;
	}
	
	private IResolvedExpressionNode VisitFunctionCall(CallExpressionNode node)
	{
		var context = CurrentResolutionContext;
		Symbol? symbol;
		switch (node.Target)
		{
			case VarExpressionNode varExpr:
				symbol = context.Resolve(varExpr.Identifier.Text);
				if (symbol is not null)
					break;
				
				return Error(node, ReportUndefinedSymbol(node, varExpr.Identifier.Text), CurrentTargetType);
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				symbol = context.ResolveMember(module, access.Member.Text);
				break;
			
			case AccessExpressionNode access:
				return VisitMemberCall(node, access);
			
			case IndexerExpressionNode indexer:
				return VisitExplicitCall(node, indexer);
			
			default:
				return VisitIndirectCall(node, VisitNode(node.Target, null));
		}
		
		var functionSymbols = GetFunctions(symbol);
		if (functionSymbols.Length == 0)
			return VisitIndirectCall(node, VisitNode(node.Target, null));
		
		var candidates = functionSymbols.Select(GetFunctionInfo).Select(static info => new FunctionCallable(info));
		return ResolveCall(node, GetName(node.Target), [..candidates], null);
	}
	
	private IResolvedExpressionNode VisitExplicitCall(CallExpressionNode node, IndexerExpressionNode indexer)
	{
		var context = CurrentResolutionContext;
		switch (indexer.Target)
		{
			case VarExpressionNode name when GetFunctions(context.Resolve(name.Identifier.Text)) is [_, ..] functions:
				return ResolveExplicitCall(node, indexer, name.Identifier.Text, [..functions.Select(GetFunctionInfo)]);
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				return GetFunctions(context.ResolveMember(module, access.Member.Text)) is [_, ..] members
					? ResolveExplicitCall(node, indexer, GetName(access), [..members.Select(GetFunctionInfo)])
					: VisitIndirectCall(node, VisitNode(indexer, null));
			
			case AccessExpressionNode access:
				return VisitMemberCall(node, access, indexer);
			
			default:
				return VisitIndirectCall(node, VisitNode(indexer, null));
		}
	}
	
	private IResolvedExpressionNode ResolveExplicitCall(CallExpressionNode node, IndexerExpressionNode indexer,
		string name, FunctionInfo[] functions) => ResolveTypeArguments(indexer, functions) is { } typeArguments
		? ResolveCall(node, name, [..functions.Select(static info => new FunctionCallable(info))], null, typeArguments)
		: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	
	private TypeArgumentList? ResolveTypeArguments(IndexerExpressionNode node, IEnumerable<ICallable> candidates) =>
		ResolveTypeArguments(node, candidates.Select(static candidate => candidate switch
		{
			FunctionCallable callable => callable.Info,
			ReceiverCallable callable => callable.Info,
			_ => (FunctionInfo?)null
		}).OfType<FunctionInfo>());
	
	private TypeArgumentList? ResolveTypeArguments(IndexerExpressionNode node, IEnumerable<FunctionInfo> functions)
	{
		var context = CurrentResolutionContext;
		var shapes = functions
			.Select(GetOpenTypeParameters)
			.Where(open => open.Length == node.Arguments.Length)
			.ToList();
		
		var types = node.Arguments
			.Select((argument, i) => context.ResolveGenericArgumentExpression(argument, FindSharedParameter(shapes, i)))
			.ToImmutableArray();
		
		return types.Any(static type => type is InvalidType)
			? null
			: new(types, [..node.Arguments.Select(static argument => argument.SourceLocation)], node.SourceLocation);
	}
	
	private static TypeParameterSymbol?
		FindSharedParameter(List<ImmutableArray<TypeParameterSymbol>> shapes, int index) =>
		shapes is [var first, ..] && shapes.All(shape => shape[index].IsValue == first[index].IsValue &&
		                                                 shape[index].IsTrait == first[index].IsTrait &&
		                                                 shape[index].ValueType == first[index].ValueType)
			? first[index]
			: null;
	
	private ICallable? Specialize(ICallable candidate, string name, IReadOnlyList<IResolvedExpressionNode> args,
		TypeArgumentList? typeArguments, SourceLocation location, List<Diagnostic> failures)
	{
		FunctionInfo info;
		switch (candidate)
		{
			case FunctionCallable callable:
				info = callable.Info;
				break;
			
			case ReceiverCallable callable:
				info = callable.Info;
				break;
			
			default:
				return candidate;
		}
		
		var open = GetOpenTypeParameters(info);
		if (open.IsEmpty && typeArguments is null)
			return candidate;
		
		ImmutableArray<TypeSymbol> arguments;
		Func<int, SourceLocation> locate;
		if (typeArguments is { } explicitArguments)
		{
			if (explicitArguments.Types.Length != open.Length)
			{
				failures.Add(
					ResolutionContext.ReportGenericArgumentCount(explicitArguments.Location, name, open.Length));
				
				return null;
			}
			
			arguments = explicitArguments.Types;
			locate = i => explicitArguments.Locations[i];
		}
		else
		{
			var inputs = args
				.Take(candidate.ParameterTypes.Length)
				.Select((arg, i) => CreateInferenceInput(candidate.ParameterTypes[i], candidate.GetMode(i), arg));
			
			var result = _inference.Infer(open, inputs.OfType<InferenceInput>(), candidate.ReturnType,
				ExpectedType);
			
			if (!result.Succeeded)
			{
				failures.Add(ReportInferenceFailure(result, name, location));
				return null;
			}
			
			arguments = result.Arguments;
			locate = _ => location;
			MarkDefaultedArguments(open, candidate, args);
		}
		
		if (ReportConstraintViolation(open, arguments, locate, info) is { } violation)
		{
			failures.Add(violation);
			return null;
		}
		
		var instantiated = InstantiateDeclared(info, arguments);
		return candidate is FunctionCallable
			? new FunctionCallable(instantiated) { IsGeneric = true }
			: new ReceiverCallable(instantiated, instantiated.Signature.ReturnType) { IsGeneric = true };
	}
	
	private void MarkDefaultedArguments(ImmutableArray<TypeParameterSymbol> open, ICallable candidate,
		IReadOnlyList<IResolvedExpressionNode> args)
	{
		if (_survey is not { } survey)
			return;
		
		var count = Math.Min(args.Count, candidate.ParameterTypes.Length);
		foreach (var parameter in open)
		{
			if (ExpectedType is not null && Mentions(candidate.ReturnType, parameter))
				continue;
			
			var inputs = Enumerable.Range(0, count)
				.Where(i => Mentions(candidate.ParameterTypes[i], parameter))
				.Select(i => args[i])
				.ToList();
			
			if (inputs.All(static input => input.Type is UntypedType))
				inputs.ForEach(survey.MarkDefaulted);
		}
		
		static bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) =>
			TypePool.FindTypeParameters(type).Contains(parameter);
	}
	
	private FunctionInfo InstantiateLike(FunctionInfo info, FunctionInfo model)
	{
		var count = info.Symbol.DeclaredTypeParameters.Length;
		return GetOpenTypeParameters(info).IsEmpty || model.TypeArguments.Length < count
			? info
			: InstantiateDeclared(info, model.TypeArguments.TakeLast(count));
	}
	
	private FunctionInfo? InferConversion(FunctionInfo info, TypeSymbol type, ResolvedLiteralExpressionNode? literal)
	{
		var open = GetOpenTypeParameters(info);
		if (open.IsEmpty)
			return info;
		
		var parameter = info.Signature.ParameterTypes[0];
		IEnumerable<InferenceInput?> inputs =
		[
			literal is null
				? new InferenceInput(parameter, type)
				: CreateInferenceInput(parameter, info.Signature.GetMode(0), literal)
		];
		
		var result = _inference.Infer(open, inputs.OfType<InferenceInput>(), info.Signature.ReturnType, null);
		return result.Succeeded && ReportConstraintViolation(open, result.Arguments, static _ => default, info) is null
			? InstantiateDeclared(info, result.Arguments)
			: null;
	}
	
	private static ImmutableArray<TypeParameterSymbol> GetOpenTypeParameters(FunctionInfo info)
	{
		var declared = info.Symbol.DeclaredTypeParameters;
		if (declared.IsEmpty || info.TypeArguments.IsDefaultOrEmpty)
			return declared;
		
		var offset = info.Symbol.TypeParameters.Length - declared.Length;
		return [..declared.Where((parameter, i) => info.TypeArguments[offset + i] == parameter)];
	}
	
	private FunctionInfo InstantiateDeclared(FunctionInfo info, IEnumerable<TypeSymbol> arguments)
	{
		var symbol = info.Symbol;
		var outerCount = symbol.TypeParameters.Length - symbol.DeclaredTypeParameters.Length;
		var outer = info.TypeArguments.IsDefaultOrEmpty
			? symbol.TypeParameters.Take(outerCount)
			: info.TypeArguments.Take(outerCount);
		
		return _typePool.InstantiateFunction(info, [..outer, ..arguments]);
	}
	
	private InferenceInput? CreateInferenceInput(TypeSymbol parameter, ParameterMode mode, IResolvedExpressionNode arg)
	{
		var type = GetArgumentType(arg);
		if (GetMutTarget(parameter, mode) is { } declared)
			return type is UntypedType or InvalidType ? null : new(declared, type) { IsExact = true };
		
		return arg switch
		{
			ResolvedFunctionGroupExpressionNode { Group.Functions: [var single] } when
				GetOpenTypeParameters(single).IsEmpty => new(parameter, GetNaturalType(single)) { IsExact = true },
			ResolvedCaseNameExpressionNode => null,
			_ when type is NeverType or FunctionGroupType or InvalidType => null,
			_ when type is UntypedType literal => new(parameter, GetDefaultType(arg)) { Literal = literal },
			_ => new(parameter, type)
		};
	}
	
	private TypeSymbol GetDefaultType(IResolvedExpressionNode node) => node.Type switch
	{
		UntypedIntegerType when node is ResolvedLiteralExpressionNode literal =>
			FindDefaultIntegerType(literal) ?? NativeSymbols.Int32,
		UntypedIntegerType => NativeSymbols.Int32,
		UntypedFloatType => NativeSymbols.Float64,
		UntypedNullType => NativeSymbols.VoidPtr,
		UntypedStringType or InterpolatedStringType => NativeSymbols.Str,
		var type => type
	};
	
	private static Diagnostic ReportInferenceFailure(InferenceResult result, string name, SourceLocation location)
	{
		var message = result.Conflicted is { } parameter
			? $"Cannot infer '{parameter.Name}' for '{name}' from {JoinNames(result.Candidates)}"
			: $"Cannot infer {JoinNames(result.Missing)} for '{name}'";
		
		return new(DiagnosticSeverity.Error, location, message);
	}
	
	private static string JoinNames(IEnumerable<TypeSymbol> types) =>
		DiagnosticReporter.JoinNames([..types.Select(static type => type.Name)]);
	
	private Diagnostic? ReportConstraintViolation(ImmutableArray<TypeParameterSymbol> parameters,
		IReadOnlyList<TypeSymbol> arguments, Func<int, SourceLocation> locate, FunctionInfo? owner = null)
	{
		var map = TypePool.CreateMap(parameters, arguments);
		if (owner is { TypeArguments.IsDefaultOrEmpty: false } info)
		{
			var outer = info.Symbol.TypeParameters.Length - info.Symbol.DeclaredTypeParameters.Length;
			for (var i = 0; i < outer; i++)
				map.TryAdd(info.Symbol.TypeParameters[i], info.TypeArguments[i]);
		}
		
		for (var i = 0; i < parameters.Length; i++)
		{
			if (_typePool.FindConstraintViolation(parameters[i], arguments[i], map) is { } message)
				return new(DiagnosticSeverity.Error, locate(i), message);
		}
		
		return null;
	}
	
	private IResolvedExpressionNode ResolveCall(CallExpressionNode node, string functionName, ICallable[] candidates,
		IResolvedExpressionNode? receiver, TypeArgumentList? typeArguments = null)
	{
		var location = (node.Target is IndexerExpressionNode indexer ? indexer.Target : node.Target).SourceLocation;
		var args = ResolveArguments(node.Arguments,
			[..candidates.Select(candidate => GetCallShape(candidate, typeArguments))], out var isAmbiguous,
			Succeeds, Accepts);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (isAmbiguous)
			return Error(node, $"Call to '{functionName}' is ambiguous", CurrentTargetType, node.Target);
		
		var failures = new List<Diagnostic>();
		var specialized = SpecializeAll(args, failures);
		if (specialized.Length == 0 && candidates.Length == 1 && failures is [var failure])
		{
			Diagnostics.Add(failure);
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		candidates = specialized;
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload, CurrentTargetType);
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Call to '{functionName}' is ambiguous", CurrentTargetType, node.Target);
		
		if (!resolutionSet.HasResult && ReportArgumentModes(candidates, args, CurrentTargetType))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		// TODO If only one candidate, we could report the unmatched arguments instead of the whole function?
		if (!resolutionSet.HasResult)
			return Error(node, $"No overload of '{functionName}' accepts these arguments", CurrentTargetType,
				node.Target);
		
		var resolution = resolutionSet[0];
		var info = resolution.Callable switch
		{
			FunctionCallable callable => callable.Info,
			ReceiverCallable callable => callable.Info,
			_ => throw new InvalidOperationException()
		};
		
		var isErased = info.Symbol.Syntax is FunctionNode { When.IsActive: false };
		if (!isErased)
			TrackFunctionUse(info, node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		if (receiver is not null)
			resolvedArgs.Insert(0, CreateReceiver(receiver, info));
		
		var result = new ResolvedFunctionCallExpressionNode(info, resolvedArgs, node);
		return isErased ? new ResolvedErasedCallExpressionNode(result) : ApplyResultResolution(result, resolution);
		
		ICallable[] SpecializeAll(IResolvedExpressionNode[] values, List<Diagnostic> failed) =>
		[
			..candidates
				.Select(candidate => Specialize(candidate, functionName, values, typeArguments, location, failed))
				.OfType<ICallable>()
		];
		
		bool Succeeds(IResolvedExpressionNode[] values) => ResolveCallable(SpecializeAll(values, []), values,
			MaterializationMode.Overload, CurrentTargetType).HasResult;
		
		bool Accepts(int index, IResolvedExpressionNode[] values) =>
			Specialize(candidates[index], functionName, values, typeArguments, location, []) is { } callable &&
			ResolveCallable([callable], values, MaterializationMode.Overload, CurrentTargetType).HasResult;
	}
	
	private IResolvedExpressionNode CreateReceiver(IResolvedExpressionNode receiver, FunctionInfo method) =>
		method.Signature.GetMode(0) == ParameterMode.Mut
			? new ResolvedMutArgumentExpressionNode(MakeWritable(receiver), _typePool.GetPointerType(receiver.Type),
				receiver.Syntax)
			: receiver;
	
	private IResolvedExpressionNode VisitArgument(IExpressionNode node, IEnumerable<TypeSymbol> expected,
		TypeSymbol? hint = null) =>
		VisitCaseName(node, expected) ?? (hint is null ? VisitArgument(node) : VisitHinted(node, hint));
	
	private IResolvedExpressionNode[] ResolveArguments(ImmutableArray<IExpressionNode> nodes,
		IReadOnlyList<CallShape> shapes, out bool isAmbiguous,
		Func<IResolvedExpressionNode[], bool>? succeeds = null,
		Func<int, IResolvedExpressionNode[], bool>? accepts = null, IResolvedExpressionNode? first = null)
	{
		isAmbiguous = false;
		var values = new IResolvedExpressionNode?[nodes.Length];
		var deferred = new List<int>();
		for (var i = 0; i < nodes.Length; i++)
		{
			if (i == 0 && first is not null)
				values[i] = first;
			else if (IsDeferred(nodes[i]))
				deferred.Add(i);
			else
				values[i] = VisitArgument(nodes[i], ParameterTypesAt(shapes, i));
		}
		
		if (deferred.Count == 0)
			return values!;
		
		var checkpoint = OpenCheckpoint();
		foreach (var i in deferred)
			values[i] = nodes[i] is LambdaExpressionNode lambda
				? VisitLambdaArgument(lambda, i, shapes, values)
				: VisitDeferred(nodes[i], i, shapes, [..shapes.Select(shape => ExpectedAt(shape, values, i))]);
		
		var resolved = values.Select(static value => value!).ToArray();
		if (succeeds is null || accepts is null || AnyInvalid(resolved) ||
		    shapes.Count < 2 && shapes.All(static shape => shape.Open.IsEmpty) || succeeds(resolved))
		{
			Commit(checkpoint);
			return resolved;
		}
		
		var retries = FindRetries(nodes, shapes, resolved, deferred, accepts);
		var cheapest = retries.Select(static retry => retry.Cost).DefaultIfEmpty().Min();
		var best = retries.Where(retry => retry.Cost == cheapest).ToList();
		if (best is not [var (shape, hints, _)])
		{
			Commit(checkpoint);
			isAmbiguous = best.Select(static retry => retry.Shape).Distinct().Count() > 1;
			return resolved;
		}
		
		Rollback(checkpoint);
		foreach (var i in deferred)
			resolved[i] = nodes[i] is LambdaExpressionNode lambda
				? VisitLambdaArgument(lambda, i, [shapes[shape]], resolved)
				: VisitDeferred(nodes[i], i, [shapes[shape]], [hints[i]]);
		
		return resolved;
	}
	
	private IResolvedExpressionNode VisitDeferred(IExpressionNode node, int index, IReadOnlyList<CallShape> shapes,
		IReadOnlyList<TypeSymbol?> expected)
	{
		var types = shapes
			.Select((shape, i) => expected[i] ?? (index < shape.Parameters.Count ? shape.Parameters[index] : null))
			.OfType<TypeSymbol>();
		
		var hint = expected.All(static type => type is not null) && expected.Distinct().Count() == 1
			? expected[0]
			: null;
		
		return VisitArgument(node, types, hint is BorrowType { IsMutable: false } borrow ? borrow.Target : hint);
	}
	
	private List<(int Shape, TypeSymbol?[] Hints, int Cost)> FindRetries(ImmutableArray<IExpressionNode> nodes,
		IReadOnlyList<CallShape> shapes, IResolvedExpressionNode[] values, List<int> deferred,
		Func<int, IResolvedExpressionNode[], bool> accepts)
	{
		var settled = values.Select((value, i) => deferred.Contains(i) ? null : value).ToArray();
		var retries = new List<(int Shape, TypeSymbol?[] Hints, int Cost)>();
		for (var index = 0; index < shapes.Count; index++)
		{
			foreach (var hints in FindHintOptions(shapes[index], values, settled, deferred))
			{
				if (!retries.Any(retry => retry.Hints.SequenceEqual(hints)) && Fit(index, hints) is { } cost)
					retries.Add((index, hints, cost));
			}
		}
		
		return retries;
		
		int? Fit(int index, TypeSymbol?[] hints)
		{
			var checkpoint = OpenCheckpoint();
			try
			{
				var trial = values.ToArray();
				foreach (var i in deferred)
					trial[i] = nodes[i] is LambdaExpressionNode lambda
						? VisitLambdaArgument(lambda, i, [shapes[index]], trial, true)
						: VisitDeferred(nodes[i], i, [shapes[index]], [hints[i]]);
				
				return AnyInvalid(trial) || checkpoint.HasErrors || !accepts(index, trial)
					? null
					: deferred.Select(i => LiteralDistance(values[i].Type, trial[i].Type)).Aggregate(0, AddCosts);
			}
			finally
			{
				Rollback(checkpoint);
			}
		}
	}
	
	private static int LiteralDistance(TypeSymbol natural, TypeSymbol chosen) => (natural, chosen) switch
	{
		_ when natural == chosen => 0,
		(NamedTypeSymbol n, NamedTypeSymbol c) when n.Definition == c.Definition =>
			n.TypeArguments.Zip(c.TypeArguments, LiteralDistance).Aggregate(0, AddCosts),
		(ArrayType n, ArrayType c) when n.Length == c.Length && n.LengthParameter == c.LengthParameter =>
			LiteralDistance(n.ElementType, c.ElementType),
		(BorrowType n, BorrowType c) when n.IsMutable == c.IsMutable => LiteralDistance(n.Target, c.Target),
		_ => FindLiteralType(natural)?.MaterializationCost(chosen, MaterializationMode.Overload) ?? int.MaxValue
	};
	
	private static UntypedType? FindLiteralType(TypeSymbol type) => type switch
	{
		IntegerType { Kind: not PrimitiveTypeKind.Char } => UntypedIntegerType.Instance,
		FloatType => UntypedFloatType.Instance,
		StringType => UntypedStringType.Instance,
		PointerType => UntypedNullType.Instance,
		_ => null
	};
	
	private static int AddCosts(int first, int second) =>
		first == int.MaxValue || second == int.MaxValue ? int.MaxValue : first + second;
	
	private IEnumerable<TypeSymbol?[]> FindHintOptions(CallShape shape, IResolvedExpressionNode[] values,
		IResolvedExpressionNode?[] settled, List<int> deferred)
	{
		List<IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>?> choices = [null];
		var result = _inference.Infer(shape.Open, CreateInferenceInputs(shape, values), shape.ReturnType, ExpectedType);
		if (result.Conflicted is { } parameter)
			choices.AddRange(result.Candidates.Select(candidate =>
				new Dictionary<TypeParameterSymbol, TypeSymbol> { [parameter] = candidate }));
		
		foreach (var choice in choices)
		{
			var hints = new TypeSymbol?[values.Length];
			foreach (var i in deferred)
				hints[i] = ExpectedAt(shape, settled, i, choice);
			
			if (deferred.Any(i => hints[i] is not null))
				yield return hints;
		}
	}
	
	private TypeSymbol? ExpectedAt(CallShape shape, IReadOnlyList<IResolvedExpressionNode?> values, int index,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? chosen = null)
	{
		if (index >= shape.Parameters.Count || shape.Modes[index] == ParameterMode.Mut ||
		    shape.Parameters[index] is BorrowType { IsMutable: true })
			return null;
		
		var parameter = shape.Parameters[index];
		var open = TypePool.FindTypeParameters(parameter).Where(shape.Open.Contains).ToList();
		if (open.Count == 0)
			return parameter;
		
		var known = _inference.InferKnown(shape.Open, CreateInferenceInputs(shape, values), shape.ReturnType,
			ExpectedType, chosen);
		
		var map = shape.Open
			.Select((type, i) => (Parameter: type, Type: known[i]))
			.Where(static entry => entry.Type is not null)
			.ToDictionary(static entry => entry.Parameter, static entry => entry.Type!);
		
		return open.All(map.ContainsKey) ? _typePool.Substitute(parameter, map) : null;
	}
	
	private IResolvedExpressionNode VisitLambdaArgument(LambdaExpressionNode node, int index,
		IReadOnlyList<CallShape> shapes, IReadOnlyList<IResolvedExpressionNode?> values, bool isTrial = false)
	{
		var settled = values.Select((value, i) => i == index ? null : value).ToList();
		var annotations = ResolveAnnotations(node);
		var expected = shapes
			.Select(shape => ExpectLambda(shape, settled, index, annotations))
			.OfType<FunctionType>()
			.Distinct()
			.ToList();
		
		var fitting = expected.Where(type => Fits(annotations, type)).ToList();
		var target = fitting is [var single] ? single : expected is [var only] ? only : null;
		return target is null && expected.Count > 1
			? new ResolvedFunctionGroupExpressionNode(new FunctionGroupType("fun", [], "fun"), node)
			: ResolveLambda(node, target, !isTrial, true);
	}
	
	private FunctionType? ExpectLambda(CallShape shape, IReadOnlyList<IResolvedExpressionNode?> values, int index,
		LambdaAnnotations annotations)
	{
		if (index >= shape.Parameters.Count || shape.Modes[index] == ParameterMode.Mut ||
		    GetExpectedFunction(shape.Parameters[index], shape) is not FunctionType parameter)
			return null;
		
		if (!TypePool.FindTypeParameters(parameter).Any(shape.Open.Contains))
			return parameter;
		
		var inputs = CreateInferenceInputs(shape, values).Concat(CreateAnnotationInputs(parameter, annotations));
		var known = _inference.InferKnown(shape.Open, inputs, shape.ReturnType, ExpectedType, fixLiterals: true);
		var map = shape.Open
			.Select((type, i) => (Parameter: type, Type: known[i]))
			.Where(static entry => entry.Type is not null)
			.ToDictionary(static entry => entry.Parameter, static entry => entry.Type!);
		
		return _typePool.Substitute(parameter, map) as FunctionType;
	}
	
	private TypeSymbol GetExpectedFunction(TypeSymbol parameter, CallShape shape) => parameter switch
	{
		BorrowType { IsMutable: false } borrow => borrow.Target,
		TypeParameterSymbol bounded when FindFunctionBound(bounded, shape) is { } bound =>
			_typePool.GetRefFunctionType(bound),
		_ => parameter
	};
	
	private FunctionType? FindFunctionBound(TypeParameterSymbol parameter, CallShape shape) =>
		_typePool.GetFunctionBounds(parameter) is [var bound]
			? bound
			: _typePool.GetParameterBounds(parameter)
				.Select(trait => shape.Outer.GetValueOrDefault(trait))
				.OfType<FunctionType>()
				.FirstOrDefault();
	
	private LambdaAnnotations ResolveAnnotations(LambdaExpressionNode node)
	{
		var checkpoint = OpenCheckpoint();
		var context = CurrentResolutionContext;
		var annotations = new LambdaAnnotations(
			[..node.Parameters.Select(parameter => parameter.Type is { } type ? context.ResolveType(type) : null)],
			[
				..node.Parameters.Select(static parameter =>
					parameter.Mode is { } mode ? SymbolCollector.GetMode(mode) : (ParameterMode?)null)
			],
			node.ReturnType is { } returnType ? context.ResolveType(returnType) : null);
		
		Rollback(checkpoint);
		return annotations;
	}
	
	private IEnumerable<InferenceInput> CreateAnnotationInputs(FunctionType parameter, LambdaAnnotations annotations)
	{
		if (parameter.ParameterTypes.Length != annotations.Types.Length)
			yield break;
		
		for (var i = 0; i < annotations.Types.Length; i++)
		{
			if (annotations.Types[i] is not { } type || type is InvalidType)
				continue;
			
			var mode = annotations.Modes[i] ?? parameter.ParameterModes[i];
			yield return new(parameter.ParameterTypes[i], _typePool.GetPassedType(type, mode)) { IsExact = true };
		}
		
		if (annotations.ReturnType is { } returnType and not InvalidType)
			yield return new(parameter.ReturnType, returnType) { IsExact = true };
	}
	
	private bool Fits(LambdaAnnotations annotations, FunctionType type) =>
		type.ParameterTypes.Length == annotations.Types.Length &&
		annotations.Types.Select((annotated, i) =>
				(annotations.Modes[i] ?? type.ParameterModes[i]) == type.ParameterModes[i] &&
				(annotated is null || !IsKnown(type.ParameterTypes[i]) ||
				 _typePool.GetPassedType(annotated, type.ParameterModes[i]) == type.ParameterTypes[i]))
			.All(static fits => fits) &&
		(annotations.ReturnType is null || !IsKnown(type.ReturnType) || annotations.ReturnType == type.ReturnType);
	
	private IEnumerable<InferenceInput> CreateInferenceInputs(CallShape shape,
		IReadOnlyList<IResolvedExpressionNode?> values) => values
		.Take(shape.Parameters.Count)
		.Select((value, i) => value is null ? null : CreateInferenceInput(shape.Parameters[i], shape.Modes[i], value))
		.OfType<InferenceInput>();
	
	private bool IsDeferred(IExpressionNode node) => node switch
	{
		CallExpressionNode or ArrayExpressionNode or LambdaExpressionNode => true,
		VarExpressionNode name => CurrentResolutionContext.Resolve(name.Identifier.Text) is null ||
		                          FindOpenLocal(name) is { } open && (open.IsCaseName || NeedsTarget(open.Initializer)),
		_ => false
	};
	
	private static IEnumerable<TypeSymbol> ParameterTypesAt(IEnumerable<CallShape> shapes, int index) => shapes
		.Where(shape => index < shape.Parameters.Count)
		.Select(shape => shape.Parameters[index]);
	
	private CallShape GetCallShape(ICallable candidate, TypeArgumentList? typeArguments)
	{
		FunctionInfo? info = candidate switch
		{
			FunctionCallable callable => callable.Info,
			ReceiverCallable callable => callable.Info,
			_ => null
		};
		
		var open = info is { } declared ? GetOpenTypeParameters(declared) : [];
		if (info is not { } function || open.IsEmpty || typeArguments is not { } explicitArguments ||
		    explicitArguments.Types.Length != open.Length)
			return CreateShape(candidate, open);
		
		var instantiated = InstantiateDeclared(function, explicitArguments.Types);
		return CreateShape(candidate is FunctionCallable
			? new FunctionCallable(instantiated)
			: (ICallable)new ReceiverCallable(instantiated, instantiated.Signature.ReturnType), []);
	}
	
	private static CallShape CreateShape(ICallable candidate, ImmutableArray<TypeParameterSymbol> open) => new(
		candidate.ParameterTypes, open, candidate.ReturnType,
		[..candidate.ParameterTypes.Select((_, i) => candidate.GetMode(i))])
	{
		Outer = candidate switch
		{
			FunctionCallable callable => GetOuterArguments(callable.Info),
			ReceiverCallable callable => GetOuterArguments(callable.Info),
			_ => []
		}
	};
	
	private static Dictionary<TypeParameterSymbol, TypeSymbol> GetOuterArguments(FunctionInfo info)
	{
		var outer = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		if (info.TypeArguments.IsDefaultOrEmpty)
			return outer;
		
		var count = info.Symbol.TypeParameters.Length - info.Symbol.DeclaredTypeParameters.Length;
		for (var i = 0; i < count && i < info.TypeArguments.Length; i++)
			outer[info.Symbol.TypeParameters[i]] = info.TypeArguments[i];
		
		return outer;
	}
	
	private static CallShape CreateShape(ImmutableArray<TypeSymbol> parameters, NamedTypeSymbol definition) =>
		new(parameters, definition.TypeParameters, definition, [..parameters.Select(static _ => ParameterMode.Own)]);
	
	private bool AcceptsInferred(CallShape shape, IResolvedExpressionNode[] args)
	{
		if (args.Length != shape.Parameters.Count)
			return false;
		
		var result = _inference.Infer(shape.Open, CreateInferenceInputs(shape, args), shape.ReturnType, ExpectedType);
		if (!result.Succeeded ||
		    ReportConstraintViolation(shape.Open, result.Arguments, _ => SourceLocation.None) is not null)
			return false;
		
		var map = TypePool.CreateMap(shape.Open, result.Arguments);
		return args
			.Select((arg, i) => MatchArg(arg, _typePool.Substitute(shape.Parameters[i], map), shape.Modes[i],
				MaterializationMode.Overload, false).Cost)
			.All(static cost => cost != int.MaxValue);
	}
	
	private IResolvedExpressionNode VisitHinted(IExpressionNode node, TypeSymbol hint)
	{
		_expectations.Push(new(hint, true));
		try
		{
			var result = ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(node);
			return result.Type == NativeSymbols.Void ? ReportVoidValue(result, null) : result;
		}
		finally
		{
			_expectations.Pop();
		}
	}
	
	private IResolvedExpressionNode VisitOperand(IExpressionNode node, IEnumerable<ICallable> candidates, int index) =>
		VisitCaseName(node, ParameterTypesAt(candidates, index)) ?? VisitNode(node, null);
	
	private ResolvedCaseNameExpressionNode? VisitCaseName(IExpressionNode node, IEnumerable<TypeSymbol> expected)
	{
		var name = node switch
		{
			VarExpressionNode variable => variable.Identifier,
			CallExpressionNode { Target: VarExpressionNode callee } => callee.Identifier,
			_ => default(Token?)
		};
		
		return name is { } token && expected.Any(type => HasCase(type, token.Text))
			? CreateCaseName(token, node)
			: null;
	}
	
	private ResolvedCaseNameExpressionNode CreateCaseName(Token name, IExpressionNode node)
	{
		var context = CurrentResolutionContext;
		var near = context.ResolveNear(name.Text);
		return new ResolvedCaseNameExpressionNode(name, near ?? context.Resolve(name.Text), near is not null, node);
	}
	
	private static IEnumerable<TypeSymbol> ParameterTypesAt(IEnumerable<ICallable> candidates, int index) => candidates
		.Where(candidate => index < candidate.ParameterTypes.Length)
		.Select(candidate => candidate.ParameterTypes[index]);
	
	private static bool HasCase(TypeSymbol? type, string name) =>
		type is EnumSymbol enumType && enumType.Cases.Any(enumCase => enumCase.Name == name);
	
	private IResolvedExpressionNode? ResolveTargetCase(Token name, IExpressionNode node)
	{
		if (CurrentTargetType is not EnumSymbol target || !HasCase(target, name.Text))
			return null;
		
		var caseName = CreateCaseName(name, node);
		return ChooseCase(target, caseName) == CaseChoice.Symbol ? null : ResolveCaseName(caseName, target);
	}
	
	private IResolvedExpressionNode ResolveCaseName(ResolvedCaseNameExpressionNode caseName, TypeSymbol target)
	{
		RecordMaterialization(caseName, target);
		if (target is not EnumSymbol enumType)
			return GetFallback(caseName);
		
		return ChooseCase(enumType, caseName) switch
		{
			CaseChoice.Case => ResolveCase(caseName.Name, enumType, caseName.Syntax as CallExpressionNode,
				caseName.Syntax),
			CaseChoice.Ambiguous => Error(caseName.Syntax, $"'{caseName.Name.Text}' is ambiguous", target,
				caseName.Name.SourceLocation),
			_ => GetFallback(caseName)
		};
	}
	
	private CaseChoice ChooseCase(EnumSymbol enumType, ResolvedCaseNameExpressionNode caseName)
	{
		if (!HasCase(enumType, caseName.Name.Text))
			return CaseChoice.Symbol;
		
		if (caseName.Symbol is not { } symbol)
			return CaseChoice.Case;
		
		if (!IsViableCase(enumType, caseName))
			return CaseChoice.Symbol;
		
		return caseName.IsNear && CouldProduce(symbol, caseName.Syntax is CallExpressionNode, enumType)
			? CaseChoice.Ambiguous
			: CaseChoice.Case;
	}
	
	private static bool IsViableCase(EnumSymbol enumType, ResolvedCaseNameExpressionNode caseName) =>
		enumType.Cases.FirstOrDefault(enumCase => enumCase.Name == caseName.Name.Text) is { } enumCase &&
		(caseName.Syntax is CallExpressionNode call
			? !enumCase.Fields.IsEmpty && call.Arguments.Length == enumCase.Fields.Length
			: enumCase.Fields.IsEmpty);
	
	private IResolvedExpressionNode GetFallback(ResolvedCaseNameExpressionNode caseName)
	{
		if (!_caseNameFallbacks.TryGetValue(caseName, out var fallback))
			_caseNameFallbacks[caseName] = fallback = VisitNode(caseName.Syntax, null);
		
		return fallback;
	}
	
	private IResolvedExpressionNode ResolveUnmatched(IResolvedExpressionNode node) =>
		node is ResolvedCaseNameExpressionNode caseName ? Decay(GetFallback(caseName)) : node;
	
	private bool CouldProduce(Symbol symbol, bool isCall, TypeSymbol target)
	{
		var valueType = GetValueType(symbol);
		if (!isCall)
			return valueType is not null && CouldConvert(valueType, target);
		
		return GetFunctions(symbol)
			       .Any(function => CouldConvert(GetFunctionInfo(function).Signature.ReturnType, target)) ||
		       valueType is FunctionType functionType && CouldConvert(functionType.ReturnType, target);
	}
	
	private bool CouldConvert(TypeSymbol type, TypeSymbol target)
	{
		if (type is BorrowType borrow)
			type = borrow.Target;
		
		return type == target || _conversionTable.FindImplicit(type, target) is not null ||
		       FindUserConversion(type, null, ParameterMode.ReadOnly, target) is not null ||
		       TypePool.ContainsTypeParameters(type) && type.OriginalDefinition == target.OriginalDefinition;
	}
	
	private TypeSymbol? GetValueType(Symbol symbol) => symbol switch
	{
		LocalVariableSymbol { IsBorrowBinding: true, Type: PointerType pointer } => pointer.BaseType,
		LocalVariableSymbol local => local.Type,
		GlobalSymbol global => _signatures.GetGlobalType(global),
		ParameterSymbol { Mode: ParameterMode.Mut } parameter when
			_signatures.GetVariableType(parameter) is PointerType pointer => pointer.BaseType,
		VariableSymbol variable => _signatures.GetVariableType(variable),
		PropertySymbol { Getter: FunctionAccessor { Function: var getter } } property =>
			GetFunctionInfo(getter, property.ContainingType!, property.TraitArguments).Signature.ReturnType,
		_ => null
	};
	
	private IResolvedExpressionNode VisitArgument(IExpressionNode node)
	{
		if (node is not BorrowExpressionNode { IsMutable: true } argument)
			return VisitNode(node, null);
		
		var outerTarget = _placeTarget;
		_placeTarget = argument.Value;
		var place = VisitNode(argument.Value, null);
		_placeTarget = outerTarget;
		if (IsInvalid(place))
			return new ResolvedInvalidExpressionNode(argument);
		
		if (place.Type is UntypedType)
			place = MaterializeAsDefault(place);
		
		return new ResolvedMutArgumentExpressionNode(MakeWritable(place), _typePool.GetPointerType(place.Type),
			argument);
	}
	
	public IResolvedExpressionNode Visit(OwnExpressionNode node)
	{
		var isPlace = node.Value is VarExpressionNode or AccessExpressionNode or IndexerExpressionNode
			              or UnaryOpExpressionNode { Op.Type: TokenType.OpStar }
		              || node.Value is BinaryOpExpressionNode binary && IsAssignment(binary.Op.Type);
		
		var value = VisitNode(node.Value, isPlace ? null : CurrentTargetType);
		if (IsInvalid(value))
			return value;
		
		if (isPlace && value.Type is not UntypedType)
			return new ResolvedOwnExpressionNode(value, node);
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.Value.SourceLocation,
			"Cannot move unstored values"));
		
		return value;
	}
	
	public IResolvedExpressionNode Visit(AtomicExpressionNode node)
	{
		if (node.Place is not UnaryOpExpressionNode { Operand: var operand })
			return node.Ordering == AtomicOrdering.Relaxed
				? Error(node, "Cannot use 'relaxed' on fences", null, node.OrderingLocation!.Value)
				: new ResolvedAtomicExpressionNode(AtomicAccess.Fence, node.Ordering, null, null, null, null,
					NativeSymbols.Void, node);
		
		var resolved = VisitNode(operand, null);
		var pointer = Decay(resolved.Type is UntypedType ? MaterializeAsDefault(resolved) : resolved);
		if (IsInvalid(pointer))
			return RejectAtomic(node, new ResolvedInvalidExpressionNode(node));
		
		if (pointer.Type is not PointerType { BaseType: var type } || type == NativeSymbols.Void)
			return RejectAtomic(node, Error(node, $"Cannot access values atomically through '{resolved.Type.Name}'",
				null, operand));
		
		if (!_typePool.IsAtomic(type))
			return RejectAtomic(node, Error(node, $"Cannot access '{type.Name}' values atomically", null, node.Place));
		
		var access = node.Op?.Type switch
		{
			null => AtomicAccess.Load,
			TokenType.OpEqual => AtomicAccess.Store,
			TokenType.OpEqualEqual => AtomicAccess.CompareSwap,
			_ => AtomicAccess.Modify
		};
		
		if (FindOrderingError(access, node.Ordering) is { } orderingError)
			return RejectAtomic(node, Error(node, orderingError, null, node.OrderingLocation!.Value));
		
		switch (access)
		{
			case AtomicAccess.Load:
				return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, null, null, null, type, node);
			
			case AtomicAccess.Store:
				var stored = VisitNode(node.Value!, type);
				return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, null, null, stored, type, node);
			
			case AtomicAccess.CompareSwap:
				var expected = VisitNode(node.Expected!, type);
				var desired = VisitNode(node.Value!, type);
				return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, null, expected, desired,
					NativeSymbols.Bool, node);
		}
		
		var op = node.Op!.Value;
		if (FindAtomicOperation(op.Type, type) is not { } operation)
			return RejectAtomic(node, Error(node, op.Type is TokenType.OpPlusEqual or TokenType.OpMinusEqual
				or TokenType.OpAmpersandEqual or TokenType.OpBarEqual or TokenType.OpHatEqual
				? $"Cannot use '{op.Text}' atomically on '{type.Name}'"
				: $"Cannot use '{op.Text}' atomically", null, op.SourceLocation));
		
		var amount = VisitNode(node.Value!, type);
		return new ResolvedAtomicExpressionNode(access, node.Ordering, pointer, operation, null, amount, type, node);
	}
	
	private ResolvedInvalidExpressionNode RejectAtomic(AtomicExpressionNode node, ResolvedInvalidExpressionNode error)
	{
		if (node.Expected is { } expected)
			VisitNode(expected, null);
		
		if (node.Value is { } value)
			VisitNode(value, null);
		
		return error;
	}
	
	private static string? FindOrderingError(AtomicAccess access, AtomicOrdering ordering) => (access, ordering) switch
	{
		(AtomicAccess.Load, AtomicOrdering.Release or AtomicOrdering.AcquireRelease) =>
			$"Cannot use '{DescribeOrdering(ordering)}' on atomic loads",
		(AtomicAccess.Store, AtomicOrdering.Acquire or AtomicOrdering.AcquireRelease) =>
			$"Cannot use '{DescribeOrdering(ordering)}' on atomic stores",
		_ => null
	};
	
	private static string DescribeOrdering(AtomicOrdering ordering) => ordering switch
	{
		AtomicOrdering.Relaxed => "relaxed",
		AtomicOrdering.Acquire => "acquire",
		AtomicOrdering.Release => "release",
		_ => "acquire release"
	};
	
	private static BinaryOperation? FindAtomicOperation(TokenType op, TypeSymbol type) => (op, type) switch
	{
		(TokenType.OpPlusEqual, IntegerType or FloatType) => BinaryOperation.Addition,
		(TokenType.OpMinusEqual, IntegerType or FloatType) => BinaryOperation.Subtraction,
		(TokenType.OpAmpersandEqual, IntegerType or PrimitiveType { Kind: PrimitiveTypeKind.Bool }) =>
			BinaryOperation.BitwiseAnd,
		(TokenType.OpBarEqual, IntegerType or PrimitiveType { Kind: PrimitiveTypeKind.Bool }) =>
			BinaryOperation.BitwiseOr,
		(TokenType.OpHatEqual, IntegerType or PrimitiveType { Kind: PrimitiveTypeKind.Bool }) =>
			BinaryOperation.BitwiseXor,
		_ => null
	};
	
	public IResolvedExpressionNode Visit(BorrowExpressionNode node)
	{
		var place = VisitNode(node.Value, null);
		if (!IsInvalid(place) && place.Type is UntypedType)
			place = MaterializeAsDefault(place);
		
		if (IsInvalid(place))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		place = Decay(place);
		if (node.IsMutable)
			place = MakeWritable(place);
		
		return new ResolvedBorrowExpressionNode(place, _typePool.GetBorrowType(place.Type, node.IsMutable), false,
			node);
	}
	
	private bool ReportArgumentModes(IReadOnlyList<ICallable> candidates, IReadOnlyList<IResolvedExpressionNode> args,
		TypeSymbol? target)
	{
		if (ReportMovedFunctionParameters(candidates, args))
			return true;
		
		var relaxed = ResolveCallable(candidates, args, MaterializationMode.Overload, target, true);
		if (relaxed.Count != 1)
			return false;
		
		var diagnostics = args
			.Select((arg, i) => ReportArgumentMode(relaxed[0].Callable, i, arg))
			.OfType<Diagnostic>()
			.ToList();
		
		Diagnostics.AddRange(diagnostics);
		return diagnostics.Count > 0;
	}
	
	private bool ReportMovedFunctionParameters(IReadOnlyList<ICallable> candidates,
		IReadOnlyList<IResolvedExpressionNode> args)
	{
		if (candidates is not [var callable])
			return false;
		
		var diagnostics = args
			.Where((arg, i) => i < callable.ParameterTypes.Length &&
			                   IsMovedFunctionParameter(arg, callable.ParameterTypes[i]))
			.Select(static arg => new Diagnostic(DiagnosticSeverity.Error, arg.Syntax.SourceLocation,
				"Cannot move read-only parameters"))
			.ToList();
		
		Diagnostics.AddRange(diagnostics);
		return diagnostics.Count > 0;
	}
	
	private Diagnostic? ReportArgumentMode(ICallable callable, int index, IResolvedExpressionNode arg)
	{
		var argument = arg as ResolvedMutArgumentExpressionNode;
		var hasParameter = index < callable.ParameterTypes.Length;
		var parameterType = hasParameter ? callable.ParameterTypes[index] : null;
		var isMut = hasParameter && (callable.GetMode(index) == ParameterMode.Mut ||
		                             parameterType is BorrowType { IsMutable: true });
		
		var name = hasParameter ? callable.GetParameterName(index) : null;
		
		if (argument is null)
			return isMut && !(arg.Type is BorrowType && parameterType is BorrowType)
				? new(DiagnosticSeverity.Error, arg.Syntax.SourceLocation, "Cannot mutably borrow arguments implicitly")
				: null;
		
		var keyword = ((BorrowExpressionNode)argument.Syntax).Keyword;
		if (!isMut)
			return new(DiagnosticSeverity.Error, keyword.SourceLocation, name is null
				? "Only an argument to a 'mut' parameter can be marked 'mut'"
				: $"'{name}' isn't a 'mut' parameter");
		
		if (GetMutTarget(parameterType, callable.GetMode(index)) is not { } declared ||
		    IsMutPlaceOf(argument.Place, declared))
			return null;
		
		return new(DiagnosticSeverity.Error, argument.Place.Syntax.SourceLocation,
			$"Cannot mutably borrow '{argument.Place.Type.Name}' values as '{declared.Name}'");
	}
	
	private static TypeSymbol? GetMutTarget(TypeSymbol? parameterType, ParameterMode mode) => parameterType switch
	{
		PointerType { BaseType: var declared } when mode == ParameterMode.Mut => declared,
		BorrowType { IsMutable: true, Target: var target } => target,
		_ => null
	};
	
	private bool IsMutPlaceOf(IResolvedExpressionNode place, TypeSymbol declared) =>
		place.Type == declared || Decay(place).Type == declared ||
		declared is DynType dyn && CanErase(Decay(place).Type, dyn);
	
	private static TypeSymbol GetArgumentType(IResolvedExpressionNode arg) =>
		arg is ResolvedMutArgumentExpressionNode argument ? argument.Place.Type : arg.Type;
	
	private IResolvedExpressionNode VisitMemberCall(CallExpressionNode node, AccessExpressionNode access,
		IndexerExpressionNode? indexer = null)
	{
		if (FindTraitView(access.Target) is var (trait, view))
			return VisitTraitCall(node, access, trait, view, indexer);
		
		if (CurrentResolutionContext.TryResolveExpressionAsType(access.Target) is { } type)
			return VisitStaticCall(node, access, RequireTypeArguments(type, access.Target), indexer);
		
		if (access.Member.Type != TokenType.Identifier)
			return Error(node, $"Cannot call '{access.Member.Text}' through a value", CurrentTargetType,
				access.Member.SourceLocation);
		
		var target = Decay(VisitNode(access.Target, null));
		if (IsInvalid(target))
			return VisitIndirectCall(node, target);
		
		if (target.Type is FStrType fstr && FindFStrPart(access.Member.Text) is { } part)
			return ResolveFStrPart(node, access, target, fstr, part, indexer);
		
		var owner = GetMemberOwner(target.Type, access.Member.Text);
		if (owner is DynType dyn)
			return VisitDynCall(node, access,
				owner == target.Type ? target : ResolveDereference(TokenType.OpStar, target, access.Target), dyn,
				indexer);
		
		if (FindField(target.Type, access.Member.Text) is not null ||
		    GetPropertyMember(owner, access.Member.Text) is not null)
			return VisitIndirectCall(node, Index(indexer, ResolveAccess(access, target)));
		
		var functions = FindFunctions(owner, access.Member.Text);
		var methods = functions.Where(static function => function.HasReceiver).ToArray();
		if (methods.Length == 0)
			return Error(node, DescribeMissingMember(owner, access.Member.Text), CurrentTargetType,
				access.Member.SourceLocation);
		
		var accessible = methods.Where(method => CanAccess(owner, method.Function)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(access.Member.SourceLocation, access.Member.Text,
				methods.Select(static method => method.Function)), CurrentTargetType);
		
		if (owner != target.Type)
			target = ResolveDereference(TokenType.OpStar, target, access.Target);
		
		var candidates = accessible
			.Select(method => GetFunctionInfo(method, owner))
			.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType));
		
		if (indexer is null)
			return ResolveCall(node, access.Member.Text, [..candidates], target);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, access.Member.Text, [..candidates], target, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private static FStrPart? FindFStrPart(string name) => name switch
	{
		"piece" => FStrPart.Piece,
		"spec" => FStrPart.Spec,
		"value" => FStrPart.Value,
		_ => null
	};
	
	private IResolvedExpressionNode ResolveFStrPart(CallExpressionNode node, AccessExpressionNode access,
		IResolvedExpressionNode target, FStrType type, FStrPart part, IndexerExpressionNode? indexer)
	{
		if (indexer is not null || node.Arguments is not [var argument] || argument is BorrowExpressionNode)
		{
			foreach (var extra in node.Arguments)
				VisitArgument(extra);
			
			return Error(node, $"No overload of '{access.Member.Text}' accepts these arguments", CurrentTargetType,
				node.Target);
		}
		
		var index = VisitNode(argument, NativeSymbols.UIntSize);
		if (IsInvalid(index))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		TypeSymbol resultType = part == FStrPart.Value ? _typePool.GetBorrowType(type.Value, false) : NativeSymbols.Str;
		return new ResolvedFStrPartExpressionNode(resultType, target, part, index, node);
	}
	
	private IResolvedExpressionNode Index(IndexerExpressionNode? indexer, IResolvedExpressionNode target) =>
		indexer is null ? target : ResolveIndexing(indexer, target);
	
	private IResolvedExpressionNode VisitStaticCall(CallExpressionNode node, AccessExpressionNode access,
		TypeSymbol type, IndexerExpressionNode? indexer = null)
	{
		if (type is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (type.GetStaticField(access.Member.Text) is not null ||
		    GetPropertyMember(type, access.Member.Text) is not null)
			return VisitIndirectCall(node, Index(indexer, VisitStaticMember(access, type)));
		
		var statics = FindStatics(type, access.Member.Text);
		if (statics.Length == 0)
			return Error(node, DescribeMissingStatic(type, access.Member.Text), CurrentTargetType,
				access.Member.SourceLocation);
		
		var accessible = FindAccessible(type, statics);
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(access.Member.SourceLocation, access.Member.Text,
				statics.Select(static method => method.Function)), CurrentTargetType);
		
		var candidates = accessible
			.Select(method => GetFunctionInfo(method, type))
			.Select(static info => new FunctionCallable(info));
		
		if (indexer is null)
			return ResolveCall(node, GetName(access), [..candidates], null);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, GetName(access), [..candidates], null, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IResolvedExpressionNode VisitStaticMember(AccessExpressionNode node, TypeSymbol type)
	{
		if (type is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (type.GetStaticField(node.Member.Text) is { } field)
			return CanAccess(type, field.Visibility)
				? ResolveSymbolValue(node, field)
				: Error(node, DiagnosticReporter.ReportHidden(node.Member.SourceLocation, field.Name, field.Visibility,
					true), CurrentTargetType);
		
		if (GetPropertyMember(type, node.Member.Text) is { } property)
			return ResolvePropertyUse(node, node.Member.SourceLocation, property, type, null);
		
		var statics = FindStatics(type, node.Member.Text);
		if (statics.Length == 0)
			return Error(node, DescribeMissingStatic(type, node.Member.Text), CurrentTargetType,
				node.Member.SourceLocation);
		
		var accessible = FindAccessible(type, statics);
		return accessible.Length > 0
			? ResolveFunctionValue(node, accessible, type)
			: Error(node, ReportHiddenMember(node.Member.SourceLocation, node.Member.Text,
				statics.Select(static method => method.Function)), CurrentTargetType);
	}
	
	private bool DeclaresNonCaseMember(TypeSymbol type, string name) => FindFunctions(type, name).Length > 0 ||
	                                                                    type.GetStaticField(name) is not null ||
	                                                                    GetPropertyMember(type, name) is not null;
	
	private IResolvedExpressionNode ResolvePropertyUse(IExpressionNode node, SourceLocation member,
		PropertySymbol property, TypeSymbol owner, IResolvedExpressionNode? receiver)
	{
		if (FindReceiverError(property.IsStatic, receiver) is { } receiverError)
			return Error(node, receiverError, CurrentTargetType, member);
		
		if (!CanAccess(owner, property))
			return Error(node, ReportHidden(member, property.Name, property.Visibility, property.Trait, property.Impl),
				CurrentTargetType);
		
		if (node == storeTarget)
			return new ResolvedPropertyExpressionNode(property, owner, receiver, GetPropertyType(property, owner),
				node);
		
		if (property.Getter is not FunctionAccessor { Function: var getter })
			return Error(node, "Cannot read write-only properties", CurrentTargetType, member);
		
		if (owner is DynType dyn && !IsDynMember(getter))
			return Error(node, $"Cannot use '{property.Name}' through '{dyn.Name}'", CurrentTargetType, member);
		
		return CanAccess(owner, getter)
			? CallAccessor(getter, owner, property.TraitArguments, receiver, [], node)
			: Error(node, DiagnosticReporter.ReportWriteOnly(member, property.Name, getter.Visibility),
				CurrentTargetType);
	}
	
	private static string? FindReceiverError(bool isStatic,
		IResolvedExpressionNode? receiver) => (isStatic, receiver) switch
	{
		(true, not null) => "Cannot use static properties through values",
		(false, null) => "Cannot use properties through types",
		_ => null
	};
	
	private TypeSymbol GetPropertyType(PropertySymbol property, TypeSymbol owner)
	{
		if (property.Getter is FunctionAccessor { Function: var getter })
			return GetFunctionInfo(getter, owner, property.TraitArguments).Signature.ReturnType;
		
		if (property.Setter is not FunctionAccessor { Function: var setter })
			return NativeSymbols.Invalid;
		
		var signature = GetFunctionInfo(setter, owner, property.TraitArguments).Signature;
		return signature.GetDeclaredType(signature.ParameterTypes.Length - 1);
	}
	
	private ResolvedFunctionCallExpressionNode CallAccessor(FunctionSymbol accessor, TypeSymbol owner,
		ImmutableArray<TypeSymbol> traitArguments, IResolvedExpressionNode? receiver,
		IEnumerable<IResolvedExpressionNode> arguments, IExpressionNode syntax)
	{
		var info = GetFunctionInfo(accessor, owner, traitArguments);
		TrackFunctionUse(info, syntax);
		IEnumerable<IResolvedExpressionNode> receivers = receiver is null ? [] : [CreateReceiver(receiver, info)];
		return new ResolvedFunctionCallExpressionNode(info, [..receivers, ..arguments], syntax);
	}
	
	private static bool TakesMutSelf(FunctionInfo accessor) => accessor.Signature.GetMode(0) == ParameterMode.Mut;
	
	private static SourceLocation GetMemberLocation(IExpressionNode syntax) =>
		syntax is AccessExpressionNode access ? access.Member.SourceLocation : syntax.SourceLocation;
	
	private MethodSymbol[] FindAccessible(TypeSymbol owner, IEnumerable<MethodSymbol> methods) =>
		[..methods.Where(method => CanAccess(owner, method.Function))];
	
	private bool CanAccess(TypeSymbol owner, Visibility visibility) =>
		CurrentResolutionContext.CanAccess(owner, visibility);
	
	private bool CanAccess(TypeSymbol owner, FunctionSymbol function) =>
		CanAccess(owner, function.Visibility, function.Trait, function.Impl);
	
	private bool CanAccess(TypeSymbol owner, PropertySymbol property) =>
		CanAccess(owner, property.Visibility, property.Trait, property.Impl);
	
	private bool CanAccess(TypeSymbol owner, Visibility visibility, TraitSymbol? trait, ImplSymbol? impl)
	{
		if (trait is null && impl is null)
			return CanAccess(owner, visibility);
		
		var context = CurrentResolutionContext;
		if (visibility == Visibility.Private)
			return trait is null ? context.ImplBlock == impl : context.Trait == trait;
		
		return impl is not { Node.Traits.IsEmpty: true } || CanAccess(owner, visibility);
	}
	
	private IEnumerable<MethodSymbol> GetMethods(TypeSymbol type, string name) =>
		type.GetFunctions(name).Concat(GetTraitMethods(type, name));
	
	private IEnumerable<MethodSymbol> GetTraitMethods(TypeSymbol type, string name)
	{
		if (type is DynType dyn)
			return GetDynTraits(dyn)
				.SelectMany(trait => GetInstanceMethods(trait, name))
				.Where(method => IsDynMember(method.Function));
		
		if (type is TypeParameterSymbol parameter)
			return _typePool.GetBounds(parameter).SelectMany(trait => GetInstanceMethods(trait, name));
		
		var conformances = GetVisibleConformances(type).ToList();
		return GetImpls(conformances, type)
			.SelectMany(impl => impl.Functions.Where(method => method.Name == name))
			.Concat(conformances.SelectMany(conformance =>
				GetInstanceMethods(_typePool.GetConformanceTrait(conformance, type), name)
					.Where(method => method.Function.Visibility == Visibility.Private ||
					                 IsDefaultWitness(conformance, method.Function))));
	}
	
	private static IEnumerable<MethodSymbol> GetInstanceMethods(TraitType trait, string name) => trait.Arguments.IsEmpty
		? trait.Trait.GetFunctions(name)
		: trait.Trait.GetFunctions(name).Select(method => new MethodSymbol(method.Name, method.Function)
		{
			TraitArguments = trait.Arguments
		});
	
	private static PropertySymbol? GetInstanceProperty(TraitType trait, string name) =>
		trait.Trait.GetProperty(name) is { } property && !trait.Arguments.IsEmpty
			? new PropertySymbol(property.Name)
			{
				BackingField = property.BackingField,
				Getter = property.Getter,
				Setter = property.Setter,
				Node = property.Node,
				Visibility = property.Visibility,
				ContainingType = property.ContainingType,
				Trait = property.Trait,
				Impl = property.Impl,
				TraitArguments = trait.Arguments
			}
			: trait.Trait.GetProperty(name);
	
	private static bool IsDefaultWitness(Conformance conformance, FunctionSymbol requirement) =>
		conformance.Witnesses.GetValueOrDefault(requirement) is FunctionWitness { Function: var witness } &&
		witness == requirement;
	
	private IEnumerable<Conformance> GetVisibleConformances(TypeSymbol type) => _typePool.FindConformances(type)
		.Where(conformance => CurrentResolutionContext.IsVisible(conformance.Trait));
	
	private IEnumerable<ImplSymbol> GetImpls(IEnumerable<Conformance> conformances, TypeSymbol type) => conformances
		.Select(static conformance => conformance.Impl)
		.OfType<ImplSymbol>()
		.Concat(_typePool.FindMemberBlocks(type))
		.Distinct();
	
	private PropertySymbol? GetPropertyMember(TypeSymbol type, string name)
	{
		if (type.GetProperty(name) is { } property)
			return property;
		
		if (type is DynType dyn)
			return GetDynTraits(dyn)
				.Select(trait => GetInstanceProperty(trait, name))
				.OfType<PropertySymbol>()
				.FirstOrDefault();
		
		if (type is TypeParameterSymbol parameter)
			return _typePool.GetBounds(parameter)
				.Select(trait => GetInstanceProperty(trait, name))
				.OfType<PropertySymbol>()
				.FirstOrDefault();
		
		var conformances = GetVisibleConformances(type).ToList();
		return GetImpls(conformances, type)
			       .Select(impl => impl.Properties.FirstOrDefault(p => p.Name == name))
			       .OfType<PropertySymbol>()
			       .FirstOrDefault() ??
		       conformances
			       .Select(conformance => conformance.Trait.GetProperty(name) is { } traitProperty &&
			                              (traitProperty.Visibility == Visibility.Private ||
			                               GetAccessors(traitProperty).Any(accessor =>
				                               IsDefaultWitness(conformance, accessor)))
				       ? GetInstanceProperty(_typePool.GetConformanceTrait(conformance, type), name)
				       : null)
			       .OfType<PropertySymbol>()
			       .FirstOrDefault();
	}
	
	private static IEnumerable<FunctionSymbol> GetAccessors(PropertySymbol property) =>
		new[] { property.Getter, property.Setter }.OfType<FunctionAccessor>().Select(static a => a.Function);
	
	private (TraitType? Trait, IExpressionNode Argument)? FindTraitView(IExpressionNode node)
	{
		if (node is not CallExpressionNode { Arguments: [var argument] } call)
			return null;
		
		var context = CurrentResolutionContext;
		var symbol = (call.Target is IndexerExpressionNode indexer ? indexer.Target : call.Target) switch
		{
			VarExpressionNode name => context.Resolve(name.Identifier.Text),
			AccessExpressionNode access when context.ResolveModule(access.Target) is { } module =>
				context.ResolveMember(module, access.Member.Text),
			_ => null
		};
		
		return symbol is TraitSymbol ? (context.ResolveTraitArgument(call.Target) as TraitType, argument) : null;
	}
	
	private IResolvedExpressionNode VisitTraitCall(CallExpressionNode node, AccessExpressionNode access,
		TraitType? trait, IExpressionNode argument, IndexerExpressionNode? indexer)
	{
		if (trait is null)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = access.Member.Text;
		var member = access.Member.SourceLocation;
		IResolvedExpressionNode? receiver = null;
		TypeSymbol self;
		if (CurrentResolutionContext.TryResolveExpressionAsType(argument) is { } type)
			self = RequireTypeArguments(type, argument);
		else
		{
			receiver = Decay(VisitNode(argument, null));
			self = receiver.Type;
		}
		
		if (self is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!_typePool.Conforms(self, trait))
			return Error(node, $"'{self.Name}' doesn't implement '{trait.Name}'", CurrentTargetType, argument);
		
		if (trait.Trait.GetProperty(name) is not null)
			return VisitIndirectCall(node, Index(indexer, VisitTraitMember(access, trait, argument)));
		
		var functions = GetInstanceMethods(trait, name).ToArray();
		var matching = functions.Where(method => method.HasReceiver == receiver is not null).ToArray();
		if (matching.Length == 0)
			return Error(node, DescribeMissingTraitMember(trait, name, functions, receiver is not null),
				CurrentTargetType, member);
		
		var accessible = matching.Where(method => CanAccess(self, method.Function)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(member, name,
				matching.Select(static method => method.Function)), CurrentTargetType);
		
		ICallable[] candidates =
		[
			..accessible
				.Select(method => GetFunctionInfo(method, self))
				.Select(info => receiver is null
					? (ICallable)new FunctionCallable(info)
					: new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (indexer is null)
			return ResolveCall(node, name, candidates, receiver);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, name, candidates, receiver, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IResolvedExpressionNode VisitTraitMember(AccessExpressionNode node, TraitType? trait,
		IExpressionNode argument)
	{
		if (trait is null)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = node.Member.Text;
		var member = node.Member.SourceLocation;
		IResolvedExpressionNode? receiver = null;
		TypeSymbol self;
		if (CurrentResolutionContext.TryResolveExpressionAsType(argument) is { } type)
			self = RequireTypeArguments(type, argument);
		else
		{
			receiver = Decay(VisitNode(argument, null));
			self = receiver.Type;
		}
		
		if (self is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!_typePool.Conforms(self, trait))
			return Error(node, $"'{self.Name}' doesn't implement '{trait.Name}'", CurrentTargetType, argument);
		
		if (GetInstanceProperty(trait, name) is { } property)
			return ResolvePropertyUse(node, member, property, self, receiver);
		
		var functions = GetInstanceMethods(trait, name).ToArray();
		if (receiver is not null)
			return Error(node, functions.Any(static method => method.HasReceiver)
				? "Cannot use methods as values"
				: DescribeMissingTraitMember(trait, name, functions, true), CurrentTargetType, member);
		
		var statics = functions.Where(static method => !method.HasReceiver).ToArray();
		if (statics.Length == 0)
			return Error(node, DescribeMissingTraitMember(trait, name, functions, false), CurrentTargetType, member);
		
		var accessible = FindAccessible(self, statics);
		return accessible.Length > 0
			? ResolveFunctionValue(node, accessible, self)
			: Error(node, ReportHiddenMember(member, name, statics.Select(static method => method.Function)),
				CurrentTargetType);
	}
	
	private static string DescribeMissingTraitMember(TraitType trait, string name, MethodSymbol[] functions,
		bool throughValue) => functions.Length == 0 ? $"Trait '{trait.Name}' has no member '{name}'"
		: throughValue ? "Cannot use static functions through values"
		: "Cannot use methods through types";
	
	private static Diagnostic ReportHiddenMember(SourceLocation location, string name,
		IEnumerable<FunctionSymbol> functions)
	{
		var widest = functions.MaxBy(static function => function.Visibility)!;
		return ReportHidden(location, name, widest.Visibility, widest.Trait, widest.Impl);
	}
	
	private static Diagnostic ReportHidden(SourceLocation location, string name, Visibility visibility,
		TraitSymbol? trait, ImplSymbol? impl) => (trait, impl) switch
	{
		(not null, _) when visibility == Visibility.Private => DiagnosticReporter.ReportHidden(location, name, "trait"),
		(_, not null) when visibility == Visibility.Private =>
			DiagnosticReporter.ReportHidden(location, name, "impl block"),
		_ => DiagnosticReporter.ReportHidden(location, name, visibility, true)
	};
	
	private Diagnostic ReportUndefinedSymbol(ISyntaxNode node, string name) =>
		CurrentResolutionContext.ReportHidden(name, node.SourceLocation) ??
		ReportHiddenStatic(name, node.SourceLocation) ??
		DiagnosticReporter.ReportUndefinedSymbol(node, name, GetVisibleSymbolNames());
	
	private Diagnostic? ReportHiddenStatic(string name, SourceLocation location)
	{
		for (var type = CurrentResolutionContext.ContainingType; type is not null; type = type.ContainingType)
		{
			var hidden = FindStatics(type, name)
				.Where(method => !CanAccess(type, method.Function))
				.Select(static method => method.Function)
				.ToList();
			
			if (hidden.Count > 0)
				return ReportHiddenMember(location, name, hidden);
		}
		
		return null;
	}
	
	private string DescribeMissingMember(TypeSymbol type, string name) =>
		type.GetStaticField(name) is not null ? "Cannot use static fields through values"
		: FindFunctions(type, name).Length > 0 ? "Cannot use static functions through values"
		: DescribeAbsentMember(type, name);
	
	private string DescribeMissingStatic(TypeSymbol type, string name) =>
		FindFunctions(type, name).Length > 0 ? "Cannot use methods through types"
		: FindField(type, name) is not null ? "Cannot use fields through types"
		: DescribeAbsentMember(type, name);
	
	private string DescribeAbsentMember(TypeSymbol type, string name) =>
		_typePool.FindBlockViolation(type, name) ?? $"Type '{type.Name}' has no member '{name}'";
	
	private MethodSymbol[] FindStatics(TypeSymbol type, string name) =>
		[..FindFunctions(type, name).Where(static function => !function.HasReceiver)];
	
	private MethodSymbol[] FindFunctions(TypeSymbol type, string name) =>
		[..GetMethods(type, name).Where(static method => !method.Function.IsConversion)];
	
	private FieldSymbol? FindField(TypeSymbol type, string name) =>
		_typePool.ResolveMember(GetMemberOwner(type, name), name) as FieldSymbol;
	
	private TypeSymbol GetMemberOwner(TypeSymbol type, string name)
	{
		if (type is PointerType { BaseType: var baseType } && baseType != NativeSymbols.Void)
			return baseType;
		
		if (type is DynType)
			return type;
		
		if (DeclaresAccessibleMember(type, name))
			return type;
		
		return GetDereferenceTarget(type) is { } target && (!DeclaresMember(type, name) || DeclaresMember(target, name))
			? target
			: type;
	}
	
	private bool DeclaresMember(TypeSymbol type, string name) =>
		_typePool.ResolveMember(type, name) is not null || DeclaresNonCaseMember(type, name);
	
	private bool DeclaresAccessibleMember(TypeSymbol type, string name) => _typePool.ResolveMember(type, name) switch
	{
		FieldSymbol field => CanAccess(type, field.Visibility),
		null when type.GetStaticField(name) is { } global => CanAccess(type, global.Visibility),
		null when GetPropertyMember(type, name) is { } property => CanAccess(type, property),
		null => FindFunctions(type, name).Any(function => CanAccess(type, function.Function)),
		_ => true
	};
	
	private TypeSymbol? GetDereferenceTarget(TypeSymbol type) => FindDereferences(type)
		.Select(dereference => GetFunctionInfo(dereference, type).Signature.ReturnType)
		.OfType<BorrowType>()
		.FirstOrDefault()?.Target;
	
	private IEnumerable<MethodSymbol> FindDereferences(TypeSymbol type) => FindPlaceOperators(type, "*");
	
	private IEnumerable<MethodSymbol> FindPlaceOperators(TypeSymbol type, string name) => GetMethods(type, name)
		.Where(method => method.HasReceiver && CanAccess(type, method.Function));
	
	private MethodSymbol? FindDereference(TypeSymbol type, ParameterMode mode) => FindPlaceOperator(type, "*", mode);
	
	private MethodSymbol? FindIndexer(TypeSymbol type, ParameterMode mode) => FindPlaceOperator(type, "[]", mode);
	
	private MethodSymbol? FindPlaceOperator(TypeSymbol type, string name, ParameterMode mode) =>
		FindPlaceOperators(type, name).FirstOrDefault(method => TakesReceiver(method, type, mode));
	
	private bool TakesReceiver(MethodSymbol method, TypeSymbol type, ParameterMode mode) =>
		GetFunctionInfo(method, type).Signature.GetMode(0) == mode;
	
	private ResolvedFunctionCallExpressionNode CallOperator(IResolvedExpressionNode receiver, MethodSymbol method,
		IExpressionNode syntax)
	{
		var info = GetFunctionInfo(method, receiver.Type);
		TrackFunctionUse(info, syntax);
		return new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(receiver, info)], syntax);
	}
	
	private IResolvedExpressionNode MakeWritable(IResolvedExpressionNode place)
	{
		switch (place)
		{
			case ResolvedAccessExpressionNode { Member: FieldSymbol } node:
			{
				var target = MakeWritable(node.Target);
				return target == node.Target
					? node
					: new ResolvedAccessExpressionNode(target, node.Member, node.Type, node.Syntax);
			}
			
			case ResolvedIndexerExpressionNode node:
			{
				var target = MakeWritable(node.Target);
				return target == node.Target
					? node
					: new ResolvedIndexerExpressionNode(node.Type, target, node.Index, node.Syntax);
			}
			
			case ResolvedUnaryOpExpressionNode
			{
				Operation.Op: TokenType.OpStar,
				Operand: ResolvedFunctionCallExpressionNode call
			} when FindReaderName(call) is { } name:
				return WriteThrough(place, call, name);
			
			case ResolvedFunctionCallExpressionNode { Type: not BorrowType } call when FindReaderName(call) is { } name:
				return WriteThrough(place, call, name);
			
			default:
				return place;
		}
	}
	
	private string? FindReaderName(ResolvedFunctionCallExpressionNode call) =>
		call is
		{
			Arguments: [var receiver, ..],
			Function.Symbol: { Syntax: FunctionNode { Identifier.Text: "*" or "[]" } syntax } reader
		} && FindPlaceOperator(receiver.Type, syntax.Identifier.Text, ParameterMode.ReadOnly)?.Function == reader
			? syntax.Identifier.Text
			: null;
	
	private IResolvedExpressionNode WriteThrough(IResolvedExpressionNode place, ResolvedFunctionCallExpressionNode call,
		string name)
	{
		var receiver = call.Arguments[0];
		if (FindPlaceOperator(receiver.Type, name, ParameterMode.Mut) is { } writer)
		{
			var info = InstantiateLike(GetFunctionInfo(writer, receiver.Type), call.Function);
			TrackFunctionUse(info, call.Syntax);
			return Decay(new ResolvedFunctionCallExpressionNode(info,
				[CreateReceiver(receiver, info), ..call.Arguments.Skip(1)], call.Syntax));
		}
		
		var hidden = GetMethods(receiver.Type, name)
			.Where(method => method.HasReceiver && TakesReceiver(method, receiver.Type, ParameterMode.Mut))
			.ToList();
		
		if (hidden.Count > 0)
			Diagnostics.Add(DiagnosticReporter.ReportReadOnly(call.Syntax.SourceLocation, name,
				hidden.Max(static method => method.Function.Visibility)));
		
		return place;
	}
	
	private IResolvedExpressionNode VisitIndirectCall(CallExpressionNode node, IResolvedExpressionNode target)
	{
		if (target.Type is UntypedType)
			target = MaterializeAsDefault(target);
		
		var callee = Decay(target);
		target = ResolveCallTarget(callee);
		ICallable[] candidates = target.Type is FunctionType type ? [new FunctionTypeCallable(type)] : [];
		var args = ResolveArguments(node.Arguments,
			[..candidates.Select(static candidate => CreateShape(candidate, []))], out _);
		
		if (IsInvalid(target) || AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (target.Type is not FunctionType functionType)
			return Error(node, $"Cannot call a value of type '{callee.Type.Name}'", CurrentTargetType, node.Target);
		
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		
		if (!resolutionSet.HasResult && ReportArgumentModes(candidates, args, null))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return Error(node, $"'{GetDisplayName(callee)}' doesn't accept these arguments", CurrentTargetType,
				node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolutionSet[0]);
		return new ResolvedIndirectCallExpressionNode(target, resolvedArgs, functionType, node);
	}
	
	private IResolvedExpressionNode ResolveCallTarget(IResolvedExpressionNode callee, bool dereferences = true)
	{
		if (callee.Type is FunctionType)
			return callee;
		
		if (_typePool.GetCallableSignatures(callee.Type) is [var signature])
			return new ResolvedConversionExpressionNode(callee,
				_conversionTable.FindImplicit(callee.Type, _typePool.GetRefFunctionType(signature))!, callee.Syntax);
		
		return dereferences && FindDereference(callee.Type, ParameterMode.ReadOnly) is not null
			? ResolveCallTarget(ResolveDereference(TokenType.OpStar, callee, callee.Syntax), false)
			: callee;
	}
	
	public IResolvedExpressionNode Visit(IndexerExpressionNode node) =>
		ResolveExplicitFunctionValue(node) ?? ResolveIndexing(node, VisitNode(node.Target));
	
	private IResolvedExpressionNode? ResolveExplicitFunctionValue(IndexerExpressionNode node)
	{
		var context = CurrentResolutionContext;
		MethodSymbol[] methods;
		TypeSymbol? owner = null;
		switch (node.Target)
		{
			case VarExpressionNode variable:
				methods = AsMethods(GetFunctions(context.Resolve(variable.Identifier.Text)));
				break;
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				methods = AsMethods(GetFunctions(context.ResolveMember(module, access.Member.Text)));
				break;
			
			case AccessExpressionNode access when context.TryResolveExpressionAsType(access.Target) is { } type:
				owner = RequireTypeArguments(type, access.Target);
				if (owner is InvalidType)
					return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
				
				methods = FindAccessible(owner, FindStatics(owner, access.Member.Text));
				break;
			
			default:
				return null;
		}
		
		if (methods.Length == 0)
			return null;
		
		FunctionInfo[] declared =
		[
			..methods.Select(method =>
				owner is null ? GetFunctionInfo(method.Function) : GetFunctionInfo(method, owner))
		];
		
		if (ResolveTypeArguments(node, declared) is not { } typeArguments)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = GetName(node.Target);
		var infos = new List<FunctionInfo>();
		foreach (var info in declared)
		{
			var open = GetOpenTypeParameters(info);
			if (open.Length != typeArguments.Types.Length)
				continue;
			
			if (ReportConstraintViolation(open, typeArguments.Types, i => typeArguments.Locations[i], info) is
			    { } violation)
				return Error(node, violation, CurrentTargetType);
			
			infos.Add(InstantiateDeclared(info, typeArguments.Types));
		}
		
		if (infos.Count > 0)
			return new ResolvedFunctionGroupExpressionNode(new FunctionGroupType(name, infos,
				infos is [var only] ? GetNaturalType(only).Name : name), node);
		
		var location = node.SourceLocation;
		return Error(node, methods is [var single]
			? ResolutionContext.ReportGenericArgumentCount(location, name,
				single.Function.DeclaredTypeParameters.Length)
			: new(DiagnosticSeverity.Error, location,
				$"No overload of '{name}' takes {typeArguments.Types.Length} generic arguments"), CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveIndexing(IndexerExpressionNode node, IResolvedExpressionNode target)
	{
		target = Decay(target);
		if (!IsInvalid(target) && target.Type is UntypedType)
			target = MaterializeAsDefault(target);
		
		if (IsInvalid(target))
			return RejectIndexing(node, new ResolvedInvalidExpressionNode(node, CurrentTargetType));
		
		return target.Type switch
		{
			ArrayType { ElementType: var elementType } => ResolveBuiltInIndexing(node, target, elementType),
			StringType => ResolveBuiltInIndexing(node, target, NativeSymbols.UInt8),
			PointerType { BaseType: var baseType } pointer when baseType != NativeSymbols.Void =>
				ResolvePointerIndexing(node, target, pointer),
			var type when GetMethods(type, "[]").Where(static method => method.HasReceiver).ToList() is
				{ Count: > 0 } declared => ResolveDeclaredIndexing(node, target, declared),
			_ => RejectIndexing(node, Error(node, $"Cannot index into type '{target.Type.Name}'", CurrentTargetType,
				target.Syntax))
		};
	}
	
	private ResolvedInvalidExpressionNode RejectIndexing(IndexerExpressionNode node,
		ResolvedInvalidExpressionNode error)
	{
		foreach (var argument in node.Arguments)
			VisitNode(argument);
		
		return error;
	}
	
	private IResolvedExpressionNode ResolveBuiltInIndexing(IndexerExpressionNode node, IResolvedExpressionNode target,
		TypeSymbol elementType)
	{
		if (node.Arguments.Length != 1)
			return RejectIndexing(node, Error(node, DescribeIndexCount(target.Type, 1), elementType));
		
		var index = VisitNode(node.Arguments[0], NativeSymbols.UIntSize);
		return FindElement(target) is { } element
			? Substitute(element)
			: new ResolvedIndexerExpressionNode(elementType, target, index, node);
	}
	
	private IResolvedExpressionNode ResolvePointerIndexing(IndexerExpressionNode node, IResolvedExpressionNode target,
		PointerType pointer)
	{
		if (node.Arguments.Length != 1)
			return RejectIndexing(node, Error(node, DescribeIndexCount(pointer, 1), pointer.BaseType));
		
		var index = VisitNode(node.Arguments[0], null);
		var offsetType = index.Type is IntegerType { IsSigned: false } ? NativeSymbols.UIntSize : NativeSymbols.IntSize;
		index = CoerceToType(index, offsetType);
		if (IsInvalid(index))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var offset = new PointerOffsetImpl(TokenType.OpPlus, pointer, offsetType);
		var address = new ResolvedBinaryOpExpressionNode(target, index, offset, node);
		return new ResolvedUnaryOpExpressionNode(address, new NativeImpl(TokenType.OpStar, pointer.BaseType), node);
	}
	
	private IResolvedExpressionNode ResolveDeclaredIndexing(IndexerExpressionNode node, IResolvedExpressionNode target,
		List<MethodSymbol> declared)
	{
		var type = target.Type;
		if ((FindIndexer(type, ParameterMode.ReadOnly) ?? FindIndexer(type, ParameterMode.Mut)) is not { } indexer)
			return RejectIndexing(node, Error(node, ReportHiddenMember(node.SourceLocation, "[]",
				declared.Select(static method => method.Function)), CurrentTargetType));
		
		var info = GetFunctionInfo(indexer, type);
		var parameterTypes = info.Signature.ParameterTypes;
		if (node.Arguments.Length != parameterTypes.Length - 1)
			return RejectIndexing(node, Error(node, DescribeIndexCount(type, parameterTypes.Length - 1),
				CurrentTargetType));
		
		if (!GetOpenTypeParameters(info).IsEmpty)
		{
			var checkpoint = OpenCheckpoint();
			var trial = node.Arguments.Select(argument => VisitNode(argument, null)).ToList();
			if (AnyInvalid([..trial]))
			{
				Commit(checkpoint);
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			}
			
			Rollback(checkpoint);
			var failures = new List<Diagnostic>();
			if (Specialize(new ReceiverCallable(info, info.Signature.ReturnType), "[]", trial, null,
				    node.SourceLocation, failures) is not ReceiverCallable specialized)
			{
				Diagnostics.AddRange(failures);
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			}
			
			info = specialized.Info;
			parameterTypes = info.Signature.ParameterTypes;
		}
		
		var indices = node.Arguments.Select((argument, i) => VisitNode(argument, parameterTypes[i + 1])).ToList();
		if (AnyInvalid([..indices]))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		TrackFunctionUse(info, node);
		return Decay(new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(target, info), ..indices], node));
	}
	
	private static string DescribeIndexCount(TypeSymbol type, int count) => count == 1
		? $"Indexing '{type.Name}' takes one argument"
		: $"Indexing '{type.Name}' takes {count} arguments";
	
	public IResolvedExpressionNode Visit(AccessExpressionNode node)
	{
		if (FindTraitView(node.Target) is var (trait, view))
			return VisitTraitMember(node, trait, view);
		
		if (ResolveEnumType(node.Target) is { } enumType)
			return DeclaresNonCaseMember(enumType, node.Member.Text)
				? VisitStaticMember(node, RequireTypeArguments(enumType, node.Target))
				: VisitEnumCase(node, enumType, null);
		
		if (CurrentResolutionContext.ResolveModule(node.Target) is { } module)
			return VisitModuleMember(node, module);
		
		if (CurrentResolutionContext.TryResolveExpressionAsType(node.Target) is { } type)
			return VisitStaticMember(node, RequireTypeArguments(type, node.Target));
		
		return ResolveAccess(node, VisitNode(node.Target));
	}
	
	private IResolvedExpressionNode VisitModuleMember(AccessExpressionNode node, ModulePathSymbol module)
	{
		var context = CurrentResolutionContext;
		if (context.ResolveMember(module, node.Member.Text) is { } member)
			return ResolveSymbolValue(node, member);
		
		var diagnostic = context.ReportUndefinedMember(node.Member.SourceLocation, module, node.Member.Text);
		return Error(node, diagnostic, CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveAccess(AccessExpressionNode node, IResolvedExpressionNode target)
	{
		if (IsInvalid(target))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (target is ResolvedLiteralExpressionNode { Type: UntypedType } or ResolvedInterpolatedStringExpressionNode)
			target = MaterializeAsDefault(target);
		
		target = Decay(target);
		if (GetMemberOwner(target.Type, node.Member.Text) != target.Type)
			target = ResolveDereference(TokenType.OpStar, target, node.Target);
		
		var memberName = node.Member.Text;
		var resolutionContext = CurrentResolutionContext;
		if (GetPropertyMember(target.Type, memberName) is { } property)
			return ResolvePropertyUse(node, node.Member.SourceLocation, property, target.Type, target);
		
		if (resolutionContext.TypePool.ResolveMember(target.Type, memberName) is not { } member)
			return Error(node, FindFunctions(target.Type, memberName) switch
			{
				[] when target.Type is FStrType && FindFStrPart(memberName) is not null =>
					"Cannot use methods as values",
				[] => DescribeMissingMember(target.Type, memberName),
				var functions when functions.Any(static function => function.HasReceiver) =>
					"Cannot use methods as values",
				_ => "Cannot use static functions through values"
			}, CurrentTargetType, node.Member.SourceLocation);
		
		if (member is FieldSymbol field && !CanAccess(target.Type, field.Visibility))
			return Error(node, DiagnosticReporter.ReportHidden(node.Member.SourceLocation, memberName, field.Visibility,
				true), CurrentTargetType);
		
		var memberType = GetMemberType(member);
		return new ResolvedAccessExpressionNode(target, member, memberType, node);
	}
	
	public IResolvedExpressionNode Visit(ArrayExpressionNode node)
	{
		var values = new List<IResolvedExpressionNode>(node.Values.Length);
		
		var elementType = (ExpectedType as ArrayType)?.ElementType;
		
		foreach (var expression in node.Values)
		{
			var value = VisitNode(expression, elementType);
			values.Add(value);
		}
		
		if (values.Count == 0)
		{
			elementType ??= NativeSymbols.Invalid;
		}
		else if (elementType is not null)
		{
			for (var i = 0; i < values.Count; i++)
				values[i] = CoerceToType(values[i], elementType);
		}
		else
		{
			// TODO Diagnostic: incompatible element types
			elementType = UnifyTypes(values) ?? NativeSymbols.Invalid;
			for (var i = 0; i < values.Count; i++)
				values[i] = CoerceToType(values[i], elementType);
		}
		
		if (elementType is DynType dyn)
			return Error(node, $"Cannot use '{dyn.Name}' by value", null);
		
		var type = _typePool.GetArrayType(elementType, node.Values.Length);
		return new ResolvedArrayExpressionNode(type, values, node);
	}
	
	public IResolvedExpressionNode Visit(InterpolatedStringExpressionNode node) =>
		new ResolvedInterpolatedStringExpressionNode(node.Values.Select(value => VisitNode(value, null)), node);
	
	private IResolvedExpressionNode FoldInterpolation(ResolvedInterpolatedStringExpressionNode node)
	{
		if (_foldedStrings.TryGetValue(node, out var folded))
			return folded;
		
		var syntax = node.Interpolation;
		var text = new StringBuilder(syntax.Segments[0]);
		var isValid = true;
		for (var i = 0; i < node.Values.Length; i++)
		{
			if (FoldString(node.Values[i]) is { } value)
				text.Append(value);
			else
				isValid = false;
			
			if (syntax.Specs[i].Length > 0)
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, syntax.SpecLocations[i],
					"Cannot use format specs in constant strings"));
				
				isValid = false;
			}
			
			text.Append(syntax.Segments[i + 1]);
		}
		
		folded = isValid
			? new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, text.ToString(), syntax)
			: new ResolvedInvalidExpressionNode(syntax);
		
		_foldedStrings[node] = folded;
		return folded;
	}
	
	private IResolvedExpressionNode CreateTemplate(ResolvedInterpolatedStringExpressionNode node, FStrType type)
	{
		if (_templates.TryGetValue((node, type), out var existing))
			return existing;
		
		var syntax = node.Interpolation;
		var target = _typePool.GetBorrowType(type.Value, false);
		var values = new List<IResolvedExpressionNode>(node.Values.Length);
		foreach (var hole in node.Values)
		{
			var value = hole.Type is UntypedType ? MaterializeAsDefault(hole) : hole;
			if (IsInvalid(value))
				continue;
			
			if (value.Type == target)
				values.Add(value);
			else if (ConvertToDyn(value, target) is { } converted)
				values.Add(converted);
			else
				Diagnostics.Add(new(DiagnosticSeverity.Error, hole.Syntax.SourceLocation,
					$"'{Decay(value).Type.Name}' doesn't implement '{type.Value.TraitName}'"));
		}
		
		var texts = new List<string> { syntax.Segments[0] };
		for (var i = 0; i < syntax.Specs.Length; i++)
		{
			texts.Add(syntax.Specs[i]);
			texts.Add(syntax.Segments[i + 1]);
		}
		
		IResolvedExpressionNode template = values.Count == node.Values.Length
			? new ResolvedFStrExpressionNode(type, texts, values, null, syntax)
			: new ResolvedInvalidExpressionNode(syntax, type);
		
		_templates[(node, type)] = template;
		return template;
	}
	
	private ResolvedFStrExpressionNode CreateTextTemplate(IResolvedExpressionNode text, FStrType type) =>
		_evaluator.Evaluate(text) is StringConstant constant
			? new ResolvedFStrExpressionNode(type, [constant.Text], [], null, text.Syntax)
			: new ResolvedFStrExpressionNode(type, [], [], text, text.Syntax);
	
	private string? FoldString(IResolvedExpressionNode value)
	{
		if (value is ResolvedInterpolatedStringExpressionNode interpolation)
			value = FoldInterpolation(interpolation);
		
		if (IsInvalid(value))
			return null;
		
		if (!IsString(value.Type))
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
				$"'{value.Type.Name}' is not a string"));
			
			return null;
		}
		
		switch (_evaluator.Evaluate(value))
		{
			case StringConstant constant:
				return constant.Text;
			
			case InvalidConstant:
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
					$"'{value.Syntax.SourceLocation.GetText()}' isn't a constant"));
				
				return null;
		}
	}
	
	private static bool IsString(TypeSymbol type) => type is StringType or UntypedStringType or InterpolatedStringType;
	
	private IResolvedExpressionNode ConcatenateStrings(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		IResolvedExpressionNode right)
	{
		if (left.Type is StringType && right.Type is StringType && left.Type != right.Type)
			return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, node.Op, right),
				CurrentTargetType);
		
		var leftText = FoldString(left);
		var rightText = FoldString(right);
		if (leftText is null || rightText is null)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var text = new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, leftText + rightText, node);
		_survey?.Track(text, _survey.GetTracked(left).Concat(_survey.GetTracked(right)));
		var type = left.Type as StringType ?? right.Type as StringType;
		return type is null ? text : MaterializeExpression(text, type);
	}
	
	public IResolvedExpressionNode Visit(LiteralExpressionNode node)
	{
		if (node.Token.Type == TokenType.InvalidCharLiteral)
			return Error(node, "Invalid character literal", CurrentTargetType);
		
		if (node.Token.Suffix is { } suffix)
			return ResolveSuffixedLiteral(node, suffix);
		
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		var tokenType = node.Token.Type;
		(type, value) = tokenType switch
		{
			TokenType.IntegerLiteral => ParseInteger(valueSpan),
			TokenType.FloatLiteral => ParseFloat(valueSpan),
			TokenType.KeywordNull => ParseNull(),
			TokenType.KeywordTrue => (NativeSymbols.Bool, true),
			TokenType.KeywordFalse => (NativeSymbols.Bool, false),
			TokenType.StringLiteral => ParseString(valueSpan),
			TokenType.CharLiteral => ParseChar(valueSpan),
			_ => (type, value)
		};
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value, node);
	}
	
	public IResolvedExpressionNode Visit(UndefExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var type = node.Type is null
			? CurrentTargetType ?? NativeSymbols.Invalid
			: resolutionContext.ResolveType(node.Type);
		
		return new ResolvedUndefExpressionNode(type, node);
	}
	
	public IResolvedExpressionNode Visit(SizeOfExpressionNode node) => ResolveLayout(node, node.Target, false);
	
	public IResolvedExpressionNode Visit(AlignOfExpressionNode node) => ResolveLayout(node, node.Target, true);
	
	private IResolvedExpressionNode ResolveLayout(IExpressionNode node, IExpressionNode targetNode, bool isAlignment)
	{
		TypeSymbol target;
		if (CurrentResolutionContext.TryResolveExpressionAsType(targetNode) is { } type)
			target = RequireTypeArguments(type, targetNode);
		else
		{
			var value = Decay(MaterializeAsDefault(VisitNode(targetNode)));
			if (value.Type is DynType)
			{
				var borrow = _typePool.GetBorrowType(value.Type, false);
				return new ResolvedConversionExpressionNode(new ResolvedBorrowExpressionNode(value, borrow, true,
					value.Syntax), new DynLayoutConversion(borrow, isAlignment), node);
			}
			
			target = value.Type;
		}
		
		if (TypePool.FindValueDyn(target) is { } dyn)
			return Error(node, $"Cannot use '{dyn.Name}' by value", CurrentTargetType);
		
		if (TypePool.ContainsTypeParameters(target))
			return isAlignment
				? new ResolvedAlignOfExpressionNode(target, node)
				: new ResolvedSizeOfExpressionNode(target, node);
		
		if (IsInvalid(target) || _typePool.SizeTable.TryGetSize(target) is not { } size)
			return new ResolvedInvalidExpressionNode(node);
		
		var bits = isAlignment ? size.CountAlignmentBits(_pointerBitSize) : size.CountBits(_pointerBitSize);
		return new ResolvedLiteralExpressionNode(NativeSymbols.UntypedInteger, new BigInteger((bits + 7) / 8), node);
	}
	
	public IResolvedExpressionNode Visit(TypeExpressionNode node) =>
		Error(node, $"'{node.SourceLocation.GetText()}' is not a value", CurrentTargetType);
	
	public IResolvedExpressionNode Visit(NameOfExpressionNode node)
	{
		if (!IsDefinedName(node.Name))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = node.Name switch
		{
			AccessExpressionNode access => access.Member.Text,
			VarExpressionNode variable => variable.Identifier.Text,
			_ => throw new InvalidOperationException()
		};
		
		return new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, name, node);
	}
	
	private bool IsDefinedName(IExpressionNode name)
	{
		var context = CurrentResolutionContext;
		switch (name)
		{
			case VarExpressionNode variable when context.Resolve(variable.Identifier.Text) is null:
				Diagnostics.Add(ReportUndefinedSymbol(variable, variable.Identifier.Text));
				return false;
			
			case VarExpressionNode:
				return true;
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				if (context.ResolveMember(module, access.Member.Text) is not null)
					return true;
				
				Diagnostics.Add(context.ReportUndefinedMember(access.Member.SourceLocation, module,
					access.Member.Text));
				
				return false;
			
			case AccessExpressionNode access when context.TryResolveExpressionAsType(access.Target) is { } type:
				return type is EnumSymbol enumType && enumType.GetStaticField(access.Member.Text) is null &&
				       GetPropertyMember(enumType, access.Member.Text) is null
					? FindCase(enumType, access.Member) is not null
					: HasMember(type, access.Member);
			
			case AccessExpressionNode access:
				var target = Decay(VisitNode(access.Target, null));
				return !IsInvalid(target) && HasMember(GetMemberOwner(target.Type, access.Member.Text), access.Member);
			
			default:
				return false;
		}
	}
	
	private bool HasMember(TypeSymbol type, Token member)
	{
		if (type.GetStaticField(member.Text) is { } global)
			return IsAccessibleMember(type, member, global.Visibility);
		
		if (GetPropertyMember(type, member.Text) is { } property)
			return IsAccessibleMember(type, member, property.Visibility);
		
		switch (_typePool.ResolveMember(type, member.Text))
		{
			case FieldSymbol field when !CanAccess(type, field.Visibility):
				Diagnostics.Add(DiagnosticReporter.ReportHidden(member.SourceLocation, member.Text, field.Visibility,
					true));
				
				return false;
			
			case not null:
				return true;
		}
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, member.SourceLocation, DescribeAbsentMember(type, member.Text)));
		return false;
	}
	
	private bool IsAccessibleMember(TypeSymbol type, Token member, Visibility visibility)
	{
		if (CanAccess(type, visibility))
			return true;
		
		Diagnostics.Add(DiagnosticReporter.ReportHidden(member.SourceLocation, member.Text, visibility, true));
		return false;
	}
	
	public IResolvedExpressionNode Visit(LambdaExpressionNode node) => ResolveLambda(node, CurrentTargetType);
	
	private IResolvedExpressionNode ResolveLambda(LambdaExpressionNode node, TypeSymbol? expected,
		bool resolveBody = true, bool isArgument = false)
	{
		var outer = CurrentResolutionContext;
		var target = (expected is BorrowType { IsMutable: false } borrow ? borrow.Target : expected) as FunctionType;
		if (target is not null && target.ParameterTypes.Length != node.Parameters.Length)
			return Error(node, $"'{(isArgument ? target.PlainName : target.Name)}' takes " +
			                   DescribeParameterCount(target.ParameterTypes.Length), expected);
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Parameters.Select(static p => p.Identifier),
			static name => $"Parameter '{name}' is declared more than once"));
		
		var modes = new ParameterMode[node.Parameters.Length];
		var types = new TypeSymbol[node.Parameters.Length];
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var parameter = node.Parameters[i];
			modes[i] = parameter.Mode is null && target is not null
				? target.ParameterModes[i]
				: SymbolCollector.GetMode(parameter.Mode);
			
			if (parameter.Type is BorrowTypeNode borrowType)
				Diagnostics.Add(DiagnosticReporter.ReportBorrowParameter(borrowType, parameter.Identifier.Text,
					parameter.Mode is not null, false));
			else if (parameter.Type is FunctionTypeNode { IsRef: true } refType)
				Diagnostics.Add(DiagnosticReporter.ReportRefFunctionParameter(refType, parameter.Identifier.Text,
					parameter.Mode is not null, false));
			
			if (parameter.Type is { } typeNode)
				types[i] = _typePool.GetPassedType(outer.ResolveType(typeNode), modes[i]);
			else if (target is not null && IsKnown(target.ParameterTypes[i]))
				types[i] = GetExpectedParameterType(target, i, modes[i]);
			else
			{
				Diagnostics.Add(new(DiagnosticSeverity.Error, parameter.Identifier.SourceLocation,
					$"Cannot infer the type of '{parameter.Identifier.Text}'"));
				
				types[i] = NativeSymbols.Invalid;
			}
		}
		
		var agrees = target is not null && types.Select((type, i) => modes[i] == target.ParameterModes[i] &&
		                                                             (type == target.ParameterTypes[i] ||
		                                                              !IsKnown(target.ParameterTypes[i])))
			.All(static agrees => agrees);
		
		var returnType = node.ReturnType is { } returnNode
			? outer.ResolveType(returnNode)
			: !agrees
				? NativeSymbols.Void
				: IsKnown(target!.ReturnType)
					? target.ReturnType
					: null;
		
		if (returnType is null)
		{
			if (node.ExpressionBody is not { } probe)
				return Error(node, "Cannot infer the return type", expected);
			
			var checkpoint = OpenCheckpoint();
			EnterLambda(outer, CreateLambdaInfo(node, outer, modes, types, NativeSymbols.Void), target);
			var value = Decay(MaterializeAsDefault(VisitNode(probe, null)));
			ExitLambda();
			Rollback(checkpoint);
			returnType = value.Type;
		}
		
		var info = CreateLambdaInfo(node, outer, modes, types, returnType);
		var isInvalid = returnType is InvalidType || types.Any(static type => type is InvalidType);
		if (!resolveBody)
			return isInvalid ? new ResolvedInvalidExpressionNode(node, expected) : CreateLambdaGroup(info, node);
		
		var frame = EnterLambda(outer, info, target);
		IResolvedNode body = node.ExpressionBody is { } expression
			? VisitNode(new ExpressionStatementNode(returnType == NativeSymbols.Void
				? expression
				: new ReturnExpressionNode(expression.SourceLocation, expression)))
			: VisitNode(node.BlockBody!);
		
		ExitLambda();
		var function = new ResolvedFunctionNode(info, body, info.Symbol.Syntax);
		_lambdas.Add(function);
		Journal(() => _lambdas.Remove(function));
		if (isInvalid)
			return new ResolvedInvalidExpressionNode(node, expected);
		
		if (frame.Captures.Count == 0)
		{
			if (node.Own is { } own && !frame.RejectsCaptures)
				Diagnostics.Add(new(DiagnosticSeverity.Hint, own.SourceLocation, "Redundant 'own'"));
			
			return CreateLambdaGroup(info, node);
		}
		
		List<(LocalVariableSymbol Binding, VarExpressionNode Use)> captures = node.Own is null
			? frame.Captures
			: [..frame.Captures.OrderBy(static capture => GetDeclarationStart(capture.Binding))];
		
		info.Symbol.Captures = [..captures.Select(static capture => capture.Binding)];
		info.Symbol.OwnsCaptures = node.Own is not null;
		var signature = _typePool.GetFunctionType(false, types, modes, returnType, node.Own is null);
		var type = node.Own is null
			? signature
			: (TypeSymbol)_typePool.GetClosureType(info,
				[..captures.Select(static capture => ((PointerType)capture.Binding.Type).BaseType)], signature,
				info.MangledName!);
		
		return new ResolvedClosureExpressionNode(info,
			[..captures.Select(capture => ResolveSymbolValue(capture.Use, capture.Binding.Captured!))], type, node);
	}
	
	private static int GetDeclarationStart(VariableSymbol variable) => variable switch
	{
		LocalVariableSymbol { Captured: { } captured } => GetDeclarationStart(captured),
		LocalVariableSymbol local => local.Identifier.SourceLocation.Range.Start,
		ParameterSymbol parameter => parameter.Definition.Range.Start,
		_ => 0
	};
	
	private ResolvedFunctionGroupExpressionNode CreateLambdaGroup(FunctionInfo info, LambdaExpressionNode node) =>
		new(new FunctionGroupType("fun", [info], GetNaturalType(info).Name), node);
	
	private TypeSymbol GetExpectedParameterType(FunctionType target, int index, ParameterMode mode)
	{
		var type = target.ParameterTypes[index];
		if (mode == target.ParameterModes[index])
			return type;
		
		return _typePool.GetPassedType(target.ParameterModes[index] == ParameterMode.Mut && type is PointerType pointer
			? pointer.BaseType
			: type, mode);
	}
	
	private static string DescribeParameterCount(int count) => count switch
	{
		0 => "no parameters",
		1 => "one parameter",
		_ => $"{count} parameters"
	};
	
	private bool IsKnown(TypeSymbol type) => TypePool.FindTypeParameters(type)
		.All(parameter => CurrentResolutionContext.Resolve(parameter.Name) == parameter);
	
	private FunctionInfo CreateLambdaInfo(LambdaExpressionNode node, ResolutionContext outer,
		IReadOnlyList<ParameterMode> modes, IReadOnlyList<TypeSymbol> types, TypeSymbol returnType)
	{
		var parameters = node.Parameters
			.Select((parameter, i) => new ParameterSymbol(parameter.Identifier) { Mode = modes[i] })
			.ToList();
		
		var symbol = new FunctionSymbol($"lambda{++_lambdaCount}", new LambdaDeclarationNode(node), Visibility.Private,
			outer.ContainingFunction, parameters, FunctionKind.Free)
		{
			TypeParameters = outer.ContainingFunction?.Symbol.TypeParameters ?? []
		};
		
		var scope = new Scope();
		foreach (var parameter in parameters)
			scope.Define(parameter);
		
		var signature = new FunctionSignature(types, returnType, false, modes);
		var info = new FunctionInfo(outer.Mangle(symbol, signature), symbol, signature, scope, null, outer.File);
		_signatures.AddLambda(info);
		Journal(() => _signatures.RemoveLambda(info));
		return info;
	}
	
	private LambdaFrame EnterLambda(ResolutionContext outer, FunctionInfo info, FunctionType? target)
	{
		var frame = new LambdaFrame(outer, target);
		_lambdaFrames.Add(frame);
		_resolutionContexts.Push(outer with
		{
			ContainingFunction = info,
			LocalScope = info.Scope,
			Capture = name => (Symbol?)frame.Bindings.GetValueOrDefault(name) ??
			                  (outer.ResolveNear(name) is LocalVariableSymbol or ParameterSymbol or CapturedSymbol
				                  ? new CapturedSymbol(name)
				                  : null)
		});
		
		return frame;
	}
	
	private void ExitLambda()
	{
		_resolutionContexts.Pop();
		_lambdaFrames.RemoveAt(_lambdaFrames.Count - 1);
	}
	
	private IResolvedExpressionNode ResolveCapture(VarExpressionNode node)
	{
		if (_survey is { } survey && FindCapturedLocal(_lambdaFrames.Count - 1, node.Identifier.Text) is { } local &&
		    survey.Find(local) is { } open)
			return node == storeTarget || node == _placeTarget
				? new ResolvedVarExpressionNode(local, local.Type, node)
				: Substitute(open);
		
		return Capture(_lambdaFrames.Count - 1, node) is { } binding
			? ResolveSymbolValue(node, binding)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private LocalVariableSymbol? FindCapturedLocal(int level, string name) =>
		_lambdaFrames[level].Outer.ResolveNear(name) switch
		{
			CapturedSymbol when level > 0 => FindCapturedLocal(level - 1, name),
			var outer => outer as LocalVariableSymbol
		};
	
	private LocalVariableSymbol? Capture(int level, VarExpressionNode node)
	{
		var frame = _lambdaFrames[level];
		var name = node.Identifier.Text;
		if (frame.Bindings.TryGetValue(name, out var existing))
			return existing;
		
		if (frame.Target is { IsRef: false } target)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"Cannot capture '{name}' in '{target.Name}'"));
			
			frame.RejectsCaptures = true;
			return null;
		}
		
		var outer = frame.Outer.ResolveNear(name);
		var captured = outer is CapturedSymbol ? Capture(level - 1, node) : outer as VariableSymbol;
		if (captured is null)
			return null;
		
		var binding = new LocalVariableSymbol(node.Identifier, _typePool.GetPointerType(GetValueType(captured)!), false)
		{
			IsBorrowBinding = true,
			IsMutBinding = IsWritable(captured),
			Captured = captured
		};
		
		frame.Bindings[name] = binding;
		frame.Captures.Add((binding, node));
		Journal(() =>
		{
			frame.Bindings.Remove(name);
			frame.Captures.RemoveAt(frame.Captures.Count - 1);
		});
		
		return binding;
	}
	
	private static bool IsWritable(VariableSymbol variable) => variable switch
	{
		LocalVariableSymbol { IsBorrowBinding: true } binding => binding.IsMutBinding,
		LocalVariableSymbol local => local.IsMutable,
		ParameterSymbol parameter => parameter.Mode != ParameterMode.ReadOnly,
		_ => false
	};
	
	public IResolvedExpressionNode Visit(VarExpressionNode node)
	{
		if (ResolveTargetCase(node.Identifier, node) is { } targetCase)
			return targetCase;
		
		var resolutionContext = CurrentResolutionContext;
		var varName = node.Identifier.Text;
		var symbol = resolutionContext.Resolve(varName);
		
		return symbol is null
			? Error(node, ReportUndefinedSymbol(node, varName), CurrentTargetType)
			: ResolveSymbolValue(node, symbol);
	}
	
	private IResolvedExpressionNode ResolveSymbolValue(IExpressionNode node, Symbol symbol)
	{
		switch (symbol)
		{
			case LocalVariableSymbol { IsBorrowBinding: true, Type: PointerType } binding:
				return ResolveDereference(TokenType.OpStar, new ResolvedVarExpressionNode(binding, binding.Type, node),
					node);
			
			case LocalVariableSymbol v when _survey?.Find(v) is { } open && node != storeTarget && node != _placeTarget:
				return Substitute(open);
			
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type, node);
			
			case GlobalSymbol g:
				if (g.ContainingType is not NamedTypeSymbol { TypeArguments.IsEmpty: false })
					TrackGenericReference(g);
				
				return new ResolvedGlobalExpressionNode(g, _signatures.GetGlobalType(g), node);
			
			case PropertySymbol property:
				return ResolvePropertyUse(node, node.SourceLocation, property, property.ContainingType!, null);
			
			case ParameterSymbol { Mode: ParameterMode.Mut } parameter:
				var pointer = new ResolvedVarExpressionNode(parameter, _signatures.GetVariableType(parameter), node);
				return pointer.Type is PointerType ? ResolveDereference(TokenType.OpStar, pointer, node) : pointer;
			
			case VariableSymbol v:
				return new ResolvedVarExpressionNode(v, _signatures.GetVariableType(v), node);
			
			case FunctionSymbol or AmbiguousSymbol:
				return ResolveFunctionValue(node, symbol);
			
			case ModulePathSymbol:
				return Error(node, $"'{GetName(node)}' is a module, not a value", CurrentTargetType);
			
			case TypeParameterSymbol { IsValue: true } parameter:
				return new ResolvedValueParameterExpressionNode(parameter, node);
			
			case TraitSymbol or TypeParameterSymbol { IsTrait: true }:
				return Error(node, "Cannot use traits as values", CurrentTargetType);
			
			case CapturedSymbol when node is VarExpressionNode variable:
				return ResolveCapture(variable);
			
			default:
				return Error(node, $"Symbol '{GetName(node)}' is not a variable", CurrentTargetType);
		}
	}
	
	private static FunctionSymbol[] GetFunctions(Symbol? symbol) => symbol switch
	{
		FunctionSymbol f => [f],
		AmbiguousSymbol a when a.Candidates.All(static c => c is FunctionSymbol) =>
			[..a.Candidates.Cast<FunctionSymbol>()],
		_ => []
	};
	
	private static MethodSymbol[] AsMethods(FunctionSymbol[] functions) =>
		[..functions.Select(static function => new MethodSymbol(function.Name, function))];
	
	private static string GetName(IExpressionNode node) => node switch
	{
		VarExpressionNode v => v.Identifier.Text,
		AccessExpressionNode a => $"{GetName(a.Target)}.{a.Member.Text}",
		_ => node.SourceLocation.GetText().ToString()
	};
	
	private IResolvedExpressionNode ResolveFunctionValue(IExpressionNode node, Symbol symbol) =>
		GetFunctions(symbol) is { Length: > 0 } functions
			? CreateFunctionGroup(node, [..functions.Select(GetFunctionInfo)])
			: Error(node, $"Reference to '{GetName(node)}' is ambiguous", CurrentTargetType);
	
	private ResolvedFunctionGroupExpressionNode ResolveFunctionValue(IExpressionNode node, MethodSymbol[] methods,
		TypeSymbol owner) => CreateFunctionGroup(node, [..methods.Select(method => GetFunctionInfo(method, owner))]);
	
	private ResolvedFunctionGroupExpressionNode CreateFunctionGroup(IExpressionNode node, FunctionInfo[] infos)
	{
		var name = GetName(node);
		var typeName = infos is [var single] ? GetNaturalType(single).Name : name;
		return new ResolvedFunctionGroupExpressionNode(new FunctionGroupType(name, infos, typeName), node);
	}
	
	private FunctionType GetNaturalType(FunctionInfo function)
	{
		var signature = function.Signature;
		return _typePool.GetFunctionType(function.Symbol.IsExternal, signature.ParameterTypes,
			signature.ParameterTypes.Select((_, i) => signature.GetMode(i)), signature.ReturnType);
	}
	
	private IResolvedExpressionNode MaterializeFunction(ResolvedFunctionGroupExpressionNode node, TypeSymbol target)
	{
		if (target is not FunctionType type || FindFunction(node.Group, type) is not { } function)
			return node;
		
		if (function.Symbol.Syntax is FunctionNode { When: not null })
			return Error(node.Syntax, $"'when' function '{node.Group.FunctionName}' can't be used as a value",
				NativeSymbols.Invalid);
		
		TrackFunctionUse(function, node.Syntax);
		return new ResolvedFunctionReferenceExpressionNode(function, type, node.Syntax);
	}
	
	private IResolvedExpressionNode MaterializeFunctionAsDefault(ResolvedFunctionGroupExpressionNode node)
	{
		var group = node.Group;
		if (group.Functions is not [var function])
			return Error(node.Syntax, $"Reference to '{group.FunctionName}' is ambiguous", NativeSymbols.Invalid);
		
		if (GetOpenTypeParameters(function) is { IsEmpty: false } open)
			return Error(node.Syntax, ReportInferenceFailure(new() { Missing = open }, group.FunctionName,
				node.Syntax.SourceLocation), NativeSymbols.Invalid);
		
		if (function.Signature.IsVariadic)
			return Error(node.Syntax, $"Variadic function '{group.FunctionName}' can't be used as a value",
				NativeSymbols.Invalid);
		
		if (function.Symbol.Syntax is FunctionNode { When: not null })
			return Error(node.Syntax, $"'when' function '{group.FunctionName}' can't be used as a value",
				NativeSymbols.Invalid);
		
		TrackFunctionUse(function, node.Syntax);
		return new ResolvedFunctionReferenceExpressionNode(function, GetNaturalType(function), node.Syntax);
	}
	
	private FunctionInfo? FindFunction(FunctionGroupType group, FunctionType type)
	{
		if (group.Find(type) is { } exact)
			return exact;
		
		foreach (var function in group.Functions)
		{
			var signature = function.Signature;
			var open = GetOpenTypeParameters(function);
			if (open.IsEmpty || signature.IsVariadic || signature.ParameterTypes.Length != type.ParameterTypes.Length)
				continue;
			
			var inputs = signature.ParameterTypes
				.Select((parameter, i) => new InferenceInput(parameter, type.ParameterTypes[i]) { IsExact = true })
				.Append(new(signature.ReturnType, type.ReturnType) { IsExact = true });
			
			var result = _inference.Infer(open, inputs, null, null);
			if (!result.Succeeded ||
			    ReportConstraintViolation(open, result.Arguments, _ => default, function) is not null)
				continue;
			
			var instantiated = InstantiateDeclared(function, result.Arguments);
			if (FunctionGroupType.Matches(instantiated, type, true))
				return instantiated;
		}
		
		return null;
	}
	
	private ResolvedInvalidExpressionNode ReportFunctionMismatch(ResolvedFunctionGroupExpressionNode node,
		TypeSymbol target)
	{
		var group = node.Group;
		var message = group.Functions switch
		{
			[{ Signature.IsVariadic: true }] => $"Variadic function '{group.FunctionName}' can't be used as a value",
			[_] => $"Cannot convert type '{group.Name}' to '{target.Name}'",
			_ => $"No overload of '{group.FunctionName}' matches '{target.Name}'"
		};
		
		return Error(node.Syntax, message, target);
	}
	
	public IResolvedStatementNode Visit(IfStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		ReportDeclarationBody(node.Then);
		var then = VisitInScope(node.Then, GetTrueBindings(condition));
		
		if (node.Else is { } elseNode)
			ReportDeclarationBody(elseNode);
		
		var @else = node.Else is null ? null : VisitInScope(node.Else, []);
		
		return new ResolvedIfStatementNode(condition, then, @else, node);
	}
	
	public IResolvedStatementNode Visit(VarStatementNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		TypeSymbol? type;
		if (node.Type is { } specifiedType)
			type = resolutionContext.ResolveType(specifiedType);
		else
			type = _settledTypes?.GetValueOrDefault(node);
		
		OpenLocal? open = null;
		IResolvedExpressionNode? initializer;
		if (node.ExpressionNode is not { } initializerNode)
			initializer = null;
		else if (type is not null)
			initializer = VisitNode(initializerNode, type);
		else
		{
			var value = VisitNode(initializerNode, null);
			if (_survey is not null && (IsOpenValue(value) || IsOpenSyntax(initializerNode)))
				open = DeclareOpen(new(node, node.Identifier, initializerNode, resolutionContext)
				{
					IsCaseName = IsCaseNameSyntax(initializerNode)
				}, [value]);
			
			initializer = MaterializeAsDefault(value);
		}
		
		if (type is null && initializer is not null && BorrowsWhenDeclared(initializer))
			initializer = new ResolvedBorrowExpressionNode(initializer,
				_typePool.GetBorrowType(initializer.Type, false), true, initializer.Syntax);
		else if (type is null && initializer is ResolvedBorrowExpressionNode { IsImplicit: false } borrow &&
		         borrow.Syntax is BorrowExpressionNode { IsMutable: false } syntax &&
		         BorrowsWhenDeclared(borrow.Place))
			Diagnostics.Add(new(DiagnosticSeverity.Hint, syntax.Keyword.SourceLocation, "Redundant 'imm'"));
		
		type ??= initializer?.Type ?? NativeSymbols.Invalid;
		if (TypePool.FindValueDyn(type) is { } valueDyn)
		{
			var location = node.Type?.SourceLocation ?? node.ExpressionNode!.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot use '{valueDyn.Name}' by value"));
			
			type = NativeSymbols.Invalid;
		}
		
		var symbol = new LocalVariableSymbol(node.Identifier, type, node.IsMutable)
		{
			ConstantValue = node.IsMutable || initializer is null || !_typePool.IsCopy(type)
				? null
				: _evaluator.Evaluate(initializer),
			IsDeferred = !node.IsMutable && initializer is null
		};
		
		resolutionContext.LocalScope!.Define(symbol);
		if (open is not null)
			_survey!.Bind(symbol, open);
		
		return new ResolvedVarStatementNode(symbol, initializer, node);
	}
	
	private bool BorrowsWhenDeclared(IResolvedExpressionNode value) =>
		value.Type is DynType || !_typePool.IsCopy(value.Type) && IsStored(value);
	
	public IResolvedStatementNode Visit(WhileStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		// Create a scope for the body and label (if applicable)
		var scope = CurrentScope?.CreateChild() ?? new();
		foreach (var binding in GetTrueBindings(condition))
			scope.Define(binding);
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		
		LabelSymbol? symbol;
		if (node.Label is { } label)
		{
			symbol = new(label);
			scope.Define(symbol);
		}
		else
			symbol = null;
		
		_resolutionContexts.Push(resolutionContext);
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedWhileStatementNode(condition, body, symbol, node);
	}
	
	public IResolvedStatementNode Visit(DoWhileStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		// Create a scope for the body and label (if applicable)
		var scope = CurrentScope?.CreateChild() ?? new();
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		
		LabelSymbol? symbol;
		if (node.Label is { } label)
		{
			symbol = new(label);
			scope.Define(symbol);
		}
		else
			symbol = null;
		
		_resolutionContexts.Push(resolutionContext);
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedDoWhileStatementNode(body, condition, symbol, node);
	}
	
	public IResolvedStatementNode Visit(ForStatementNode node)
	{
		var header = node.End is { } end ? ResolveRange(node, end) : ResolveIteration(node);
		var scope = CreateScope([header.Binding]);
		LabelSymbol? label = node.Label is { } labelToken ? new(labelToken) : null;
		if (label is not null)
			scope.Define(label);
		
		_resolutionContexts.Push(CurrentResolutionContext with { LocalScope = scope });
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		return header.Build(body, label);
	}
	
	private sealed record ForHeader
	(
		LocalVariableSymbol? Binding,
		Func<IResolvedStatementNode, LabelSymbol?, IResolvedStatementNode> Build
	);
	
	private ForHeader ResolveRange(ForStatementNode node, IExpressionNode endNode)
	{
		List<IResolvedExpressionNode> bounds = [Decay(VisitNode(node.Source)), Decay(VisitNode(endNode))];
		if (node.Mode is { } mode)
			Diagnostics.Add(new(DiagnosticSeverity.Error, mode.SourceLocation,
				"Cannot iterate over ranges with 'mut'"));
		
		var open = _settledTypes is null && _survey is not null && bounds.All(IsOpenValue)
			? DeclareOpen(new(node, node.Binding, node.Source, CurrentResolutionContext) { IsCounter = true }, bounds)
			: null;
		
		var type = _settledTypes?.GetValueOrDefault(node) ??
		           (AnyInvalid(bounds[0], bounds[1]) ? NativeSymbols.Invalid : UnifyTypes(bounds));
		
		if (type is not (InvalidType or IntegerType { Kind: not PrimitiveTypeKind.Char }))
		{
			var (source, range) = node.Source.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, new(source, range.Join(endNode.SourceLocation.Range)),
				type is null
					? $"Range bounds have incompatible types: '{bounds[0].Type.Name}', '{bounds[1].Type.Name}'"
					: "Range bounds must be integers"));
			
			type = NativeSymbols.Invalid;
		}
		
		var binding = CreateLoopBinding(node.Binding, type, false, false);
		if (open is not null && binding is not null)
			_survey!.Bind(binding, open);
		
		if (type is InvalidType)
			return InvalidHeader(node, binding);
		
		var counter = binding ?? new LocalVariableSymbol(HiddenName(node.Binding, "counter"), type, false);
		var start = CoerceToType(bounds[0], type);
		var end = CoerceToType(bounds[1], type);
		var isInclusive = node.RangeOperator?.Type == TokenType.OpDotDotEqual;
		return new(binding, (body, label) =>
			new ResolvedRangeForStatementNode(counter, start, end, isInclusive, null, null, body, label, node));
	}
	
	private ForHeader ResolveIteration(ForStatementNode node)
	{
		var source = VisitNode(node.Source);
		if (!IsInvalid(source) && source.Type is UntypedType)
			source = MaterializeAsDefault(source);
		
		source = Decay(source);
		if (IsInvalid(source))
			return InvalidHeader(node, CreateLoopBinding(node.Binding, NativeSymbols.Invalid, false, false));
		
		var isMut = node.Mode is not null;
		var type = source.Type;
		if (type is ArrayType array)
			return ResolveArrayIteration(node, source, array, isMut);
		
		if (FindLoopOperators(type, "in") is [_, ..] collection)
			return ResolveCollection(node, source, collection, isMut);
		
		if (FindLoopOperators(type, "for") is not [_, ..])
			return RejectIteration(node, $"Cannot iterate over '{type.Name}'", node.Source.SourceLocation);
		
		if (isMut)
		{
			var pointer = _typePool.GetPointerType(type);
			var cursor = new LocalVariableSymbol(HiddenName(node.Binding, "cursor"), pointer, false)
			{
				IsBorrowBinding = true,
				IsMutBinding = true
			};
			
			return ResolveCursor(node, cursor,
				new ResolvedMutArgumentExpressionNode(MakeWritable(source), pointer, node.Source), true);
		}
		
		return TypePool.FindValueDyn(type) is { } dyn
			? RejectIteration(node, $"Cannot use '{dyn.Name}' by value", node.Source.SourceLocation)
			: ResolveCursor(node, new LocalVariableSymbol(HiddenName(node.Binding, "cursor"), type, true), source,
				false);
	}
	
	private ForHeader ResolveArrayIteration(ForStatementNode node, IResolvedExpressionNode source, ArrayType array,
		bool isMut)
	{
		var syntax = node.Source;
		var pointer = _typePool.GetPointerType(array);
		var sourceSymbol = new LocalVariableSymbol(HiddenName(node.Binding, "source"), pointer, false)
		{
			IsBorrowBinding = true,
			IsMutBinding = isMut
		};
		
		IResolvedExpressionNode sourceValue = isMut
			? new ResolvedMutArgumentExpressionNode(MakeWritable(source), pointer, syntax)
			: source;
		
		var index = new LocalVariableSymbol(HiddenName(node.Binding, "index"), NativeSymbols.UIntSize, false);
		var start = MaterializeLiteral(syntax, NativeSymbols.UIntSize, BigInteger.Zero);
		var end = array.LengthParameter is { } length
			? ConvertLength(new ResolvedValueParameterExpressionNode(length, syntax))
			: MaterializeLiteral(syntax, NativeSymbols.UIntSize, array.Length);
		
		var elementType = array.ElementType;
		var binding = CreateLoopBinding(node.Binding, elementType, isMut || !_typePool.IsCopy(elementType), isMut);
		if (!isMut && binding is not null && FindElement(source) is { } open)
			_survey!.Bind(binding, open);
		
		var element = binding is null
			? null
			: new ResolvedLoopVariable(binding, new ResolvedIndexerExpressionNode(elementType,
				Dereference(sourceSymbol, syntax), new ResolvedVarExpressionNode(index, index.Type, syntax), syntax));
		
		return new(binding, (body, label) => new ResolvedRangeForStatementNode(index, start, end, false,
			new(sourceSymbol, sourceValue), element, body, label, node));
	}
	
	private IResolvedExpressionNode ConvertLength(IResolvedExpressionNode length) =>
		length.Type == NativeSymbols.UIntSize || IsInvalid(length)
			? length
			: new ResolvedConversionExpressionNode(length,
				_conversionTable.FindExplicit(length.Type, NativeSymbols.UIntSize)!, length.Syntax);
	
	private ForHeader ResolveCollection(ForStatementNode node, IResolvedExpressionNode source,
		List<MethodSymbol> operators, bool isMut)
	{
		var type = source.Type;
		var mode = isMut ? ParameterMode.Mut : ParameterMode.ReadOnly;
		var location = node.Source.SourceLocation;
		var sameMode = operators.Where(method => TakesReceiver(method, type, mode)).ToList();
		if (sameMode.FirstOrDefault(method => CanAccess(type, method.Function)) is not { } method)
			return sameMode.Count > 0
				? RejectIteration(node, ReportHiddenMember(location, "in", sameMode.Select(static m => m.Function)))
				: RejectIteration(node,
					$"Cannot iterate over '{type.Name}' {(isMut ? "with" : "without")} 'mut'", location);
		
		var call = CallOperator(source, method, node.Source);
		return ResolveCursor(node, new LocalVariableSymbol(HiddenName(node.Binding, "cursor"), call.Type, true), call,
			isMut);
	}
	
	private ForHeader ResolveCursor(ForStatementNode node, LocalVariableSymbol cursor,
		IResolvedExpressionNode cursorValue, bool isMut)
	{
		var syntax = node.Source;
		var type = cursor.Type is PointerType { BaseType: var target } && cursor.IsBorrowBinding ? target : cursor.Type;
		var steps = FindLoopOperators(type, "for");
		if (steps.FirstOrDefault(method => CanAccess(type, method.Function)) is not { } step)
			return steps.Count > 0
				? RejectIteration(node, ReportHiddenMember(syntax.SourceLocation, "for",
					steps.Select(static method => method.Function)))
				: RejectIteration(node, $"'{type.Name}' has no 'for' operator", syntax.SourceLocation);
		
		var stepCall = CallOperator(ReadCursor(), step, syntax);
		if (node.Binding.Text == "_")
			return new(null, (body, label) =>
				new ResolvedCursorForStatementNode(new(cursor, cursorValue), stepCall, null, body, label, node));
		
		var dereference = isMut
			? FindDereference(type, ParameterMode.Mut) ?? FindDereference(type, ParameterMode.ReadOnly)
			: FindDereference(type, ParameterMode.ReadOnly) ?? FindDereference(type, ParameterMode.Mut);
		
		if (dereference is null)
		{
			var location = node.Binding.SourceLocation;
			var hidden = FindLoopOperators(type, "*");
			return hidden.Count > 0
				? RejectIteration(node, ReportHiddenMember(location, "*", hidden.Select(static m => m.Function)))
				: RejectIteration(node, $"'{type.Name}' has no '*' operator", location);
		}
		
		var element = Decay(CallOperator(ReadCursor(), dereference, syntax));
		var isWritable = isMut && TakesReceiver(dereference, type, ParameterMode.Mut);
		var binding = CreateLoopBinding(node.Binding, element.Type, isWritable || !_typePool.IsCopy(element.Type),
			isWritable)!;
		
		return new(binding, (body, label) => new ResolvedCursorForStatementNode(new(cursor, cursorValue), stepCall,
			new(binding, element), body, label, node));
		
		IResolvedExpressionNode ReadCursor() => cursor.IsBorrowBinding
			? Dereference(cursor, syntax)
			: new ResolvedVarExpressionNode(cursor, cursor.Type, syntax);
	}
	
	private List<MethodSymbol> FindLoopOperators(TypeSymbol type, string name) =>
		[..GetMethods(type, name).Where(static method => method.HasReceiver)];
	
	private ForHeader RejectIteration(ForStatementNode node, string message, SourceLocation location) =>
		RejectIteration(node, new Diagnostic(DiagnosticSeverity.Error, location, message));
	
	private ForHeader RejectIteration(ForStatementNode node, Diagnostic diagnostic)
	{
		Diagnostics.Add(diagnostic);
		return InvalidHeader(node, CreateLoopBinding(node.Binding, NativeSymbols.Invalid, false, false));
	}
	
	private static ForHeader InvalidHeader(ForStatementNode node, LocalVariableSymbol? binding) =>
		new(binding, (_, _) => new ResolvedInvalidStatementNode(node));
	
	private LocalVariableSymbol? CreateLoopBinding(Token token, TypeSymbol type, bool isBorrow, bool isMut) =>
		token.Text == "_"
			? null
			: new(token, isBorrow && type is not InvalidType ? _typePool.GetPointerType(type) : type, false)
			{
				IsLoopBinding = true,
				IsBorrowBinding = isBorrow,
				IsMutBinding = isMut
			};
	
	private static Token HiddenName(Token binding, string name) =>
		new(TokenType.Identifier, binding.SourceLocation, $".{name}");
	
	private static ResolvedUnaryOpExpressionNode Dereference(LocalVariableSymbol pointer, IExpressionNode syntax) =>
		new(new ResolvedVarExpressionNode(pointer, pointer.Type, syntax),
			new NativeImpl(TokenType.OpStar, ((PointerType)pointer.Type).BaseType), syntax);
	
	public IResolvedStatementNode Visit(LoopStatementNode node)
	{
		// Create a scope for the body and label (if applicable)
		var scope = CurrentScope?.CreateChild() ?? new();
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		
		LabelSymbol? symbol;
		if (node.Label is { } label)
		{
			symbol = new(label);
			scope.Define(symbol);
		}
		else
			symbol = null;
		
		_resolutionContexts.Push(resolutionContext);
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedLoopStatementNode(body, symbol, node);
	}
	
	public IResolvedExpressionNode Visit(UnaryOpExpressionNode node)
	{
		var op = node.Op;
		var isLiteral = node.Operand is LiteralExpressionNode;
		if (isLiteral)
			_unaryOpJobs.Push(new(op.Type));
		
		var outerTarget = _placeTarget;
		if (op.Type == TokenType.OpAt)
			_placeTarget = node.Operand;
		
		var operand = VisitNode(node.Operand, null);
		_placeTarget = outerTarget;
		var consumed = isLiteral && _unaryOpJobs.Pop().Consumed;
		
		if (consumed)
			return operand;
		
		if (IsInvalid(operand))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		operand = Decay(operand);
		switch (op.Type)
		{
			// Special unary operators that aren't stored in the registry
			case TokenType.OpAt:
				return ResolveAddressOf(op.Type, operand, node);
			
			case TokenType.OpStar:
				return ResolveDereference(op.Type, operand, node);
			
			default:
			{
				if (FoldLiteral(node, operand) is { } folded)
					return folded;
				
				if (ResolveDeclaredUnary(node, operand) is { } declared)
					return declared;
				
				if (op.Type == TokenType.OpMinus && operand.Type is IntegerType { IsSigned: false })
				{
					var negation = new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
						$"Operator '-' cannot be applied to '{operand.Type.Name}'")
					{
						Hints = [$"'{operand.Type.Name}' can't represent negative values"]
					};
					
					return Error(node, negation, CurrentTargetType);
				}
				
				var candidates = _operatorRegistry.GetUnaryCandidates(op.Type);
				var operandArray = new[] { operand };
				var resolutionSet = ResolveCallable(candidates, operandArray, MaterializationMode.Overload);
				
				if (resolutionSet.IsAmbiguous)
					return Error(node, $"Ambiguous operation '{op.Text}' on '{operand.Type.Name}'", CurrentTargetType);
				
				if (!resolutionSet.HasResult)
					return Error(node, $"Operator '{op.Text}' cannot be applied to '{operand.Type.Name}'",
						CurrentTargetType);
				
				var resolution = resolutionSet[0];
				var resolvedOperand = ApplyArgumentResolution(operandArray, resolution);
				var operation = (OperationImpl)resolution.Callable;
				var result = new ResolvedUnaryOpExpressionNode(resolvedOperand[0], operation, node);
				
				return ApplyResultResolution(result, resolution);
			}
		}
	}
	
	private IResolvedExpressionNode ResolveDereference(TokenType opType, IResolvedExpressionNode operand,
		IExpressionNode node) => operand.Type switch
	{
		PointerType { BaseType: { } baseType } => baseType == NativeSymbols.Void
			? Error(node, "Cannot dereference an untyped pointer; Cast to a typed pointer first", CurrentTargetType)
			: new ResolvedUnaryOpExpressionNode(operand, new NativeImpl(opType, baseType), node),
		var type when (FindDereference(type, ParameterMode.ReadOnly) ?? FindDereference(type, ParameterMode.Mut)) is
			{ } dereference => Decay(CallOperator(operand, dereference, node)),
		var type when GetMethods(type, "*").Where(static method => method.HasReceiver).ToList() is
			{ Count: > 0 } hidden => Error(node, ReportHiddenMember(node.SourceLocation, "*",
			hidden.Select(static method => method.Function)), CurrentTargetType),
		_ => Error(node, $"Cannot dereference type '{operand.Type.Name}'", CurrentTargetType)
	};
	
	private ResolvedUnaryOpExpressionNode ResolveAddressOf(TokenType opType, IResolvedExpressionNode operand,
		UnaryOpExpressionNode node)
	{
		var ptrType = _typePool.GetPointerType(operand.Type);
		return new(operand, new NativeImpl(opType, ptrType), node);
	}
	
	private static bool IsAssignment(TokenType op) => op is TokenType.OpEqual or TokenType.OpPlusEqual
		or TokenType.OpMinusEqual or TokenType.OpStarEqual or TokenType.OpSlashEqual or TokenType.OpPercentEqual
		or TokenType.OpPlusPercentEqual or TokenType.OpMinusPercentEqual or TokenType.OpStarPercentEqual
		or TokenType.OpAmpersandEqual or TokenType.OpBarEqual or TokenType.OpHatEqual or TokenType.OpLessLessEqual
		or TokenType.OpGreaterGreaterEqual or TokenType.OpLessLessLessEqual or TokenType.OpGreaterGreaterGreaterEqual;
	
	public IResolvedExpressionNode Visit(BinaryOpExpressionNode node)
	{
		var op = node.Op;
		if (IsAssignment(op.Type))
		{
			var isOwnStore = node.Left is OwnExpressionNode;
			var outerTarget = storeTarget;
			storeTarget = node.Left is OwnExpressionNode target ? target.Value : node.Left;
			var left = VisitNode(storeTarget, null);
			storeTarget = outerTarget;
			if (FindStoredLocals(left) is [_, ..] stored)
				return RecordStore(node, stored);
			
			if (left is ResolvedPropertyExpressionNode property)
				return ResolvePropertyAssignment(node, property, isOwnStore);
			
			if (left is ResolvedFunctionGroupExpressionNode)
			{
				VisitNode(node.Right, null);
				return Error(node, "Cannot reassign functions", CurrentTargetType, node.Left);
			}
			
			left = MakeWritable(left);
			
			if (op.Type != TokenType.OpEqual)
				return ResolveCompoundAssignment(node, Decay(MaterializeAsDefault(left)), isOwnStore);
			
			if (left.Type is BorrowType borrow)
				return ResolveBorrowAssignment(node, left, borrow, isOwnStore);
			
			var right = VisitNode(node.Right, left.Type);
			return new ResolvedAssignmentExpressionNode(left.Type, left, op, right, null, node, isOwnStore);
		}
		else
		{
			var candidates = _operatorRegistry.GetBinaryCandidates(op.Type);
			var left = Decay(VisitOperand(node.Left, candidates, 0));
			var isConjunction = op.Type == TokenType.OpAmpersandAmpersand;
			var right = Decay(isConjunction
				? VisitInScope(node.Right, GetTrueBindings(left))
				: VisitOperand(node.Right, [..candidates, ..FindOperatorCallables(left.Type, op.Text)], 1));
			
			if (isConjunction)
				ReportRepeatedBindings(GetTrueBindings(left).Concat(GetTrueBindings(right)));
			
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
			if (FoldLiterals(node, left, right) is { } folded)
				return folded;
			
			if (_survey is { } survey && left.Type is UntypedType && right.Type is UntypedType)
			{
				survey.MarkDefaulted(left);
				survey.MarkDefaulted(right);
			}
			
			if (ResolveDeclaredOperator(node, left, right) is { } declared)
				return declared;
			
			if (op.Type == TokenType.OpPlus && IsString(left.Type) && IsString(right.Type))
				return ConcatenateStrings(node, left, right);
			
			if (IsMixedSignComparison(op.Type, left.Type, right.Type))
			{
				var comparison = new NativeImpl(op.Type, NativeSymbols.Bool, left.Type, right.Type);
				return new ResolvedBinaryOpExpressionNode(left, right, comparison, node);
			}
			
			if (!IsShiftOrRotate(op.Type) &&
			    FindLossyMixedSign(left.Type, right.Type) is var (signedType, unsignedType))
			{
				if (FindMixedSignType(signedType, unsignedType) is not { } mixedType)
				{
					var hint = $"'{signedType.Name}' can't represent every '{unsignedType.Name}' value";
					if (CountBits(signedType) > CountBits(unsignedType))
						hint += signedType == NativeSymbols.IntSize ? " on 32-bit targets" : " on 64-bit targets";
					
					var mismatch = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right);
					return Error(node, mismatch with { Hints = [hint] }, CurrentTargetType);
				}
				
				candidates = [..candidates.Where(candidate => candidate.ParameterTypes.All(type => type == mixedType))];
			}
			
			var args = new[] { left, right };
			var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
			if (resolutionSet.IsAmbiguous)
				return Error(node,
					$"Ambiguous operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'",
					CurrentTargetType);
			
			if (!resolutionSet.HasResult)
			{
				left = ResolveUnmatched(left);
				right = ResolveUnmatched(right);
				if (AnyInvalid(left, right))
					return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
				
				var diagnostic = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right);
				return Error(node, diagnostic, CurrentTargetType);
			}
			
			var resolution = resolutionSet[0];
			var resolvedArgs = ApplyArgumentResolution(args, resolution);
			var operation = (OperationImpl)resolution.Callable;
			
			if (FindShiftRangeError(node, resolvedArgs[0].Type, resolvedArgs[1]) is { } rangeError)
				return Error(node, rangeError, CurrentTargetType);
			
			var result = new ResolvedBinaryOpExpressionNode(resolvedArgs[0], resolvedArgs[1], operation, node);
			return ApplyResultResolution(result, resolution);
		}
	}
	
	private ResolvedLiteralExpressionNode? FoldLiteral(UnaryOpExpressionNode node, IResolvedExpressionNode operand)
	{
		if (AsNumberLiteral(operand) is not { } literal)
			return null;
		
		object? value = (node.Op.Type, literal.Value) switch
		{
			(TokenType.OpPlus, var number) => number,
			(TokenType.OpMinus, BigInteger integer) => -integer,
			(TokenType.OpMinus, double number) => -number,
			(TokenType.OpMinus, string text) => text.StartsWith('-') ? text[1..] : $"-{text}",
			(TokenType.OpTilde, BigInteger integer) => ~integer,
			_ => null
		};
		
		return value is null ? null : CreateFold(node, node.Op.Type, literal.Type, value, [literal]);
	}
	
	private ResolvedLiteralExpressionNode? FoldLiterals(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		IResolvedExpressionNode right)
	{
		if (AsNumberLiteral(left) is not { } leftLiteral || AsNumberLiteral(right) is not { } rightLiteral)
			return null;
		
		if (leftLiteral.Value is BigInteger leftInteger && rightLiteral.Value is BigInteger rightInteger)
		{
			var integer = node.Op.Type is TokenType.OpLessLessLess or TokenType.OpGreaterGreaterGreater
				? FoldRotation(node.Op.Type, leftLiteral, rightInteger)
				: FoldIntegers(node.Op.Type, leftInteger, rightInteger);
			
			return integer is { } value
				? CreateFold(node, node.Op.Type, NativeSymbols.UntypedInteger, value, [leftLiteral, rightLiteral])
				: null;
		}
		
		return ToDouble(leftLiteral) is { } leftNumber && ToDouble(rightLiteral) is { } rightNumber &&
		       FoldFloats(node.Op.Type, leftNumber, rightNumber) is { } number
			? CreateFold(node, node.Op.Type, NativeSymbols.UntypedFloat, number, [leftLiteral, rightLiteral])
			: null;
	}
	
	private ResolvedLiteralExpressionNode CreateFold(IExpressionNode node, TokenType op, TypeSymbol type, object value,
		ImmutableArray<ResolvedLiteralExpressionNode> operands)
	{
		var folded = new ResolvedLiteralExpressionNode(type, value, node);
		if (type is UntypedIntegerType)
			_literalFolds[folded] = new(op, operands);
		
		_survey?.Track(folded, operands.SelectMany(operand => _survey.GetTracked(operand)));
		return folded;
	}
	
	private static ResolvedLiteralExpressionNode? AsNumberLiteral(IResolvedExpressionNode node) =>
		node is ResolvedLiteralExpressionNode { Type: UntypedIntegerType or UntypedFloatType } literal ? literal : null;
	
	private BigInteger? FoldIntegers(TokenType op, BigInteger left, BigInteger right) => op switch
	{
		TokenType.OpPlus or TokenType.OpPlusPercent => left + right,
		TokenType.OpMinus or TokenType.OpMinusPercent => left - right,
		TokenType.OpStar or TokenType.OpStarPercent => left * right,
		TokenType.OpSlash when !right.IsZero => BigInteger.Divide(left, right),
		TokenType.OpPercent when !right.IsZero => BigInteger.Remainder(left, right),
		TokenType.OpAmpersand => left & right,
		TokenType.OpBar => left | right,
		TokenType.OpHat => left ^ right,
		TokenType.OpLessLess when IsFoldableShift(right) => left << (int)right,
		TokenType.OpGreaterGreater when IsFoldableShift(right) => left >> (int)right,
		_ => null
	};
	
	private BigInteger? FoldRotation(TokenType op, ResolvedLiteralExpressionNode value, BigInteger amount) =>
		FindDefaultIntegerType(value) is { } type
			? _evaluator.Rotate(DefaultIntegerValue(value), amount, type, op == TokenType.OpLessLessLess)
			: null;
	
	private bool IsFoldableShift(BigInteger amount) =>
		amount.Sign >= 0 && amount < CountBits(NativeSymbols.UInt128);
	
	private static double? FoldFloats(TokenType op, double left, double right) => op switch
	{
		TokenType.OpPlus => left + right,
		TokenType.OpMinus => left - right,
		TokenType.OpStar => left * right,
		TokenType.OpSlash => left / right,
		TokenType.OpPercent => left % right,
		_ => null
	};
	
	private double? ToDouble(ResolvedLiteralExpressionNode literal) => literal.Type switch
	{
		UntypedFloatType => ParseFloatValue(literal.Value!, NativeSymbols.Float64),
		_ when DefaultIntegerValue(literal) is var integer && IsExactInFloat(integer, NativeSymbols.Float64) =>
			(double)integer,
		_ => null
	};
	
	private BigInteger? EvaluateIn(ResolvedLiteralExpressionNode literal, IntegerType type)
	{
		if (!_literalFolds.TryGetValue(literal, out var fold))
			return literal.Value is BigInteger value && FitsInType(value, type) ? value : null;
		
		if (EvaluateIn(fold.Operands[0], type) is not { } left)
			return null;
		
		if (fold.Operands.Length == 1)
			return fold.Op switch
			{
				TokenType.OpMinus => IfFits(-left, type),
				TokenType.OpTilde => _evaluator.Wrap(~left, type),
				_ => left
			};
		
		if (fold.Op is TokenType.OpLessLess or TokenType.OpGreaterGreater)
		{
			if (fold.Operands[1].Value is not BigInteger amount || amount.Sign < 0 || amount >= CountBits(type))
				return null;
			
			return fold.Op == TokenType.OpLessLess ? _evaluator.Wrap(left << (int)amount, type) : left >> (int)amount;
		}
		
		if (fold.Op is TokenType.OpLessLessLess or TokenType.OpGreaterGreaterGreater)
			return _evaluator.Rotate(left, (BigInteger)fold.Operands[1].Value!, type,
				fold.Op == TokenType.OpLessLessLess);
		
		if (EvaluateIn(fold.Operands[1], type) is not { } right)
			return null;
		
		return fold.Op switch
		{
			TokenType.OpPlus => IfFits(left + right, type),
			TokenType.OpMinus => IfFits(left - right, type),
			TokenType.OpStar => IfFits(left * right, type),
			TokenType.OpPlusPercent => _evaluator.Wrap(left + right, type),
			TokenType.OpMinusPercent => _evaluator.Wrap(left - right, type),
			TokenType.OpStarPercent => _evaluator.Wrap(left * right, type),
			TokenType.OpSlash when !right.IsZero => IfFits(BigInteger.Divide(left, right), type),
			TokenType.OpPercent when !right.IsZero => BigInteger.Remainder(left, right),
			TokenType.OpAmpersand => left & right,
			TokenType.OpBar => left | right,
			TokenType.OpHat => left ^ right,
			_ => null
		};
	}
	
	private BigInteger? IfFits(BigInteger value, IntegerType type) => FitsInType(value, type) ? value : null;
	
	private BigInteger? IntegerValueIn(ResolvedLiteralExpressionNode literal, IntegerType type) =>
		EvaluateIn(literal, type) ?? IfFits((BigInteger)literal.Value!, type);
	
	private BigInteger DefaultIntegerValue(ResolvedLiteralExpressionNode literal) =>
		FindDefaultIntegerType(literal) is { } type && IntegerValueIn(literal, type) is { } value
			? value
			: (BigInteger)literal.Value!;
	
	private IResolvedExpressionNode ResolveBorrowAssignment(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		BorrowType borrow, bool isOwnStore)
	{
		if (node.Right is BorrowExpressionNode)
			return new ResolvedAssignmentExpressionNode(borrow, left, node.Op, VisitNode(node.Right, borrow), null,
				node, isOwnStore);
		
		var right = VisitNode(node.Right, borrow.Target);
		if (right is ResolvedUnaryOpExpressionNode
		    {
			    Operation.Op: TokenType.OpStar, Operand: { Type: BorrowType source } borrowed
		    } && (source == borrow || !borrow.IsMutable && source.Target == borrow.Target))
			right = CoerceToType(borrowed, borrow);
		else if (!borrow.IsMutable && right is not ResolvedInvalidExpressionNode)
			right = CoerceToType(right, borrow);
		else
			return new ResolvedAssignmentExpressionNode(borrow.Target, Decay(left), node.Op, right, null, node,
				isOwnStore);
		
		return new ResolvedAssignmentExpressionNode(borrow, left, node.Op, right, null, node, isOwnStore);
	}
	
	private IResolvedExpressionNode ResolveCompoundAssignment(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		bool isOwnStore)
	{
		if (ResolveDeclaredCompound(node, left) is { } declared)
			return declared;
		
		return ResolveCompoundOperation(node, left) is var (right, operation)
			? new ResolvedAssignmentExpressionNode(left.Type, left, node.Op, right, operation, node, isOwnStore)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IEnumerable<(TypeSymbol Owner, MethodSymbol Method)> FindOperators(TypeSymbol type, string name) =>
		GetMethods(type, name)
			.Where(static method => !method.HasReceiver)
			.Select(method => (type, method));
	
	private IEnumerable<ICallable> FindOperatorCallables(TypeSymbol type, string name) => FindOperators(type, name)
		.Where(entry => CanAccess(entry.Owner, entry.Method.Function))
		.Select(entry => new FunctionCallable(GetFunctionInfo(entry.Method, entry.Owner)));
	
	private IResolvedExpressionNode? ResolveDeclaredOperator(BinaryOpExpressionNode node, IResolvedExpressionNode left,
		IResolvedExpressionNode right)
	{
		var name = node.Op.Text;
		var declared = FindOperators(left.Type, name).ToList();
		if (right.Type != left.Type)
			declared.AddRange(FindOperators(right.Type, name));
		
		if (declared.Count == 0)
			return null;
		
		var accessible = declared
			.Where(entry => CanAccess(entry.Owner, entry.Method.Function))
			.Select(entry => new FunctionCallable(GetFunctionInfo(entry.Method, entry.Owner)))
			.ToArray();
		
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(node.Op.SourceLocation, name,
				declared.Select(static entry => entry.Method.Function)), CurrentTargetType);
		
		var args = new[] { left, right };
		ICallable[] candidates =
		[
			..accessible
				.Select(candidate => Specialize(candidate, name, args, null, node.Op.SourceLocation, []))
				.OfType<ICallable>()
		];
		
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Ambiguous operation '{name}' between '{left.Type.Name}' and '{right.Type.Name}'",
				CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return null;
		
		var resolution = resolutionSet[0];
		var info = ((FunctionCallable)resolution.Callable).Info;
		TrackFunctionUse(info, node);
		return new ResolvedFunctionCallExpressionNode(info, ApplyArgumentResolution(args, resolution), node);
	}
	
	private IResolvedExpressionNode? ResolveDeclaredUnary(UnaryOpExpressionNode node, IResolvedExpressionNode operand)
	{
		var name = node.Op.Text;
		var declared = GetMethods(operand.Type, name).Where(static method => method.HasReceiver).ToList();
		if (declared.Count == 0)
			return null;
		
		if (declared.FirstOrDefault(method => CanAccess(operand.Type, method.Function)) is not { } unary)
			return Error(node, ReportHiddenMember(node.Op.SourceLocation, name,
				declared.Select(static method => method.Function)), CurrentTargetType);
		
		var info = GetFunctionInfo(unary, operand.Type);
		TrackFunctionUse(info, node);
		return new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(operand, info)], node);
	}
	
	private IResolvedExpressionNode? ResolveDeclaredCompound(BinaryOpExpressionNode node, IResolvedExpressionNode left)
	{
		var name = node.Op.Text;
		var declared = GetMethods(left.Type, name).Where(static method => method.HasReceiver).ToList();
		if (declared.Count == 0)
			return null;
		
		ICallable[] candidates =
		[
			..declared
				.Where(method => CanAccess(left.Type, method.Function))
				.Select(method => GetFunctionInfo(method, left.Type))
				.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (candidates.Length == 0)
			return RejectAssignment(node, ReportHiddenMember(node.Op.SourceLocation, name,
				declared.Select(static method => method.Function)));
		
		var right = VisitArgument(node.Right, ParameterTypesAt(candidates, 0));
		if (IsInvalid(right))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		candidates =
		[
			..candidates
				.Select(candidate => Specialize(candidate, name, [right], null, node.Op.SourceLocation, []))
				.OfType<ICallable>()
		];
		
		var resolutionSet = ResolveCallable(candidates, [right], MaterializationMode.Overload);
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Ambiguous operation '{name}' between '{left.Type.Name}' and '{right.Type.Name}'",
				CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, node.Op, right),
				CurrentTargetType);
		
		var resolution = resolutionSet[0];
		var info = ((ReceiverCallable)resolution.Callable).Info;
		TrackFunctionUse(info, node);
		var args = ApplyArgumentResolution([right], resolution);
		args.Insert(0, CreateReceiver(left, info));
		return new ResolvedFunctionCallExpressionNode(info, args, node);
	}
	
	private (IResolvedExpressionNode Right, OperationImpl Operation)? ResolveCompoundOperation(
		BinaryOpExpressionNode node, IResolvedExpressionNode left)
	{
		var candidates = _operatorRegistry.GetBinaryCandidates(node.Op.Type)
			.Where(candidate => candidate.ReturnType == left.Type && candidate.ParameterTypes[0] == left.Type)
			.ToList();
		
		var rightTypes = candidates.Select(static candidate => candidate.ParameterTypes[1]).Distinct().ToList();
		var right = VisitNode(node.Right, rightTypes.Count == 1 ? rightTypes[0] : null);
		if (AnyInvalid(left, right))
			return null;
		
		var args = new[] { left, right };
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		if (resolutionSet.Count != 1)
		{
			Diagnostics.Add(DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, node.Op, right));
			return null;
		}
		
		var resolution = resolutionSet[0];
		var resolvedRight = ApplyArgumentResolution(args, resolution)[1];
		if (FindShiftRangeError(node, left.Type, resolvedRight) is { } rangeError)
		{
			Diagnostics.Add(rangeError);
			return null;
		}
		
		return (resolvedRight, (OperationImpl)resolution.Callable);
	}
	
	private IResolvedExpressionNode ResolvePropertyAssignment(BinaryOpExpressionNode node,
		ResolvedPropertyExpressionNode target, bool isOwnStore)
	{
		var property = target.Property;
		var member = GetMemberLocation(target.Syntax);
		if (isOwnStore)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, node.Left.SourceLocation,
				"Cannot assign with 'own' except through pointers"));
		
		if (property.Setter is not FunctionAccessor { Function: var setter })
			return RejectAssignment(node, new(DiagnosticSeverity.Error, target.Syntax.SourceLocation,
				"Cannot reassign read-only properties"));
		
		var read = node.Op.Type == TokenType.OpEqual ? null : (property.Getter as FunctionAccessor)?.Function;
		if (target.Owner is DynType dyn && (!IsDynMember(setter) || read is not null && !IsDynMember(read)))
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member,
				$"Cannot use '{property.Name}' through '{dyn.Name}'"));
		
		if (FindReceiverError(setter.Kind == FunctionKind.Free, target.Receiver) is { } setterError)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, setterError));
		
		if (!CanAccess(target.Owner, setter))
			return RejectAssignment(node, DiagnosticReporter.ReportReadOnly(member, property.Name, setter.Visibility));
		
		var setterInfo = GetFunctionInfo(setter, target.Owner, property.TraitArguments);
		var valueType = setterInfo.Signature.GetDeclaredType(setterInfo.Signature.ParameterTypes.Length - 1);
		if (node.Op.Type == TokenType.OpEqual)
			return CallAccessor(setter, target.Owner, property.TraitArguments, target.Receiver,
				[VisitNode(node.Right, valueType)], node);
		
		if (property.Getter is not FunctionAccessor { Function: var getter })
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, "Cannot read write-only properties"));
		
		if (FindReceiverError(getter.Kind == FunctionKind.Free, target.Receiver) is { } getterError)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, getterError));
		
		if (!CanAccess(target.Owner, getter))
			return RejectAssignment(node, DiagnosticReporter.ReportWriteOnly(member, property.Name, getter.Visibility));
		
		var getterInfo = GetFunctionInfo(getter, target.Owner, property.TraitArguments);
		var receiver = target.Receiver is { } place && (TakesMutSelf(getterInfo) || TakesMutSelf(setterInfo))
			? MakeWritable(place)
			: target.Receiver;
		
		var current = CallAccessor(getter, target.Owner, property.TraitArguments, receiver, [], target.Syntax);
		if (current.Type == valueType && ResolveDeclaredCompound(node, current) is { } declared)
		{
			if (declared is not ResolvedFunctionCallExpressionNode { Arguments: [_, var value] } call)
				return declared;
			
			TrackFunctionUse(setterInfo, node);
			return new ResolvedPropertyAssignmentExpressionNode(getterInfo, setterInfo, receiver, node.Op, value, null,
				node)
			{
				Compound = call.Function
			};
		}
		
		if (current.Type != valueType || ResolveCompoundOperation(node, current) is not var (right, operation))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		TrackFunctionUse(setterInfo, node);
		return new ResolvedPropertyAssignmentExpressionNode(getterInfo, setterInfo, receiver, node.Op, right,
			operation, node);
	}
	
	private ResolvedInvalidExpressionNode RejectAssignment(BinaryOpExpressionNode node, Diagnostic diagnostic)
	{
		VisitNode(node.Right, null);
		return Error(node, diagnostic, CurrentTargetType);
	}
	
	public IResolvedExpressionNode Visit(ChainedExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		var operands = new List<IResolvedExpressionNode>(node.Operands.Length);
		foreach (var operand in node.Operands)
			operands.Add(Decay(VisitNode(operand, null)));
		
		if (Enumerable.Range(0, node.Ops.Length)
		    .Any(i => DeclaresOperator(operands[i].Type, operands[i + 1].Type, node.Ops[i].Text)))
			return ResolveDeclaredChain(node, operands);
		
		// TODO Do we need common types anymore?
		var commonType = UnifyTypes(operands);
		if (commonType is null)
			return Error(node, "Cannot chain comparisons between incompatible types", CurrentTargetType);
		
		for (var i = 0; i < operands.Count; i++)
			operands[i] = CoerceToType(operands[i], commonType);
		
		var links = new List<ChainLink>(node.Ops.Length);
		for (var i = 0; i < operands.Count - 1; i++)
		{
			var left = operands[i];
			var op = node.Ops[i];
			var right = operands[i + 1];
			
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
			// TODO Allow types other than bool?
			var args = new[] { left, right };
			var candidates = _operatorRegistry.GetBinaryCandidates(op.Type);
			var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload, NativeSymbols.Bool);
			
			if (resolutionSet.IsAmbiguous)
			{
				var (source, range) = left.Syntax.SourceLocation;
				range = range.Join(right.Syntax.SourceLocation.Range);
				
				return Error(node,
					$"Ambiguous operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'",
					CurrentTargetType, new SourceLocation(source, range));
			}
			
			if (!resolutionSet.HasResult)
			{
				var diagnostic = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right);
				return Error(node, diagnostic, CurrentTargetType);
			}
			
			var resolution = resolutionSet[0];
			var resolvedArgs = ApplyArgumentResolution(args, resolution);
			
			operands[i] = resolvedArgs[0];
			operands[i + 1] = resolvedArgs[1];
			links.Add(new ChainLink((OperationImpl)resolution.Callable, null));
		}
		
		// TODO Aggregate types with implicit AND?
		
		return new ResolvedChainedExpressionNode(NativeSymbols.Bool, operands, links, node);
	}
	
	private bool DeclaresOperator(TypeSymbol left, TypeSymbol right, string name) =>
		FindOperators(left, name).Any() || FindOperators(right, name).Any();
	
	private IResolvedExpressionNode ResolveDeclaredChain(ChainedExpressionNode node,
		List<IResolvedExpressionNode> operands)
	{
		if (AnyInvalid([..operands]))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var links = new List<ChainLink>(node.Ops.Length);
		for (var i = 0; i < node.Ops.Length; i++)
		{
			var op = node.Ops[i];
			var args = new[] { operands[i], operands[i + 1] };
			var (source, range) = args[0].Syntax.SourceLocation;
			var location = new SourceLocation(source, range.Join(args[1].Syntax.SourceLocation.Range));
			var declared = FindOperators(args[0].Type, op.Text).ToList();
			if (args[1].Type != args[0].Type)
				declared.AddRange(FindOperators(args[1].Type, op.Text));
			
			ICallable[] candidates = declared.Count == 0
				? [.._operatorRegistry.GetBinaryCandidates(op.Type)]
				:
				[
					..declared
						.Where(entry => CanAccess(entry.Owner, entry.Method.Function))
						.Select(entry => new FunctionCallable(GetFunctionInfo(entry.Method, entry.Owner)))
				];
			
			if (candidates.Length == 0)
				return Error(node, ReportHiddenMember(op.SourceLocation, op.Text,
					declared.Select(static entry => entry.Method.Function)), CurrentTargetType);
			
			var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload,
				declared.Count == 0 ? NativeSymbols.Bool : null);
			
			if (resolutionSet.IsAmbiguous)
				return Error(node,
					$"Ambiguous operation '{op.Text}' between '{args[0].Type.Name}' and '{args[1].Type.Name}'",
					CurrentTargetType, location);
			
			if (!resolutionSet.HasResult)
				return Error(node, DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, args[0], op, args[1]),
					CurrentTargetType);
			
			var resolution = resolutionSet[0];
			if (resolution.Callable.ReturnType != NativeSymbols.Bool)
				return Error(node, $"Cannot chain '{op.Text}' results of type '{resolution.Callable.ReturnType.Name}'",
					CurrentTargetType, location);
			
			for (var j = 0; j < 2; j++)
				operands[i + j] = MaterializeChainOperand(args[j], resolution.Callable.ParameterTypes[j]);
			
			var function = resolution.Callable is FunctionCallable callable ? callable.Info : (FunctionInfo?)null;
			if (function is { } info && FindSharedMove(operands, i, info) is { } shared)
				return Error(node, "Cannot move operands shared by two comparisons", CurrentTargetType,
					shared.Syntax.SourceLocation);
			
			if (function is { } used)
				TrackFunctionUse(used, node);
			
			links.Add(new ChainLink(resolution.Callable as OperationImpl, function)
			{
				LeftConversion = resolution.ArgumentConversions[0],
				RightConversion = resolution.ArgumentConversions[1]
			});
		}
		
		return new ResolvedChainedExpressionNode(NativeSymbols.Bool, operands, links, node);
	}
	
	private IResolvedExpressionNode MaterializeChainOperand(IResolvedExpressionNode operand, TypeSymbol target)
	{
		if (operand.Type is not UntypedType)
			return operand;
		
		var materialized = MaterializeExpression(operand, target);
		return materialized.Type is UntypedType ? MaterializeAsDefault(materialized) : materialized;
	}
	
	private IResolvedExpressionNode? FindSharedMove(List<IResolvedExpressionNode> operands, int link,
		FunctionInfo function)
	{
		for (var j = 0; j < 2; j++)
		{
			var index = link + j;
			if (index > 0 && index < operands.Count - 1 && function.Signature.GetMode(j) == ParameterMode.Own &&
			    !_typePool.IsCopy(operands[index].Type))
				return operands[index];
		}
		
		return null;
	}
	
	private static bool IsBorrowedFunctionParameter(IResolvedExpressionNode node) =>
		node is ResolvedVarExpressionNode { Symbol: ParameterSymbol { Mode: ParameterMode.ReadOnly } } &&
		node.Type is FunctionType { IsRef: true, IsExternal: false };
	
	private bool IsMovedFunctionParameter(IResolvedExpressionNode node, TypeSymbol target) =>
		IsBorrowedFunctionParameter(node) && node.Type is FunctionType function &&
		_typePool.GetPlainFunctionType(function) == target;
	
	private static string GetDisplayName(IResolvedExpressionNode node) =>
		IsBorrowedFunctionParameter(node) && node.Type is FunctionType function ? function.PlainName : node.Type.Name;
	
	[return: NotNullIfNotNull(nameof(source))]
	private IResolvedExpressionNode? ApplyImplicitConversion(IResolvedExpressionNode? source, TypeSymbol target)
	{
		if (source is null)
			return null;
		
		if (source.Type == target)
			return source;
		
		if (IsInvalid(source) || IsInvalid(target))
			return new ResolvedConversionExpressionNode(source, new IdentityConversion(target), source.Syntax);
		
		if (target is BorrowType { IsMutable: false } borrow && source.Type == borrow.Target)
			return new ResolvedBorrowExpressionNode(source, borrow, true, source.Syntax);
		
		if (target is FStrType fstr && Decay(source).Type == NativeSymbols.Str)
			return CreateTextTemplate(Decay(source), fstr);
		
		if (IsDynTarget(target) && ConvertToDyn(source, target) is { } dynValue)
			return dynValue;
		
		if (target is not BorrowType && Decay(source) is var decayed && decayed != source)
			return ApplyImplicitConversion(decayed, target);
		
		if (_conversionTable.FindImplicit(source.Type, target) is { } conversion)
			return new ResolvedConversionExpressionNode(source, conversion, source.Syntax);
		
		if (FindUserConversion(source, target) is { } userConversion)
			return ApplyUserConversion(source, userConversion);
		
		if (IsDynTarget(target) && ConvertToDyn(source, target) is { } erased)
			return erased;
		
		if (source is ResolvedFunctionGroupExpressionNode group)
			return ReportFunctionMismatch(group, target);
		
		if (IsMovedFunctionParameter(source, target))
			return Error(source.Syntax, "Cannot move read-only parameters", target);
		
		if (source.Type is ClosureType && target is ClosureType)
			return Error(source.Syntax, "Cannot convert between 'own fun' closures", target);
		
		return Error(source.Syntax, $"Cannot convert type '{source.Type.Name}' to '{target.Name}'", target);
	}
	
	private IResolvedExpressionNode ResolveSuffixedLiteral(LiteralExpressionNode node, string suffix)
	{
		var type = NativeSymbols.Resolve(suffix) as PrimitiveType;
		if (type is not (FloatType or IntegerType { Kind: not PrimitiveTypeKind.Char }))
			return Error(node, $"Invalid suffix '{suffix}'", CurrentTargetType);
		
		return (node.Token.Type, type) switch
		{
			(TokenType.IntegerLiteral, IntegerType integer) => ResolveSuffixedInteger(node, integer),
			(TokenType.IntegerLiteral or TokenType.FloatLiteral, FloatType floating) =>
				ResolveSuffixedFloat(node, floating),
			(TokenType.CharLiteral, IntegerType integer) => ResolveSuffixedChar(node, integer),
			(TokenType.StringLiteral, IntegerType
			{
				Kind: PrimitiveTypeKind.UInt8 or PrimitiveTypeKind.UInt16 or PrimitiveTypeKind.UInt32
			} unit) => ResolveCodeUnits(node, unit),
			(TokenType.FloatLiteral, _) => Error(node, $"Cannot use '{suffix}' on float literals", CurrentTargetType),
			(TokenType.CharLiteral, _) => Error(node, $"Cannot use '{suffix}' on char literals", CurrentTargetType),
			_ => Error(node, $"Cannot use '{suffix}' on string literals", CurrentTargetType)
		};
	}
	
	private IResolvedExpressionNode ResolveSuffixedInteger(LiteralExpressionNode node, IntegerType type)
	{
		var text = type.IsSigned ? ConsumeNegation(node.Token.AsSpan()) : node.Token.Text;
		return Scanner.TryParseInteger(text, out var value) && _evaluator.Fits(value, type)
			? new ResolvedLiteralExpressionNode(type, value, node)
			: Error(node, $"'{text}' doesn't fit in '{type.Name}'", CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveSuffixedFloat(LiteralExpressionNode node, FloatType type)
	{
		var text = ConsumeNegation(node.Token.AsSpan());
		var value = ParseFloatValue(text.Replace("_", ""), type);
		return double.IsFinite(value)
			? new ResolvedLiteralExpressionNode(type, value, node)
			: Error(node, $"'{text}' doesn't fit in '{type.Name}'", CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveSuffixedChar(LiteralExpressionNode node, IntegerType type)
	{
		var (_, code) = ParseChar(node.Token.AsSpan());
		return _evaluator.Fits(code, type)
			? new ResolvedLiteralExpressionNode(type, new BigInteger(code), node)
			: Error(node, $"'{node.Token.Text}' doesn't fit in '{type.Name}'", CurrentTargetType);
	}
	
	private ResolvedArrayExpressionNode ResolveCodeUnits(LiteralExpressionNode node, IntegerType type)
	{
		var text = node.Token.Text;
		IEnumerable<BigInteger> units = type.Kind switch
		{
			PrimitiveTypeKind.UInt8 => Encoding.UTF8.GetBytes(text).Select(static unit => (BigInteger)unit),
			PrimitiveTypeKind.UInt16 => text.Select(static unit => (BigInteger)unit),
			_ => text.EnumerateRunes().Select(static rune => (BigInteger)rune.Value)
		};
		
		ImmutableArray<IResolvedExpressionNode> values =
			[..units.Select(unit => new ResolvedLiteralExpressionNode(type, unit, node))];
		
		return new ResolvedArrayExpressionNode(_typePool.GetArrayType(type, values.Length), values, node);
	}
	
	private (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span)
	{
		// TODO Check suffixes
		
		if (Scanner.TryParseInteger(ConsumeNegation(span), out var untypedValue))
			return (NativeSymbols.UntypedInteger, untypedValue);
		
		return (null, null);
	}
	
	private (TypeSymbol? Type, object? Value) ParseFloat(ReadOnlySpan<char> span) =>
		(NativeSymbols.UntypedFloat, ConsumeNegation(span).Replace("_", ""));
	
	private string ConsumeNegation(ReadOnlySpan<char> span)
	{
		if (!_unaryOpJobs.TryPeek(out var job) || job is not { Op: TokenType.OpMinus, Consumed: false })
			return span.ToString();
		
		job.Consumed = true;
		return $"-{span}";
	}
	
	private (TypeSymbol type, uint value) ParseChar(ReadOnlySpan<char> span)
	{
		Rune.DecodeFromUtf16(span, out var rune, out _);
		return (NativeSymbols.Char, (uint)rune.Value);
	}
	
	private (TypeSymbol type, object? value) ParseNull() =>
		(NativeSymbols.UntypedNull, null);
	
	private (TypeSymbol type, string value) ParseString(ReadOnlySpan<char> span) =>
		(NativeSymbols.UntypedString, new string(span));
	
	private IResolvedExpressionNode MaterializeWithPeer(IResolvedExpressionNode operand, TypeSymbol peerType)
	{
		if (peerType is UntypedType)
			return operand;
		
		var materialized = MaterializeExpression(operand, peerType);
		return materialized.Type is UntypedType
			? operand
			: materialized;
	}
	
	private IResolvedExpressionNode MaterializeAsDefault(IResolvedExpressionNode node)
	{
		if (node is ResolvedCaseNameExpressionNode caseName)
			return GetFallback(caseName);
		
		switch (node.Type)
		{
			case UntypedIntegerType when node is ResolvedLiteralExpressionNode literal:
			{
				var targetType = FindDefaultIntegerType(literal);
				return targetType is not null
					? MaterializeInteger(literal, targetType)
					: Error(node.Syntax, "Integer literal too large to fit any type", targetType);
			}
			
			case UntypedIntegerType:
				return MaterializeExpression(node, NativeSymbols.Int32);
			
			case UntypedFloatType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeFloat(literal, NativeSymbols.Float64);
			
			case UntypedNullType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeNull(literal, NativeSymbols.VoidPtr);
			
			case UntypedStringType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeStr(literal);
			
			case InterpolatedStringType when node is ResolvedInterpolatedStringExpressionNode interpolation:
				return MaterializeAsDefault(FoldInterpolation(interpolation));
			
			case FunctionGroupType when node is ResolvedFunctionGroupExpressionNode group:
				return MaterializeFunctionAsDefault(group);
			
			default:
				return node;
		}
	}
	
	private IntegerType? FindDefaultIntegerType(ResolvedLiteralExpressionNode literal) => NativeSymbols.IntegerTypes
		.Where(type => IntegerValueIn(literal, type) is not null)
		.Select(static type => (Type: type,
			Cost: UntypedIntegerType.Instance.MaterializationCost(type, MaterializationMode.Default)))
		.Where(static candidate => candidate.Cost != int.MaxValue)
		.OrderBy(static candidate => candidate.Cost)
		.FirstOrDefault().Type;
	
	private ResolvedLiteralExpressionNode MaterializeLiteral(IExpressionNode syntax, IntegerType type,
		BigInteger value) => new(type, ConvertInteger(value, type), syntax);
	
	private static object? ConvertInteger(BigInteger value, IntegerType type) => type.Kind switch
	{
		PrimitiveTypeKind.Int8 => (sbyte)value,
		PrimitiveTypeKind.Int16 => (short)value,
		PrimitiveTypeKind.Int32 => (int)value,
		PrimitiveTypeKind.Int64 => (long)value,
		PrimitiveTypeKind.Int128 => (Int128)value,
		PrimitiveTypeKind.IntSize => value,
		PrimitiveTypeKind.UInt8 => (byte)value,
		PrimitiveTypeKind.UInt16 => (ushort)value,
		PrimitiveTypeKind.UInt32 => (uint)value,
		PrimitiveTypeKind.UInt64 => (ulong)value,
		PrimitiveTypeKind.UInt128 => (UInt128)value,
		PrimitiveTypeKind.UIntSize => value,
		PrimitiveTypeKind.Char => (uint)value,
		_ => throw new InvalidOperationException()
	};
	
	private bool FitsInType(BigInteger value, IntegerType type) => type.Kind switch
	{
		PrimitiveTypeKind.Int8 => value >= sbyte.MinValue && value <= sbyte.MaxValue,
		PrimitiveTypeKind.Int16 => value >= short.MinValue && value <= short.MaxValue,
		PrimitiveTypeKind.Int32 => value >= int.MinValue && value <= int.MaxValue,
		PrimitiveTypeKind.Int64 => value >= long.MinValue && value <= long.MaxValue,
		PrimitiveTypeKind.Int128 => value >= Int128.MinValue && value <= Int128.MaxValue,
		PrimitiveTypeKind.IntSize => value >= _isizeMinValue && value <= _isizeMaxValue,
		PrimitiveTypeKind.UInt8 => value >= byte.MinValue && value <= byte.MaxValue,
		PrimitiveTypeKind.UInt16 => value >= ushort.MinValue && value <= ushort.MaxValue,
		PrimitiveTypeKind.UInt32 => value >= uint.MinValue && value <= uint.MaxValue,
		PrimitiveTypeKind.UInt64 => value >= ulong.MinValue && value <= ulong.MaxValue,
		PrimitiveTypeKind.UInt128 => value >= UInt128.MinValue && value <= UInt128.MaxValue,
		PrimitiveTypeKind.UIntSize => value >= _usizeMinValue && value <= _usizeMaxValue,
		PrimitiveTypeKind.Char => value >= uint.MinValue && value <= uint.MaxValue,
		_ => false
	};
	
	private IResolvedExpressionNode MaterializeExpression(IResolvedExpressionNode node, TypeSymbol target)
	{
		RecordMaterialization(node, target);
		if (node is ResolvedCaseNameExpressionNode caseName)
			return ResolveCaseName(caseName, target);
		
		if (node is ResolvedFunctionGroupExpressionNode group)
			return MaterializeFunction(group, target);
		
		if (node.Type is NeverType)
			return new ResolvedConversionExpressionNode(node, new NeverConversion(target), node.Syntax);
		
		if (node is ResolvedInterpolatedStringExpressionNode interpolation)
			return target switch
			{
				FStrType fstr => CreateTemplate(interpolation, fstr),
				StringType => MaterializeExpression(FoldInterpolation(interpolation), target),
				_ => node
			};
		
		if (node is not ResolvedLiteralExpressionNode literal)
			return node;
		
		return node.Type switch
		{
			UntypedIntegerType when target is IntegerType t => MaterializeInteger(literal, t),
			UntypedIntegerType when target is FloatType t => MaterializeIntegerAsFloat(literal, t),
			UntypedFloatType when target is FloatType t => MaterializeFloat(literal, t),
			UntypedNullType when target is PointerType t => MaterializeNull(literal, t),
			UntypedStringType when target is StringType t => t == NativeSymbols.CStr
				? MaterializeCStr(literal)
				: MaterializeStr(literal),
			UntypedStringType when target is FStrType t =>
				new ResolvedFStrExpressionNode(t, [(string)literal.Value!], [], null, literal.Syntax),
			_ => literal
		};
	}
	
	private ResolvedLiteralExpressionNode MaterializeInteger(ResolvedLiteralExpressionNode node, IntegerType target)
	{
		if (node.Type is not UntypedIntegerType)
			return node;
		
		if (IntegerValueIn(node, target) is { } fitted)
			return MaterializeLiteral(node.Syntax, target, fitted);
		
		var value = (BigInteger)node.Value!;
		var fallback = SmallestFittingType(value);
		return fallback is not null
			? MaterializeLiteral(node.Syntax, fallback, value)
			: node; // TODO Diagnostic: Too large for any integer type
	}
	
	private ResolvedLiteralExpressionNode MaterializeIntegerAsFloat(ResolvedLiteralExpressionNode node,
		FloatType target)
	{
		var value = DefaultIntegerValue(node);
		return IsExactInFloat(value, target)
			? new ResolvedLiteralExpressionNode(target, (double)value, node.Syntax)
			: MaterializeInteger(node, NativeSymbols.Int32);
	}
	
	private IResolvedExpressionNode MaterializeFloat(ResolvedLiteralExpressionNode node, FloatType target)
	{
		var value = node.Value!;
		var type = FitsInFloat(value, target) ? target : NativeSymbols.Float64;
		return FitsInFloat(value, type)
			? new ResolvedLiteralExpressionNode(type, ParseFloatValue(value, type), node.Syntax)
			: Error(node.Syntax, "Float literal too large to fit any type", target);
	}
	
	private static double ParseFloatValue(object value, FloatType type) => value switch
	{
		double number => type == NativeSymbols.Float32 ? (float)number : number,
		_ when type == NativeSymbols.Float32 => float.Parse((string)value, CultureInfo.InvariantCulture),
		_ => double.Parse((string)value, CultureInfo.InvariantCulture)
	};
	
	private static bool FitsInFloat(object value, FloatType type) =>
		double.IsFinite(ParseFloatValue(value, type)) || value is double number && !double.IsFinite(number);
	
	private static bool IsExactInFloat(BigInteger value, FloatType type)
	{
		var (significandBits, maxBitLength) = type == NativeSymbols.Float32 ? (24, 128) : (53, 1024);
		var magnitude = BigInteger.Abs(value);
		if (magnitude.IsZero)
			return true;
		
		var significand = magnitude >> (int)BigInteger.TrailingZeroCount(magnitude);
		return significand.GetBitLength() <= significandBits && magnitude.GetBitLength() <= maxBitLength;
	}
	
	private ResolvedLiteralExpressionNode MaterializeNull(ResolvedLiteralExpressionNode node, PointerType target)
	{
		if (node.Type is not UntypedNullType)
			return node;
		
		return new ResolvedLiteralExpressionNode(target, null, node.Syntax);
	}
	
	private ResolvedLiteralExpressionNode MaterializeStr(ResolvedLiteralExpressionNode node)
	{
		if (node.Type is not UntypedStringType)
			return node;
		
		var text = (string)node.Value!;
		var byteCount = Encoding.UTF8.GetByteCount(text);
		var bytes = new byte[byteCount];
		Encoding.UTF8.GetBytes(text, bytes);
		return new ResolvedLiteralExpressionNode(NativeSymbols.Str, new StrValue((ulong)byteCount, bytes), node.Syntax);
	}
	
	private ResolvedLiteralExpressionNode MaterializeCStr(ResolvedLiteralExpressionNode node)
	{
		if (node.Type is not UntypedStringType)
			return node;
		
		var text = (string)node.Value!;
		var byteCount = Encoding.UTF8.GetByteCount(text);
		var bytes = new byte[byteCount + 1];
		Encoding.UTF8.GetBytes(text, bytes);
		return new ResolvedLiteralExpressionNode(NativeSymbols.CStr, bytes, node.Syntax);
	}
	
	private IntegerType? SmallestFittingType(BigInteger value)
	{
		if (value >= int.MinValue && value <= int.MaxValue)
			return NativeSymbols.Int32;
		
		if (value >= long.MinValue && value <= long.MaxValue)
			return NativeSymbols.Int64;
		
		if (value >= Int128.MinValue && value <= Int128.MaxValue)
			return NativeSymbols.Int128;
		
		return null;
	}
	
	private static bool IsMixedSignComparison(TokenType op, TypeSymbol left, TypeSymbol right) =>
		op is TokenType.OpLess or TokenType.OpLessEqual or TokenType.OpGreater or TokenType.OpGreaterEqual
			or TokenType.OpEqualEqual or TokenType.OpBangEqual &&
		left is IntegerType { IsSigned: var leftSigned } && right is IntegerType { IsSigned: var rightSigned } &&
		leftSigned != rightSigned;
	
	private static bool IsShiftOrRotate(TokenType op) => op is TokenType.OpLessLess or TokenType.OpLessLessEqual
		or TokenType.OpGreaterGreater or TokenType.OpGreaterGreaterEqual or TokenType.OpLessLessLess
		or TokenType.OpLessLessLessEqual or TokenType.OpGreaterGreaterGreater or TokenType.OpGreaterGreaterGreaterEqual;
	
	private Diagnostic? FindShiftRangeError(BinaryOpExpressionNode node, TypeSymbol valueType,
		IResolvedExpressionNode amount)
	{
		var isShift = node.Op.Type is TokenType.OpLessLess or TokenType.OpLessLessEqual or TokenType.OpGreaterGreater
			or TokenType.OpGreaterGreaterEqual;
		
		if (!isShift || _evaluator.Evaluate(amount) is not IntegerConstant { Value: var value })
			return null;
		
		var bits = CountBits(valueType);
		if (value >= 0 && value < bits)
			return null;
		
		return new Diagnostic(DiagnosticSeverity.Error, node.Right.SourceLocation,
			$"Shift amount {value} is out of range for '{valueType.Name}'")
		{
			Hints = [$"'{valueType.Name}' has {bits} bits"]
		};
	}
	
	private (IntegerType Signed, IntegerType Unsigned)? FindLossyMixedSign(TypeSymbol left, TypeSymbol right)
	{
		if (left is not IntegerType a || right is not IntegerType b || a.IsSigned == b.IsSigned)
			return null;
		
		var (signedType, unsignedType) = a.IsSigned ? (a, b) : (b, a);
		return _conversionTable.FindImplicit(unsignedType, signedType) is null ? (signedType, unsignedType) : null;
	}
	
	private IntegerType? FindMixedSignType(IntegerType signedType, IntegerType unsignedType)
	{
		if (signedType.Kind == PrimitiveTypeKind.IntSize ||
		    unsignedType.Kind is PrimitiveTypeKind.UIntSize or PrimitiveTypeKind.Char)
			return null;
		
		var bits = Math.Max(CountBits(signedType), CountBits(unsignedType) + 1);
		return NativeSymbols.PureIntegerTypes.FirstOrDefault(type =>
			type.IsSigned && type.Kind != PrimitiveTypeKind.IntSize && CountBits(type) >= bits);
	}
	
	private TypeSymbol? UnifyTypes(IList<IResolvedExpressionNode> values)
	{
		if (values.Count == 0)
			return NativeSymbols.Never;
		
		// Materialize untyped integer values left to right
		for (var i = 0; i < values.Count - 1; i++)
		{
			values[i] = MaterializeWithPeer(values[i], values[i + 1].Type);
			values[i + 1] = MaterializeWithPeer(values[i + 1], values[i].Type);
		}
		
		// Propagate materialized values back right to left
		for (var i = values.Count - 1; i > 0; i--)
		{
			values[i] = MaterializeWithPeer(values[i], values[i - 1].Type);
			values[i - 1] = MaterializeWithPeer(values[i - 1], values[i].Type);
		}
		
		for (var i = 0; i < values.Count; i++)
			values[i] = MaterializeAsDefault(values[i]);
		
		TypeSymbol? commonType = values[0].Type;
		for (var i = 1; i < values.Count && commonType is not null; i++)
			commonType = FindCommonType(commonType, values[i].Type);
		
		return commonType;
	}
	
	private TypeSymbol? FindCommonType(TypeSymbol a, TypeSymbol b)
	{
		if (a == b || b is NeverType)
			return a;
		
		if (a is NeverType)
			return b;
		
		if (AnyInvalid(a, b))
			return NativeSymbols.Invalid;
		
		if (a is BorrowType { Target: var aTarget } && aTarget == b)
			return b;
		
		if (b is BorrowType { Target: var bTarget } && bTarget == a)
			return a;
		
		if (_conversionTable.FindImplicit(a, b) is not null)
			return b;
		
		if (_conversionTable.FindImplicit(b, a) is not null)
			return a;
		
		return null;
	}
	
	[return: NotNullIfNotNull(nameof(node))]
	private IResolvedExpressionNode? CoerceToType(IResolvedExpressionNode? node, TypeSymbol target)
	{
		if (node?.Type is NeverType)
			return MaterializeExpression(node, target);
		
		if (node?.Type is UntypedType)
			node = MaterializeExpression(node,
				target is BorrowType { IsMutable: false } borrow ? borrow.Target : target);
		
		return ApplyImplicitConversion(node, target);
	}
	
	private static IResolvedExpressionNode Decay(IResolvedExpressionNode node) =>
		node is not ResolvedBorrowExpressionNode { IsImplicit: false } && node.Type is BorrowType borrow
			? new ResolvedUnaryOpExpressionNode(node, new NativeImpl(TokenType.OpStar, borrow.Target), node.Syntax)
			: node;
	
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
	
	private ResolvedInvalidStatementNode Error(IStatementNode node, string message, SourceLocation sourceLocation)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, sourceLocation, message));
		return new(node);
	}
	
	private ResolvedInvalidStatementNode Error(IStatementNode node, string message, ISyntaxNode? source = null) =>
		Error(node, message, source?.SourceLocation ?? node.SourceLocation);
	
	private ResolvedInvalidDeclarationNode Error(IDeclarationNode node, string message, SourceLocation sourceLocation)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, sourceLocation, message));
		return new(node);
	}
	
	private ResolvedInvalidDeclarationNode Error(IDeclarationNode node, string message, ISyntaxNode? source = null) =>
		Error(node, message, source?.SourceLocation ?? node.SourceLocation);
	
	private ResolvedInvalidDeclarationNode Error(IDeclarationNode node, Diagnostic diagnostic)
	{
		Diagnostics.Add(diagnostic);
		return new(node);
	}
	
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
	
	private void TrackGenericReference(Symbol symbol)
	{
		if (CurrentResolutionContext.ContainingFunction is { Symbol.TypeParameters.IsEmpty: false } &&
		    _signatures.IsLocal(symbol) && _genericReferences.Add(symbol))
			Journal(() => _genericReferences.Remove(symbol));
	}
	
	private void TrackFunctionUse(FunctionInfo info, IExpressionNode syntax)
	{
		if ((!_signatures.IsLocal(info.Symbol) || info.File?.Module != CurrentResolutionContext.File.Module) &&
		    _importedFunctions.TryAdd(info.Symbol, info))
			Journal(() => _importedFunctions.Remove(info.Symbol));
		
		if (info.Symbol.TypeParameters.IsEmpty)
			TrackGenericReference(info.Symbol);
		
		if (CurrentResolutionContext.ContainingFunction is not { Symbol: { TypeParameters.IsEmpty: false } caller } ||
		    info.TypeArguments.IsDefaultOrEmpty)
			return;
		
		for (var i = 0; i < info.TypeArguments.Length; i++)
		{
			var argument = info.TypeArguments[i];
			foreach (var parameter in caller.TypeParameters.Where(parameter => Mentions(argument, parameter)))
			{
				_instantiations.Add(new((caller, parameter), (info.Symbol, info.Symbol.TypeParameters[i]),
					argument != parameter, syntax.SourceLocation));
				
				Journal(() => _instantiations.RemoveAt(_instantiations.Count - 1));
			}
		}
	}
	
	private void Journal(Action undo) => _journal?.Add(undo);
	
	private Checkpoint OpenCheckpoint()
	{
		_journal ??= [];
		_openCheckpoints++;
		var redirects = new[] { Diagnostics, CurrentResolutionContext.Diagnostics }
			.Distinct()
			.Select(static list =>
			{
				var captured = new List<Diagnostic>();
				return new Redirection(list, list.Redirect(captured), captured);
			})
			.ToList();
		
		return new(_journal.Count, redirects);
	}
	
	private void Commit(Checkpoint checkpoint)
	{
		Close(checkpoint);
		foreach (var redirection in checkpoint.Redirections)
			redirection.List.AddRange(redirection.Captured);
	}
	
	private void Rollback(Checkpoint checkpoint)
	{
		var journal = _journal!;
		for (var i = journal.Count - 1; i >= checkpoint.JournalLength; i--)
			journal[i]();
		
		journal.RemoveRange(checkpoint.JournalLength, journal.Count - checkpoint.JournalLength);
		Close(checkpoint);
	}
	
	private void Close(Checkpoint checkpoint)
	{
		foreach (var redirection in Enumerable.Reverse(checkpoint.Redirections))
			redirection.List.Redirect(redirection.Previous);
		
		if (--_openCheckpoints == 0)
			_journal = null;
	}
	
	private static bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) => type switch
	{
		TypeParameterSymbol other => other == parameter,
		NamedTypeSymbol named => named.TypeArguments.Any(argument => Mentions(argument, parameter)),
		PointerType pointer => Mentions(pointer.BaseType, parameter),
		BorrowType borrow => Mentions(borrow.Target, parameter),
		ArrayType array => Mentions(array.ElementType, parameter),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Any(part => Mentions(part, parameter)),
		_ => false
	};
	
	public void ReportInfiniteInstantiations()
	{
		var edges = _instantiations.ToLookup(static instantiation => instantiation.From);
		var components = new Dictionary<(FunctionSymbol, TypeParameterSymbol), int>();
		var indices = new Dictionary<(FunctionSymbol, TypeParameterSymbol), (int Index, int Low)>();
		var stack = new Stack<(FunctionSymbol, TypeParameterSymbol)>();
		var count = 0;
		
		foreach (var node in _instantiations.Select(static instantiation => instantiation.From))
		{
			if (!indices.ContainsKey(node))
				Connect(node);
		}
		
		var locations = _instantiations
			.Where(instantiation => instantiation.IsExpansive &&
			                        components.GetValueOrDefault(instantiation.From, -1) ==
			                        components.GetValueOrDefault(instantiation.To, -2))
			.DistinctBy(static instantiation => instantiation.Location);
		
		foreach (var instantiation in locations)
			Diagnostics.Add(new(DiagnosticSeverity.Error, instantiation.Location,
				$"Instantiating '{instantiation.To.Function.Name}' never ends"));
		
		return;
		
		void Connect((FunctionSymbol, TypeParameterSymbol) node)
		{
			var index = indices.Count;
			indices[node] = (index, index);
			stack.Push(node);
			foreach (var next in edges[node].Select(static instantiation => instantiation.To))
			{
				if (!indices.ContainsKey(next))
				{
					Connect(next);
					indices[node] = (index, Math.Min(indices[node].Low, indices[next].Low));
				}
				else if (stack.Contains(next))
				{
					indices[node] = (index, Math.Min(indices[node].Low, indices[next].Index));
				}
			}
			
			if (indices[node].Low != index)
				return;
			
			var component = count++;
			(FunctionSymbol, TypeParameterSymbol) member;
			do
			{
				member = stack.Pop();
				components[member] = component;
			} while (member != node);
		}
	}
	
	private ResolutionSet ResolveCallable(IEnumerable<ICallable> candidates,
		IReadOnlyList<IResolvedExpressionNode> args, MaterializationMode mode, TypeSymbol? target = null,
		bool ignoreModes = false)
	{
		var options = new List<CallableResolution>();
		
		foreach (var candidate in candidates)
		{
			var parameterCount = candidate.ParameterTypes.Length;
			if (candidate.IsVariadic ? args.Count < parameterCount : args.Count != parameterCount)
				continue;
			
			if (!ignoreModes && args.Skip(parameterCount).Any(static arg => arg is ResolvedMutArgumentExpressionNode))
				continue;
			
			var conversionCost = 0;
			var materializationCost = 0;
			var argumentConversions = new Conversion?[args.Count];
			var valid = true;
			
			for (var i = 0; i < parameterCount; i++)
			{
				var (cost, conversion) = MatchArg(args[i], candidate.ParameterTypes[i], candidate.GetMode(i), mode,
					ignoreModes);
				
				if (cost == int.MaxValue || candidate is ConversionCallable && conversion is not null)
				{
					valid = false;
					break;
				}
				
				if (args[i].Type is UntypedType)
					materializationCost += cost;
				else
					conversionCost += cost;
				
				argumentConversions[i] = conversion;
			}
			
			if (!valid)
				continue;
			
			var resultRank = 0;
			var resultCost = 0;
			Conversion? resultConversion = null;
			
			if (target is not null && candidate.ReturnType != target)
			{
				var conversion = _conversionTable.FindImplicit(candidate.ReturnType, target) ??
				                 (candidate.ReturnType is BorrowType
					                 ? null
					                 : FindUserConversion(candidate.ReturnType, null, ParameterMode.ReadOnly, target));
				
				if (conversion is null && candidate.ReturnType != NativeSymbols.Void &&
				    candidate.ReturnType is not BorrowType && target is not BorrowType)
					continue;
				
				// Exact returns beat converted returns, so set to a higher cost
				resultRank = 1;
				resultCost = conversion?.Cost ?? 0;
				resultConversion = conversion;
			}
			
			var totalCost = new CallableCost(resultRank, conversionCost, materializationCost, resultCost,
				candidate.IsGeneric ? 1 : 0);
			
			options.Add(new(candidate, totalCost, resultConversion, argumentConversions));
		}
		
		if (options.Count == 0)
			return ResolutionSet.None;
		
		var best = options.Min(static v => v.Cost);
		var winners = options.Where(v => v.Cost == best).ToList();
		return winners.Count switch
		{
			0 => ResolutionSet.None,
			1 => ResolutionSet.Single(winners[0]),
			_ => ResolutionSet.Ambiguous(winners)
		};
	}
	
	private List<IResolvedExpressionNode> ApplyArgumentResolution(IReadOnlyList<IResolvedExpressionNode> args,
		CallableResolution resolution)
	{
		var result = new List<IResolvedExpressionNode>(args.Count);
		
		for (var i = 0; i < args.Count; i++)
		{
			var arg = args[i];
			if (i >= resolution.Callable.ParameterTypes.Length)
			{
				result.Add(PromoteVariadicArgument(arg));
				continue;
			}
			
			var target = resolution.Callable.ParameterTypes[i];
			if (resolution.ArgumentConversions[i] is FunctionConversion userConversion)
			{
				result.Add(ApplyArgumentConversion(arg, userConversion, target));
				continue;
			}
			
			if (arg is ResolvedMutArgumentExpressionNode argument)
			{
				result.Add(ApplyMutArgument(argument, target));
				continue;
			}
			
			if (arg is ResolvedCaseNameExpressionNode caseName)
				arg = ResolveCaseName(caseName,
					target is BorrowType { IsMutable: false, Target: var readTarget } ? readTarget : target);
			
			if (IsDynTarget(target) && arg.Type != target && ConvertToDyn(arg, target) is { } erased)
			{
				result.Add(erased);
				continue;
			}
			
			var borrowed = target is BorrowType { IsMutable: false } borrow && arg.Type is not BorrowType
				? borrow
				: null;
			
			if (arg.Type is UntypedType)
				arg = MaterializeExpression(arg, borrowed?.Target ?? target);
			
			if (arg.Type is UntypedType)
				arg = MaterializeAsDefault(arg);
			
			if (borrowed is not null)
				arg = new ResolvedBorrowExpressionNode(arg, borrowed, true, arg.Syntax);
			else if (arg.Type is BorrowType && target is not BorrowType)
				arg = Decay(arg);
			
			if (target is FStrType fstr && arg.Type == NativeSymbols.Str)
				arg = CreateTextTemplate(arg, fstr);
			
			if (resolution.ArgumentConversions[i] is { } conversion)
				arg = new ResolvedConversionExpressionNode(arg, conversion, arg.Syntax);
			
			result.Add(arg);
		}
		
		return result;
	}
	
	private IResolvedExpressionNode ApplyMutArgument(ResolvedMutArgumentExpressionNode argument, TypeSymbol target)
	{
		var declared = target switch
		{
			PointerType { BaseType: var baseType } => baseType,
			BorrowType { Target: var borrowed } => borrowed,
			_ => argument.Place.Type
		};
		
		if (FindOpenPlace(argument.Place) is { } open)
		{
			RecordUse(open, declared, LocalUseKind.Read);
			RecordUse(open, declared, LocalUseKind.Write);
		}
		
		var place = argument.Place.Type == declared ? argument.Place : Decay(argument.Place);
		if (declared is DynType && place.Type != declared && !IsInvalid(place))
			return Erase(target is BorrowType
					? new ResolvedBorrowExpressionNode(place, _typePool.GetBorrowType(place.Type, true), false,
						argument.Syntax)
					: new ResolvedMutArgumentExpressionNode(place, _typePool.GetPointerType(place.Type),
						argument.Syntax),
				target);
		
		if (target is BorrowType borrow)
			return new ResolvedBorrowExpressionNode(place, borrow, false, argument.Syntax);
		
		return place == argument.Place
			? argument
			: new ResolvedMutArgumentExpressionNode(place, _typePool.GetPointerType(place.Type), argument.Syntax);
	}
	
	private IResolvedExpressionNode PromoteVariadicArgument(IResolvedExpressionNode arg)
	{
		if (arg.Type is UntypedStringType or InterpolatedStringType)
			arg = MaterializeExpression(arg, NativeSymbols.CStr);
		
		if (arg.Type is UntypedType)
			arg = MaterializeAsDefault(arg);
		
		arg = Decay(arg);
		switch (arg.Type)
		{
			case NeverType:
				return arg;
			
			case PrimitiveType { Kind: PrimitiveTypeKind.Bool }:
				return new ResolvedConversionExpressionNode(arg, _boolPromotion, arg.Syntax);
			
			case IntegerType type when CountBits(type) < 32:
				return new ResolvedConversionExpressionNode(arg,
					_conversionTable.FindExplicit(type, NativeSymbols.Int32)!,
					arg.Syntax);
			
			case IntegerType type when CountBits(type) <= 64:
				return arg;
			
			case FloatType { Kind: PrimitiveTypeKind.Float32 } type:
				return new ResolvedConversionExpressionNode(arg,
					_conversionTable.FindImplicit(type, NativeSymbols.Float64)!,
					arg.Syntax);
			
			case FloatType { Kind: PrimitiveTypeKind.Float64 }:
				return arg;
			
			case PointerType:
			case PrimitiveType { Kind: PrimitiveTypeKind.CStr }:
				return arg;
			
			default:
				return Error(arg.Syntax, $"Cannot pass '{arg.Type.Name}' values to variadic functions",
					NativeSymbols.Invalid);
		}
	}
	
	private uint CountBits(TypeSymbol type) => _typePool.SizeTable.GetSize(type).CountBits(_pointerBitSize);
	
	private IResolvedExpressionNode ApplyResultResolution(IResolvedExpressionNode node,
		CallableResolution resolution) => resolution.ResultConversion switch
	{
		FunctionConversion userConversion => ApplyUserConversion(node, userConversion),
		{ } conversion => new ResolvedConversionExpressionNode(node, conversion, node.Syntax),
		null => node
	};
	
	private IResolvedExpressionNode ApplyArgumentConversion(IResolvedExpressionNode arg, FunctionConversion conversion,
		TypeSymbol target)
	{
		var converted = ApplyUserConversion(arg, conversion);
		return arg is ResolvedMutArgumentExpressionNode argument && !IsInvalid(converted)
			? ApplyMutArgument(new ResolvedMutArgumentExpressionNode(converted,
				_typePool.GetPointerType(converted.Type), argument.Syntax), target)
			: converted;
	}
	
	private FunctionConversion? FindUserConversion(IResolvedExpressionNode source, TypeSymbol target,
		bool isExplicit = false)
	{
		var (value, use) = source switch
		{
			ResolvedMutArgumentExpressionNode argument => (Decay(argument.Place), ParameterMode.Mut),
			ResolvedOwnExpressionNode owned => (Decay(owned.Value), ParameterMode.Own),
			_ => (Decay(source), ParameterMode.ReadOnly)
		};
		
		return IsInvalid(value)
			? null
			: FindUserConversion(value.Type, value as ResolvedLiteralExpressionNode, use, target, isExplicit);
	}
	
	private FunctionConversion? FindUserConversion(TypeSymbol type, ResolvedLiteralExpressionNode? literal,
		ParameterMode use, TypeSymbol target, bool isExplicit = false)
	{
		if (IsInvalid(type) || IsInvalid(target))
			return null;
		
		List<(FunctionInfo Info, int Cost)> candidates =
		[
			..FindOutwardConversions(type, use, target, isExplicit).Select(static info => (Info: info, Cost: 0)),
			..use == ParameterMode.Mut
				? []
				: GetInwardConversions(target)
					.Select(info => InferConversion(info, type, literal))
					.OfType<FunctionInfo>()
					.Select(info => (Info: info,
						Cost: MatchConversionSource(type, literal, info.Signature.ParameterTypes[0])))
					.Where(static candidate => candidate.Cost < int.MaxValue)
		];
		
		if (candidates.Count == 0)
			return null;
		
		var cheapest = candidates.Min(static candidate => candidate.Cost);
		var best = candidates.Where(candidate => candidate.Cost == cheapest).ToList();
		return new FunctionConversion(best[0].Info, isExplicit ? ConversionKind.Explicit : ConversionKind.Implicit,
			UserConversionCost + cheapest)
		{
			IsAmbiguous = best.Count > 1
		};
	}
	
	private int MatchConversionSource(TypeSymbol type, ResolvedLiteralExpressionNode? literal, TypeSymbol parameter)
	{
		if (type == parameter)
			return 0;
		
		return type is UntypedType untyped && (literal is null || LiteralFits(literal, parameter))
			? untyped.MaterializationCost(parameter, MaterializationMode.Overload)
			: int.MaxValue;
	}
	
	private List<FunctionInfo> FindOutwardConversions(TypeSymbol type, ParameterMode use, TypeSymbol target,
		bool isExplicit)
	{
		if (type is UntypedType)
			return [];
		
		var matching = GetMethods(type, "as")
			.Concat(isExplicit ? GetMethods(type, "new") : [])
			.Where(method => method.Function is { IsConversion: true, Kind: FunctionKind.Method } &&
			                 CanAccess(type, method.Function))
			.Select(method => GetFunctionInfo(method, type))
			.Where(info => info.Signature.ParameterTypes.Length == 1 &&
			               Produces(info.Signature.ReturnType, target, use == ParameterMode.Mut))
			.ToList();
		
		ParameterMode[] modes = use switch
		{
			ParameterMode.Mut => [ParameterMode.Mut],
			ParameterMode.Own => [ParameterMode.Own, ParameterMode.ReadOnly],
			_ => [ParameterMode.ReadOnly, ParameterMode.Own]
		};
		
		return modes
			.Select(mode => matching.Where(info => info.Signature.GetMode(0) == mode).ToList())
			.FirstOrDefault(static infos => infos.Count > 0) ?? [];
	}
	
	private static bool Produces(TypeSymbol result, TypeSymbol target, bool isMutable) => isMutable
		? result is BorrowType { IsMutable: true } borrow && borrow.Target == target
		: result == target || result is BorrowType { IsMutable: false } view && view.Target == target;
	
	private IEnumerable<FunctionInfo> GetInwardConversions(TypeSymbol target) =>
		target is BorrowType or UntypedType or NamedTypeSymbol { IsGenericDefinition: true }
			? []
			: GetMethods(target, "as")
				.Where(method => method.Function is { IsConversion: true, Kind: FunctionKind.Free } &&
				                 CanAccess(target, method.Function))
				.Select(method => GetFunctionInfo(method, target))
				.Where(info => info.Signature.ParameterTypes.Length == 1 && info.Signature.ReturnType == target);
	
	private static ResolutionSet PreferConstruction(ResolutionSet resolutionSet)
	{
		if (!resolutionSet.IsAmbiguous)
			return resolutionSet;
		
		var winners = Enumerable.Range(0, resolutionSet.Count).Select(i => resolutionSet[i]).ToList();
		var kept = winners
			.Where(static winner => winner.Callable is not ConversionCallable { Info.Symbol.Kind: FunctionKind.Free })
			.ToList();
		
		return kept is [{ Callable: not ConversionCallable } only] ? ResolutionSet.Single(only) : resolutionSet;
	}
	
	private ConversionCallable[] FindConversionCallables(IReadOnlyList<IResolvedExpressionNode> args,
		TypeSymbol target)
	{
		if (args is not [var arg] || arg is ResolvedMutArgumentExpressionNode || IsInvalid(arg) || IsInvalid(target) ||
		    target is NamedTypeSymbol { IsGenericDefinition: true })
			return [];
		
		var (value, use) = arg is ResolvedOwnExpressionNode owned
			? (Decay(owned.Value), ParameterMode.Own)
			: (Decay(arg), ParameterMode.ReadOnly);
		
		return
		[
			..FindOutwardConversions(value.Type, use, target, true)
				.Concat(GetInwardConversions(target)
					.Select(info => InferConversion(info, value.Type, value as ResolvedLiteralExpressionNode))
					.OfType<FunctionInfo>())
				.Select(info => new ConversionCallable(info, target))
		];
	}
	
	private IResolvedExpressionNode ApplyUserConversion(IResolvedExpressionNode source, FunctionConversion conversion,
		IExpressionNode? syntax = null)
	{
		if (!conversion.IsAmbiguous)
			return CallConversion(conversion.Function, source, syntax ?? source.Syntax);
		
		var from = Decay(source is ResolvedMutArgumentExpressionNode argument ? argument.Place : source).Type;
		var to = conversion.To is BorrowType borrow ? borrow.Target : conversion.To;
		return Error(syntax ?? source.Syntax, $"Conversion from '{from.Name}' to '{to.Name}' is ambiguous", to);
	}
	
	private IResolvedExpressionNode CallConversion(FunctionInfo info, IResolvedExpressionNode source,
		IExpressionNode syntax)
	{
		TrackFunctionUse(info, syntax);
		if (info.Symbol.Kind == FunctionKind.Free)
		{
			var value = source.Type is UntypedType
				? MaterializeExpression(source, info.Signature.ParameterTypes[0])
				: source is ResolvedOwnExpressionNode
					? source
					: Decay(source);
			
			return new ResolvedFunctionCallExpressionNode(info, [value], syntax) { IsConversion = true };
		}
		
		var receiver = source switch
		{
			ResolvedMutArgumentExpressionNode argument => argument.Place,
			ResolvedOwnExpressionNode => source,
			_ => Decay(source)
		};
		
		return Decay(new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(receiver, info)], syntax)
		{
			IsConversion = true
		});
	}
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		ParameterMode parameterMode, MaterializationMode mode, bool ignoreModes)
	{
		var mutTarget = arg.Type is BorrowType ? null : GetMutTarget(target, parameterMode);
		if (parameterMode == ParameterMode.Mut || mutTarget is not null)
		{
			if (arg is ResolvedMutArgumentExpressionNode { Place: var place } && mutTarget is not null &&
			    FindOpenPlace(place) is not null)
				return (0, null);
			
			if (arg is ResolvedMutArgumentExpressionNode mutArgument && mutTarget is not null &&
			    IsMutPlaceOf(mutArgument.Place, mutTarget))
				return (mutArgument.Place.Type == mutTarget ? 0 : 1, null);
			
			if (arg is ResolvedMutArgumentExpressionNode && mutTarget is not null &&
			    FindUserConversion(arg, mutTarget) is { } mutConversion)
				return (mutConversion.Cost, mutConversion);
			
			return ignoreModes ? (0, null) : (int.MaxValue, null);
		}
		
		if (arg is not ResolvedMutArgumentExpressionNode argument)
			return MatchArg(arg, target, mode);
		
		return ignoreModes ? MatchArg(argument.Place, target, mode) : (int.MaxValue, null);
	}
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		MaterializationMode mode)
	{
		if (arg is ResolvedCaseNameExpressionNode caseName)
			return MatchCaseName(caseName, target, mode);
		
		if (arg.Type == target)
			return (0, null);
		
		if (IsDynTarget(target))
			return CanConvertToDyn(arg.Type is UntypedType ? GetDefaultType(arg) : arg.Type, target)
				? (1, null)
				: (int.MaxValue, null);
		
		if (target is BorrowType { IsMutable: false } borrow && arg.Type is not BorrowType)
		{
			var (cost, _) = MatchArg(arg, borrow.Target, mode);
			return arg.Type == borrow.Target || arg.Type is UntypedType && cost != int.MaxValue
				? (cost + 1, null)
				: (int.MaxValue, null);
		}
		
		if (target is not BorrowType && Decay(arg) is var decayedArg && decayedArg != arg)
		{
			var (cost, decayedConversion) = MatchArg(decayedArg, target, mode);
			return cost == int.MaxValue ? (cost, null) : (cost + 1, decayedConversion);
		}
		
		if (arg is ResolvedFunctionGroupExpressionNode group && target is FunctionType functionType)
			return FindFunction(group.Group, functionType) is { } function
				? ((function.Symbol.IsExternal == functionType.IsExternal ? 0 : 1) + (functionType.IsRef ? 1 : 0) +
				   (FunctionGroupType.Matches(function, functionType) ? 0 : 1), null)
				: (int.MaxValue, null);
		
		if (arg.Type is UntypedType u)
		{
			// Special case for literals: If target type cannot store the value, conversion is impossible
			if (arg is ResolvedLiteralExpressionNode literal && !LiteralFits(literal, target))
				return (int.MaxValue, null);
			
			var cost = u.MaterializationCost(target, mode);
			return cost == int.MaxValue && FindUserConversion(arg, target) is { } literalConversion
				? (literalConversion.Cost, literalConversion)
				: (cost, null);
		}
		
		if (target is FStrType && arg.Type == NativeSymbols.Str)
			return (1, null);
		
		if (_conversionTable.FindImplicit(arg.Type, target) is { } conversion)
			return (conversion.Cost, conversion);
		
		return FindUserConversion(arg, target) is { } userConversion
			? (userConversion.Cost, userConversion)
			: (int.MaxValue, null);
	}
	
	private (int Cost, Conversion? Conversion) MatchCaseName(ResolvedCaseNameExpressionNode caseName,
		TypeSymbol target, MaterializationMode mode)
	{
		var (read, borrowCost) = target is BorrowType { IsMutable: false } borrow ? (borrow.Target, 1) : (target, 0);
		if (read is EnumSymbol enumType && ChooseCase(enumType, caseName) != CaseChoice.Symbol)
			return (borrowCost + (IsViableCase(enumType, caseName) ? 0 : 1), null);
		
		return caseName.Symbol is null ? (int.MaxValue, null) : MatchArg(GetFallback(caseName), target, mode);
	}
	
	private bool LiteralFits(ResolvedLiteralExpressionNode literal, TypeSymbol target) => (literal.Type, target) switch
	{
		(UntypedIntegerType, IntegerType type) => IntegerValueIn(literal, type) is not null,
		(UntypedIntegerType, FloatType type) => IsExactInFloat(DefaultIntegerValue(literal), type),
		(UntypedFloatType, FloatType type) => FitsInFloat(literal.Value!, type),
		_ => true
	};
	
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
	
	private readonly record struct CallableCost
	(
		int ResultRank,
		int ConversionCost,
		int MaterializationCost,
		int ResultCost,
		int GenericRank
	) : IComparable<CallableCost>
	{
		public int CompareTo(CallableCost other)
		{
			var resultRankComparison = ResultRank.CompareTo(other.ResultRank);
			if (resultRankComparison != 0)
				return resultRankComparison;
			
			var conversionCostComparison = ConversionCost.CompareTo(other.ConversionCost);
			if (conversionCostComparison != 0)
				return conversionCostComparison;
			
			var materializationCostComparison = MaterializationCost.CompareTo(other.MaterializationCost);
			if (materializationCostComparison != 0)
				return materializationCostComparison;
			
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
	
	private readonly record struct Instantiation
	(
		(FunctionSymbol Function, TypeParameterSymbol Parameter) From,
		(FunctionSymbol Function, TypeParameterSymbol Parameter) To,
		bool IsExpansive,
		SourceLocation Location
	);
	
	private sealed class FunctionCallable(FunctionInfo info) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public bool IsGeneric { get; init; }
		public ImmutableArray<TypeSymbol> ParameterTypes => Info.Signature.ParameterTypes;
		public TypeSymbol ReturnType => Info.Signature.ReturnType;
		public bool IsVariadic => Info.Signature.IsVariadic;
		public ParameterMode GetMode(int index) => Info.Signature.GetMode(index);
		public string GetParameterName(int index) => Info.Symbol.Parameters[index].Name;
	}
	
	private sealed class FunctionTypeCallable(FunctionType type) : ICallable
	{
		public ImmutableArray<TypeSymbol> ParameterTypes => type.ParameterTypes;
		public TypeSymbol ReturnType => type.ReturnType;
		public ParameterMode GetMode(int index) => type.ParameterModes[index];
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
	
	private sealed class ReceiverCallable(FunctionInfo info, TypeSymbol type) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public bool IsGeneric { get; init; }
		
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } =
			info.Signature.ParameterTypes.Skip(1).ToImmutableArray(); // Skip implicit self
		
		public TypeSymbol ReturnType { get; } = type;
		public ParameterMode GetMode(int index) => Info.Signature.GetMode(index + 1);
		
		public string? GetParameterName(int index) =>
			Info.Symbol.Syntax is ConstructorConstraintNode ? null : Info.Symbol.Parameters[index + 1].Name;
	}
}

public interface ICallable
{
	ImmutableArray<TypeSymbol> ParameterTypes { get; }
	TypeSymbol ReturnType { get; }
	bool IsVariadic => false;
	bool IsGeneric => false;
	ParameterMode GetMode(int index) => ParameterMode.ReadOnly;
	string? GetParameterName(int index) => null;
}