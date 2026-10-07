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
	private readonly Stack<TypeSymbol?> _targetTypes = [];
	private readonly Stack<ResolutionContext> _resolutionContexts = [];
	private readonly HashSet<LocalVariableSymbol> _repeatedBindings = [];
	private readonly Dictionary<IResolvedExpressionNode, ImmutableArray<LocalVariableSymbol?>> _failedPatterns = [];
	private readonly ExtSignatureTypes _extSignatureTypes;
	private readonly TypeInference _inference;
	private readonly List<Instantiation> _instantiations = [];
	private IExpressionNode? storeTarget;
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	private TypeSymbol? CurrentTargetType => _targetTypes.TryPeek(out var result) ? result : null;
	
	public Resolver(SymbolTable symbolTable, SignatureCollector signatures, TypePool typePool, uint pointerBitSize)
	{
		_typePool = typePool;
		_extSignatureTypes = new(typePool);
		_conversionTable = typePool.ConversionTable;
		_inference = new(_conversionTable);
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
		var result = VisitNode(initializer, type);
		_resolutionContexts.Pop();
		return result;
	}
	
	public Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context)
	{
		_resolutionContexts.Push(context);
		var result = VisitNode(expression, null);
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
		_targetTypes.Push(targetType);
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
			_targetTypes.Pop();
		}
	}
	
	private IResolvedExpressionNode VisitDiscarded(IExpressionNode node)
	{
		_targetTypes.Push(null);
		var result = ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(node);
		_targetTypes.Pop();
		return result;
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
		
		_resolutionContexts.Pop();
		_extSignatureTypes.ReportDestructors(Diagnostics);
		
		var result = new ResolvedFileNode(file, resolvedDeclarations, _importedFunctions.Values, node);
		_importedFunctions.Clear();
		return result;
	}
	
	public IResolvedDeclarationNode Visit(ConstructorNode node) => ResolveFunction(node, node.Body);
	
	public IResolvedDeclarationNode Visit(DestructorNode node) => ResolveFunction(node, node.Body);
	
	public IResolvedDeclarationNode Visit(FunctionNode node) => ResolveFunction(node, node.Body);
	
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
		var resolvedBody = VisitNode(body);
		_resolutionContexts.Pop();
		
		return new ResolvedFunctionNode(info, resolvedBody, node);
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
	
	public IResolvedExpressionNode Visit(CallExpressionNode node)
	{
		if (node.Target is AccessExpressionNode access && ResolveEnumType(access.Target) is { } enumType)
			return DeclaresNonCaseMember(enumType, access.Member.Text)
				? VisitStaticCall(node, access, enumType)
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
		IExpressionNode node = call is null ? access : call;
		var arguments = call?.Arguments ?? [];
		var enumCase = FindCase(enumType, access.Member);
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
	
	private (IResolvedExpressionNode Value, EnumSymbol? Type) ResolveMatchedValue(IExpressionNode node)
	{
		var value = VisitNode(node, null);
		if (value is ResolvedOwnExpressionNode { Type: BorrowType } owned)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, "Cannot move borrowed values"));
			value = owned.Value;
		}
		
		value = Decay(value);
		if (value.Type is EnumSymbol enumType)
			return (value, enumType);
		
		if (!IsInvalid(value))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{value.Type.Name}' is not an enum"));
		
		return (value, null);
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
		return new ResolvedPattern(enumCase, bindings);
	}
	
	private ImmutableArray<LocalVariableSymbol?> CreateBindings(PatternNode pattern, IReadOnlyList<TypeSymbol>? types,
		bool isMut, bool ownsValue) =>
	[
		..pattern.Bindings.Select((token, i) =>
			CreateBinding(token, pattern.BindingModes[i], types?[i] ?? NativeSymbols.Invalid, isMut, ownsValue))
	];
	
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
			case not null when symbol == enumType:
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
				if (_repeatedBindings.Add(binding))
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
		var (value, enumType) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, enumType, isMut, [node.Pattern]);
		var ownsValue = OwnsValue(value, enumType);
		if (enumType is not null && ResolvePattern(node.Pattern, enumType, isMut, ownsValue) is { } pattern)
			return new ResolvedIsExpressionNode(value, pattern, isMut || pattern.HasMutBindings, ownsValue, node);
		
		var invalid = new ResolvedInvalidExpressionNode(node, NativeSymbols.Bool);
		_failedPatterns[invalid] = CreateBindings(node.Pattern, null, isMut, ownsValue);
		return invalid;
	}
	
	public IResolvedStatementNode Visit(MatchStatementNode node)
	{
		var isMut = node.Mode is not null;
		var (value, enumType) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, enumType, isMut, node.Arms.Select(static arm => arm.Pattern));
		var ownsValue = OwnsValue(value, enumType);
		var arms = new List<ResolvedMatchArm>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var pattern = arm.Pattern is { } syntax && enumType is not null
				? ResolvePattern(syntax, enumType, isMut, ownsValue)
				: null;
			
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
		var (value, enumType) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, enumType, isMut, node.Arms.Select(static arm => arm.Pattern));
		var ownsValue = OwnsValue(value, enumType);
		var patterns = new List<ResolvedPattern?>(node.Arms.Length);
		var values = new List<IResolvedExpressionNode>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var pattern = arm.Pattern is { } syntax && enumType is not null
				? ResolvePattern(syntax, enumType, isMut, ownsValue)
				: null;
			
			var bindings = pattern?.Bindings ??
			               (arm.Pattern is { } failed ? CreateBindings(failed, null, isMut, ownsValue) : []);
			
			patterns.Add(pattern);
			values.Add(VisitInScope(arm.Value, bindings, target));
			summaries.Add(SummarizeArm(arm.Pattern, pattern, enumType, arm.SourceLocation));
		}
		
		ReportArmConflicts(summaries);
		if (enumType is not null)
			ReportMissingCases(node.Keyword, enumType, summaries);
		
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
	);
	
	private MatchArmSummary SummarizeArm(PatternNode? syntax, ResolvedPattern? pattern, EnumSymbol? enumType,
		SourceLocation location) => new(syntax is null, pattern?.Case,
		pattern is null ? null : _typePool.GetCaseValue(enumType!, pattern.Case), location);
	
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
		if (targetType is RecordSymbol record)
			return _typePool.GetConstructors(record).Count == 0
				? VisitRecordConstruction(node, record)
				: VisitConstructorCall(node, record, null);
		
		if (node.Arguments.Length != 1)
			return VisitConstructorCall(node, targetType, null);
		
		// Don't push targetType; we're trying to find a CAST to targetType, not a targetType itself
		var arg = VisitNode(node.Arguments[0], null);
		
		// If the argument has an invalid type, we don't want to cascade useless errors; assume identity conversion
		if (IsInvalid(arg))
			return new ResolvedConversionExpressionNode(arg, new IdentityConversion(targetType), node);
		
		if (arg.Type is UntypedType)
		{
			arg = MaterializeExpression(arg, targetType);
			
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
		
		return VisitConstructorCall(node, targetType, arg);
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
		var args = node.Arguments.Select(VisitArgument).ToArray();
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = GetName(node.Target);
		var constructors = _typePool.GetConstructors(definition);
		if (constructors.Count > 0)
			return ResolveInferredConstructor(node, definition, name, args, constructors);
		
		var fields = _typePool.GetMembers(definition).OfType<FieldSymbol>().ToArray();
		if (args.Length != fields.Length && args.Length > 0)
			return VisitRecordConstruction(node, definition, args);
		
		var inputs = args.Length == 0
			? []
			: fields.Select((field, i) => CreateInferenceInput(GetMemberType(field), ParameterMode.Own, args[i]));
		
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			CurrentTargetType);
		
		if (InstantiateInferred(result, definition, name, node.Target.SourceLocation) is not { } instance)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		return VisitRecordConstruction(node, instance, args);
	}
	
	private IResolvedExpressionNode ResolveInferredConstructor(CallExpressionNode node, RecordSymbol definition,
		string name, IResolvedExpressionNode[] args, IReadOnlyList<FunctionInfo> constructors)
	{
		var accessible = constructors.Where(info => CanAccess(definition, info.Symbol.Visibility)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(node.SourceLocation, "new",
				constructors.Select(static info => info.Symbol.Visibility)), CurrentTargetType);
		
		var failures = new List<Diagnostic>();
		var candidates = new List<ICallable>();
		foreach (var constructor in accessible)
		{
			var callable = new ReceiverCallable(constructor, definition);
			var inputs = args
				.Take(callable.ParameterTypes.Length)
				.Select((arg, i) => CreateInferenceInput(callable.ParameterTypes[i], callable.GetMode(i), arg));
			
			var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
				CurrentTargetType);
			
			var location = node.Target.SourceLocation;
			if (InstantiateInferred(result, definition, name, location, failures) is not { } instance)
				continue;
			
			var info = _typePool.GetConstructors(instance).First(info => info.Symbol == constructor.Symbol);
			candidates.Add(new ReceiverCallable(info, instance));
		}
		
		if (candidates.Count == 0 && accessible.Length == 1 && failures is [var failure])
		{
			Diagnostics.Add(failure);
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		return ResolveConstructorCall(node, definition, args, [..candidates], null);
	}
	
	private RecordSymbol? InstantiateInferred(InferenceResult result, RecordSymbol definition, string name,
		SourceLocation location, List<Diagnostic>? failures = null)
	{
		var diagnostic = result.Succeeded
			? ReportStoredBorrows(definition.TypeParameters, result.Arguments, _ => location)
			: ReportInferenceFailure(result, name, location);
		
		if (diagnostic is null)
			return _typePool.Instantiate(definition, result.Arguments) as RecordSymbol;
		
		if (failures is null)
			Diagnostics.Add(diagnostic);
		else
			failures.Add(diagnostic);
		
		return null;
	}
	
	private IResolvedExpressionNode VisitConstructorCall(CallExpressionNode node, TypeSymbol targetType,
		IResolvedExpressionNode? firstArg)
	{
		var args = new IResolvedExpressionNode[node.Arguments.Length];
		
		var iStart = 0;
		if (firstArg is not null)
		{
			iStart++;
			args[0] = firstArg;
		}
		
		for (var i = iStart; i < node.Arguments.Length; i++)
			args[i] = VisitArgument(node.Arguments[i]);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, targetType);
		
		var constructors = _typePool.GetConstructors(targetType);
		var ctorCandidates = constructors
			.Where(info => CanAccess(targetType, info.Symbol.Visibility))
			.Select(info => new ReceiverCallable(info, targetType))
			.ToArray();
		
		if (ctorCandidates.Length == 0 && constructors.Count > 0)
			return Error(node, ReportHiddenMember(node.SourceLocation, "new",
				constructors.Select(static info => info.Symbol.Visibility)), targetType);
		
		return ResolveConstructorCall(node, targetType, args, ctorCandidates, targetType);
	}
	
	private IResolvedExpressionNode ResolveConstructorCall(CallExpressionNode node, TypeSymbol targetType,
		IResolvedExpressionNode[] args, ICallable[] ctorCandidates, TypeSymbol? target)
	{
		var resolutionSet = ResolveCallable(ctorCandidates, args, MaterializationMode.Overload, target);
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Conversion to '{targetType.Name}' is ambiguous", targetType, node);
		
		if (!resolutionSet.HasResult)
		{
			if (ReportArgumentModes(ctorCandidates, args, target))
				return new ResolvedInvalidExpressionNode(node, targetType);
			
			var message = args.Length == 1
				? $"No constructor for '{targetType.Name}' accepts argument of type '{GetArgumentType(args[0]).Name}'"
				: $"No constructor for '{targetType.Name}' accepts these arguments";
			
			return Error(node, message, targetType, node);
		}
		
		var resolution = resolutionSet[0];
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
		string name, FunctionInfo[] functions) => ResolveTypeArguments(indexer) is { } typeArguments
		? ResolveCall(node, name, [..functions.Select(static info => new FunctionCallable(info))], null, typeArguments)
		: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	
	private TypeArgumentList? ResolveTypeArguments(IndexerExpressionNode node)
	{
		var context = CurrentResolutionContext;
		var types = node.Arguments.Select(context.ResolveTypeExpression).ToImmutableArray();
		return types.Any(static type => type is InvalidType)
			? null
			: new(types, [..node.Arguments.Select(static argument => argument.SourceLocation)], node.SourceLocation);
	}
	
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
				failures.Add(ResolutionContext.ReportTypeArgumentCount(explicitArguments.Location, name, open.Length));
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
				CurrentTargetType);
			
			if (!result.Succeeded)
			{
				failures.Add(ReportInferenceFailure(result, name, location));
				return null;
			}
			
			arguments = result.Arguments;
			locate = _ => location;
		}
		
		if (ReportStoredBorrows(open, arguments, locate) is { } violation)
		{
			failures.Add(violation);
			return null;
		}
		
		var instantiated = InstantiateDeclared(info, arguments);
		return candidate is FunctionCallable
			? new FunctionCallable(instantiated) { IsGeneric = true }
			: new ReceiverCallable(instantiated, instantiated.Signature.ReturnType) { IsGeneric = true };
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
			_ when type is NeverType or FunctionGroupType or InvalidType => null,
			_ when type is UntypedType literal => new(parameter, GetDefaultType(arg)) { Literal = literal },
			_ => new(parameter, type)
		};
	}
	
	private TypeSymbol GetDefaultType(IResolvedExpressionNode node) => node.Type switch
	{
		UntypedIntegerType when node is ResolvedLiteralExpressionNode literal =>
			FindDefaultIntegerType((BigInteger)literal.Value!) ?? NativeSymbols.Int32,
		UntypedIntegerType => NativeSymbols.Int32,
		UntypedFloatType => NativeSymbols.Float64,
		UntypedNullType => NativeSymbols.VoidPtr,
		UntypedStringType => NativeSymbols.Str,
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
	
	private Diagnostic? ReportStoredBorrows(ImmutableArray<TypeParameterSymbol> parameters,
		IReadOnlyList<TypeSymbol> arguments, Func<int, SourceLocation> locate)
	{
		for (var i = 0; i < parameters.Length; i++)
		{
			if (parameters[i].IsNoref && _typePool.HoldsBorrows(arguments[i]))
				return new(DiagnosticSeverity.Error, locate(i), $"Cannot store borrows in '{parameters[i].Name}'");
		}
		
		return null;
	}
	
	private IResolvedExpressionNode ResolveCall(CallExpressionNode node, string functionName, ICallable[] candidates,
		IResolvedExpressionNode? receiver, TypeArgumentList? typeArguments = null)
	{
		var args = new IResolvedExpressionNode[node.Arguments.Length];
		for (var i = 0; i < node.Arguments.Length; i++)
			args[i] = VisitArgument(node.Arguments[i]);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var failures = new List<Diagnostic>();
		var location = (node.Target is IndexerExpressionNode indexer ? indexer.Target : node.Target).SourceLocation;
		var specialized = candidates
			.Select(candidate => Specialize(candidate, functionName, args, typeArguments, location, failures))
			.OfType<ICallable>()
			.ToArray();
		
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
		
		TrackFunctionUse(info, node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		if (receiver is not null)
			resolvedArgs.Insert(0, CreateReceiver(receiver, info));
		
		var result = new ResolvedFunctionCallExpressionNode(info, resolvedArgs, node);
		return ApplyResultResolution(result, resolution);
	}
	
	private IResolvedExpressionNode CreateReceiver(IResolvedExpressionNode receiver, FunctionInfo method) =>
		method.Signature.GetMode(0) == ParameterMode.Mut
			? new ResolvedMutArgumentExpressionNode(MakeWritable(receiver), _typePool.GetPointerType(receiver.Type),
				receiver.Syntax)
			: receiver;
	
	private IResolvedExpressionNode VisitArgument(IExpressionNode node)
	{
		if (node is not BorrowExpressionNode { IsMutable: true } argument)
			return VisitNode(node, null);
		
		var place = VisitNode(argument.Value, null);
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
	
	private static Diagnostic? ReportArgumentMode(ICallable callable, int index, IResolvedExpressionNode arg)
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
	
	private static bool IsMutPlaceOf(IResolvedExpressionNode place, TypeSymbol declared) =>
		place.Type == declared || Decay(place).Type == declared;
	
	private static TypeSymbol GetArgumentType(IResolvedExpressionNode arg) =>
		arg is ResolvedMutArgumentExpressionNode argument ? argument.Place.Type : arg.Type;
	
	private IResolvedExpressionNode VisitMemberCall(CallExpressionNode node, AccessExpressionNode access,
		IndexerExpressionNode? indexer = null)
	{
		if (CurrentResolutionContext.TryResolveExpressionAsType(access.Target) is { } type)
			return VisitStaticCall(node, access, RequireTypeArguments(type, access.Target), indexer);
		
		var target = Decay(VisitNode(access.Target, null));
		if (IsInvalid(target))
			return VisitIndirectCall(node, target);
		
		var owner = GetMemberOwner(target.Type, access.Member.Text);
		if (FindField(target.Type, access.Member.Text) is not null || owner.GetProperty(access.Member.Text) is not null)
			return VisitIndirectCall(node, Index(indexer, ResolveAccess(access, target)));
		
		var functions = FindFunctions(owner, access.Member.Text);
		var methods = functions.Where(static function => function.HasReceiver).ToArray();
		if (methods.Length == 0)
			return Error(node, DescribeMissingMember(owner, access.Member.Text), CurrentTargetType,
				access.Member.SourceLocation);
		
		var accessible = methods.Where(method => CanAccess(owner, method.Function.Visibility)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(access.Member.SourceLocation, access.Member.Text,
				methods.Select(static method => method.Function.Visibility)), CurrentTargetType);
		
		if (owner != target.Type)
			target = ResolveDereference(TokenType.OpStar, target, access.Target);
		
		var candidates = accessible
			.Select(method => GetFunctionInfo(method.Function, owner))
			.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType));
		
		if (indexer is null)
			return ResolveCall(node, access.Member.Text, [..candidates], target);
		
		return ResolveTypeArguments(indexer) is { } typeArguments
			? ResolveCall(node, access.Member.Text, [..candidates], target, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IResolvedExpressionNode Index(IndexerExpressionNode? indexer, IResolvedExpressionNode target) =>
		indexer is null ? target : ResolveIndexing(indexer, target);
	
	private IResolvedExpressionNode VisitStaticCall(CallExpressionNode node, AccessExpressionNode access,
		TypeSymbol type, IndexerExpressionNode? indexer = null)
	{
		if (type is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (type.GetStaticField(access.Member.Text) is not null || type.GetProperty(access.Member.Text) is not null)
			return VisitIndirectCall(node, Index(indexer, VisitStaticMember(access, type)));
		
		var statics = FindStatics(type, access.Member.Text);
		if (statics.Length == 0)
			return Error(node, DescribeMissingStatic(type, access.Member.Text), CurrentTargetType,
				access.Member.SourceLocation);
		
		var accessible = FindAccessible(type, statics);
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(access.Member.SourceLocation, access.Member.Text,
				statics.Select(static function => function.Visibility)), CurrentTargetType);
		
		var candidates = accessible
			.Select(function => GetFunctionInfo(function, type))
			.Select(static info => new FunctionCallable(info));
		
		if (indexer is null)
			return ResolveCall(node, GetName(access), [..candidates], null);
		
		return ResolveTypeArguments(indexer) is { } typeArguments
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
		
		if (type.GetProperty(node.Member.Text) is { } property)
			return ResolvePropertyUse(node, node.Member.SourceLocation, property, type, null);
		
		var statics = FindStatics(type, node.Member.Text);
		if (statics.Length == 0)
			return Error(node, DescribeMissingStatic(type, node.Member.Text), CurrentTargetType,
				node.Member.SourceLocation);
		
		var accessible = FindAccessible(type, statics);
		return accessible.Length > 0
			? ResolveFunctionValue(node, accessible, type)
			: Error(node, ReportHiddenMember(node.Member.SourceLocation, node.Member.Text,
				statics.Select(static function => function.Visibility)), CurrentTargetType);
	}
	
	private static bool DeclaresNonCaseMember(TypeSymbol type, string name) => type.GetFunctions(name).Any() ||
	                                                                           type.GetStaticField(name) is not null ||
	                                                                           type.GetProperty(name) is not null;
	
	private IResolvedExpressionNode ResolvePropertyUse(IExpressionNode node, SourceLocation member,
		PropertySymbol property, TypeSymbol owner, IResolvedExpressionNode? receiver)
	{
		if (FindReceiverError(property.IsStatic, receiver) is { } receiverError)
			return Error(node, receiverError, CurrentTargetType, member);
		
		if (!CanAccess(owner, property.Visibility))
			return Error(node, DiagnosticReporter.ReportHidden(member, property.Name, property.Visibility, true),
				CurrentTargetType);
		
		if (node == storeTarget)
			return new ResolvedPropertyExpressionNode(property, owner, receiver, GetPropertyType(property, owner),
				node);
		
		if (property.Getter is not FunctionAccessor { Function: var getter })
			return Error(node, "Cannot read write-only properties", CurrentTargetType, member);
		
		return CanAccess(owner, getter.Visibility)
			? CallAccessor(getter, owner, receiver, [], node)
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
			return GetFunctionInfo(getter, owner).Signature.ReturnType;
		
		if (property.Setter is not FunctionAccessor { Function: var setter })
			return NativeSymbols.Invalid;
		
		var signature = GetFunctionInfo(setter, owner).Signature;
		return signature.GetDeclaredType(signature.ParameterTypes.Length - 1);
	}
	
	private ResolvedFunctionCallExpressionNode CallAccessor(FunctionSymbol accessor, TypeSymbol owner,
		IResolvedExpressionNode? receiver, IEnumerable<IResolvedExpressionNode> arguments, IExpressionNode syntax)
	{
		var info = GetFunctionInfo(accessor, owner);
		TrackFunctionUse(info, syntax);
		IEnumerable<IResolvedExpressionNode> receivers = receiver is null ? [] : [CreateReceiver(receiver, info)];
		return new ResolvedFunctionCallExpressionNode(info, [..receivers, ..arguments], syntax);
	}
	
	private static bool TakesMutSelf(FunctionInfo accessor) => accessor.Signature.GetMode(0) == ParameterMode.Mut;
	
	private static SourceLocation GetMemberLocation(IExpressionNode syntax) =>
		syntax is AccessExpressionNode access ? access.Member.SourceLocation : syntax.SourceLocation;
	
	private FunctionSymbol[] FindAccessible(TypeSymbol owner, IEnumerable<FunctionSymbol> functions) =>
		[..functions.Where(function => CanAccess(owner, function.Visibility))];
	
	private bool CanAccess(TypeSymbol owner, Visibility visibility) =>
		CurrentResolutionContext.CanAccess(owner, visibility);
	
	private static Diagnostic ReportHiddenMember(SourceLocation location, string name,
		IEnumerable<Visibility> visibilities) =>
		DiagnosticReporter.ReportHidden(location, name, visibilities.Max(), true);
	
	private Diagnostic ReportUndefinedSymbol(ISyntaxNode node, string name) =>
		CurrentResolutionContext.ReportHidden(name, node.SourceLocation) ??
		DiagnosticReporter.ReportUndefinedSymbol(node, name, GetVisibleSymbolNames());
	
	private string DescribeMissingMember(TypeSymbol type, string name) =>
		type.GetStaticField(name) is not null ? "Cannot use static fields through values"
		: FindFunctions(type, name).Length > 0 ? "Cannot use static functions through values"
		: $"Type '{type.Name}' has no member '{name}'";
	
	private string DescribeMissingStatic(TypeSymbol type, string name) =>
		FindFunctions(type, name).Length > 0 ? "Cannot use methods through types"
		: FindField(type, name) is not null ? "Cannot use fields through types"
		: $"Type '{type.Name}' has no member '{name}'";
	
	private static FunctionSymbol[] FindStatics(TypeSymbol type, string name) =>
	[
		..FindFunctions(type, name)
			.Where(static function => !function.HasReceiver)
			.Select(static function => function.Function)
	];
	
	private static MethodSymbol[] FindFunctions(TypeSymbol type, string name) => [..type.GetFunctions(name)];
	
	private FieldSymbol? FindField(TypeSymbol type, string name) =>
		_typePool.ResolveMember(GetMemberOwner(type, name), name) as FieldSymbol;
	
	private TypeSymbol GetMemberOwner(TypeSymbol type, string name)
	{
		if (type is PointerType { BaseType: var baseType } && baseType != NativeSymbols.Void)
			return baseType;
		
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
		null when type.GetProperty(name) is { } property => CanAccess(type, property.Visibility),
		null => FindFunctions(type, name).Any(function => CanAccess(type, function.Function.Visibility)),
		_ => true
	};
	
	private TypeSymbol? GetDereferenceTarget(TypeSymbol type) => FindDereferences(type)
		.Select(dereference => GetFunctionInfo(dereference.Function, type).Signature.ReturnType)
		.OfType<BorrowType>()
		.FirstOrDefault()?.Target;
	
	private IEnumerable<MethodSymbol> FindDereferences(TypeSymbol type) => type.GetFunctions("*")
		.Where(method => method.HasReceiver && CanAccess(type, method.Function.Visibility));
	
	private MethodSymbol? FindDereference(TypeSymbol type, ParameterMode mode) => FindDereferences(type)
		.FirstOrDefault(method => GetFunctionInfo(method.Function, type).Signature is var signature &&
		                          signature.GetMode(0) == mode && signature.ReturnType is BorrowType);
	
	private ResolvedFunctionCallExpressionNode CallDereference(IResolvedExpressionNode receiver,
		MethodSymbol dereference, IExpressionNode syntax)
	{
		var info = GetFunctionInfo(dereference.Function, receiver.Type);
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
					Operand: ResolvedFunctionCallExpressionNode { Arguments: [var receiver] } call
				} when FindDereference(receiver.Type, ParameterMode.ReadOnly)?.Function == call.Function.Symbol &&
				       FindDereference(receiver.Type, ParameterMode.Mut) is { } dereference:
				return Decay(CallDereference(receiver, dereference, call.Syntax));
			
			default:
				return place;
		}
	}
	
	private IResolvedExpressionNode VisitIndirectCall(CallExpressionNode node, IResolvedExpressionNode target)
	{
		if (target.Type is UntypedType)
			target = MaterializeAsDefault(target);
		
		target = Decay(target);
		var args = node.Arguments.Select(VisitArgument).ToArray();
		if (IsInvalid(target) || AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (target.Type is not FunctionType functionType)
			return Error(node, $"Cannot call a value of type '{target.Type.Name}'", CurrentTargetType, node.Target);
		
		ICallable[] candidates = [new FunctionTypeCallable(functionType)];
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		
		if (!resolutionSet.HasResult && ReportArgumentModes(candidates, args, null))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return Error(node, $"'{functionType.Name}' doesn't accept these arguments", CurrentTargetType, node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolutionSet[0]);
		return new ResolvedIndirectCallExpressionNode(target, resolvedArgs, functionType, node);
	}
	
	public IResolvedExpressionNode Visit(IndexerExpressionNode node) =>
		ResolveExplicitFunctionValue(node) ?? ResolveIndexing(node, VisitNode(node.Target));
	
	private IResolvedExpressionNode? ResolveExplicitFunctionValue(IndexerExpressionNode node)
	{
		var context = CurrentResolutionContext;
		FunctionSymbol[] functions;
		TypeSymbol? owner = null;
		switch (node.Target)
		{
			case VarExpressionNode variable:
				functions = GetFunctions(context.Resolve(variable.Identifier.Text));
				break;
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				functions = GetFunctions(context.ResolveMember(module, access.Member.Text));
				break;
			
			case AccessExpressionNode access when context.TryResolveExpressionAsType(access.Target) is { } type:
				owner = RequireTypeArguments(type, access.Target);
				if (owner is InvalidType)
					return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
				
				functions = FindAccessible(owner, FindStatics(owner, access.Member.Text));
				break;
			
			default:
				return null;
		}
		
		if (functions.Length == 0)
			return null;
		
		if (ResolveTypeArguments(node) is not { } typeArguments)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = GetName(node.Target);
		var infos = new List<FunctionInfo>();
		foreach (var function in functions)
		{
			var info = owner is null ? GetFunctionInfo(function) : GetFunctionInfo(function, owner);
			var open = GetOpenTypeParameters(info);
			if (open.Length != typeArguments.Types.Length)
				continue;
			
			if (ReportStoredBorrows(open, typeArguments.Types, i => typeArguments.Locations[i]) is { } violation)
				return Error(node, violation, CurrentTargetType);
			
			infos.Add(InstantiateDeclared(info, typeArguments.Types));
		}
		
		if (infos.Count > 0)
			return new ResolvedFunctionGroupExpressionNode(new FunctionGroupType(name, infos,
				infos is [var only] ? GetNaturalType(only).Name : name), node);
		
		var location = node.SourceLocation;
		return Error(node, functions is [var single]
			? ResolutionContext.ReportTypeArgumentCount(location, name, single.DeclaredTypeParameters.Length)
			: new(DiagnosticSeverity.Error, location,
				$"No overload of '{name}' takes {typeArguments.Types.Length} type arguments"), CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveIndexing(IndexerExpressionNode node, IResolvedExpressionNode target)
	{
		target = Decay(target);
		if (IsInvalid(target))
		{
			foreach (var argument in node.Arguments)
				VisitNode(argument);
			
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		// TODO Indexable user-defined types
		
		if (target.Type is not ArrayType { ElementType: var elementType })
			return Error(node, $"Cannot index into type '{target.Type.Name}'", CurrentTargetType, target.Syntax);
		
		if (node.Arguments.Length != 1)
			return Error(node, "Array indexer requires exactly one argument", elementType);
		
		var indexExpr = VisitNode(node.Arguments[0], NativeSymbols.UIntSize);
		return new ResolvedIndexerExpressionNode(elementType, target, indexExpr, node);
	}
	
	public IResolvedExpressionNode Visit(AccessExpressionNode node)
	{
		if (ResolveEnumType(node.Target) is { } enumType)
			return DeclaresNonCaseMember(enumType, node.Member.Text)
				? VisitStaticMember(node, enumType)
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
		
		if (target is ResolvedLiteralExpressionNode { Type: UntypedType })
			target = MaterializeAsDefault(target);
		
		target = Decay(target);
		if (GetMemberOwner(target.Type, node.Member.Text) != target.Type)
			target = ResolveDereference(TokenType.OpStar, target, node.Target);
		
		var memberName = node.Member.Text;
		var resolutionContext = CurrentResolutionContext;
		if (target.Type.GetProperty(memberName) is { } property)
			return ResolvePropertyUse(node, node.Member.SourceLocation, property, target.Type, target);
		
		if (resolutionContext.TypePool.ResolveMember(target.Type, memberName) is not { } member)
			return Error(node, FindFunctions(target.Type, memberName) switch
			{
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
		
		var elementType = (CurrentTargetType as ArrayType)?.ElementType;
		
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
		
		var type = _typePool.GetArrayType(elementType, node.Values.Length);
		return new ResolvedArrayExpressionNode(type, values, node);
	}
	
	public IResolvedExpressionNode Visit(InterpolatedStringExpressionNode node)
	{
		var text = new StringBuilder(node.Segments[0]);
		var isValid = true;
		for (var i = 0; i < node.Values.Length; i++)
		{
			if (FoldString(VisitNode(node.Values[i], null)) is { } value)
				text.Append(value);
			else
				isValid = false;
			
			text.Append(node.Segments[i + 1]);
		}
		
		return isValid
			? new ResolvedLiteralExpressionNode(NativeSymbols.UntypedString, text.ToString(), node)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private string? FoldString(IResolvedExpressionNode value)
	{
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
	
	private static bool IsString(TypeSymbol type) => type is StringType or UntypedStringType;
	
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
		var type = left.Type as StringType ?? right.Type as StringType;
		return type is null ? text : MaterializeExpression(text, type);
	}
	
	public IResolvedExpressionNode Visit(LiteralExpressionNode node)
	{
		if (node.Token.Type == TokenType.InvalidCharLiteral)
			return Error(node, "Invalid character literal", CurrentTargetType);
		
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
			? _targetTypes.TryPeek(out var targetType) ? targetType ?? NativeSymbols.Invalid : NativeSymbols.Invalid
			: resolutionContext.ResolveType(node.Type);
		
		return new ResolvedUndefExpressionNode(type, node);
	}
	
	public IResolvedExpressionNode Visit(SizeOfExpressionNode node)
	{
		var target = node.Target switch
		{
			ITypeNode type => CurrentResolutionContext.ResolveType(type),
			IExpressionNode expression => CurrentResolutionContext.TryResolveExpressionAsType(expression) is { } type
				? RequireTypeArguments(type, expression)
				: MaterializeAsDefault(VisitNode(expression)).Type,
			_ => NativeSymbols.Invalid
		};
		
		if (TypePool.ContainsTypeParameters(target))
			return new ResolvedSizeOfExpressionNode(target, node);
		
		if (IsInvalid(target) || _typePool.SizeTable.TryGetSize(target) is not { } size)
			return new ResolvedInvalidExpressionNode(node);
		
		var sizeInBits = size.CountBits(_pointerBitSize);
		var sizeInBytes = new BigInteger((sizeInBits + 7) / 8);
		
		return new ResolvedLiteralExpressionNode(NativeSymbols.UntypedInteger, sizeInBytes, node);
	}
	
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
				       enumType.GetProperty(access.Member.Text) is null
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
		
		if (type.GetProperty(member.Text) is { } property)
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
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, member.SourceLocation,
			$"Type '{type.Name}' has no member '{member.Text}'"));
		
		return false;
	}
	
	private bool IsAccessibleMember(TypeSymbol type, Token member, Visibility visibility)
	{
		if (CanAccess(type, visibility))
			return true;
		
		Diagnostics.Add(DiagnosticReporter.ReportHidden(member.SourceLocation, member.Text, visibility, true));
		return false;
	}
	
	public IResolvedExpressionNode Visit(VarExpressionNode node)
	{
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
			
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type, node);
			
			case GlobalSymbol g:
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
	
	private static string GetName(IExpressionNode node) => node switch
	{
		VarExpressionNode v => v.Identifier.Text,
		AccessExpressionNode a => $"{GetName(a.Target)}.{a.Member.Text}",
		_ => node.SourceLocation.GetText().ToString()
	};
	
	private IResolvedExpressionNode ResolveFunctionValue(IExpressionNode node, Symbol symbol) =>
		ResolveFunctionValue(node, GetFunctions(symbol));
	
	private IResolvedExpressionNode ResolveFunctionValue(IExpressionNode node, FunctionSymbol[] functions,
		TypeSymbol? owner = null)
	{
		var name = GetName(node);
		if (functions.Length == 0)
			return Error(node, $"Reference to '{name}' is ambiguous", CurrentTargetType);
		
		var infos = functions
			.Select(function => owner is null ? GetFunctionInfo(function) : GetFunctionInfo(function, owner))
			.ToArray();
		
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
			if (!result.Succeeded || ReportStoredBorrows(open, result.Arguments, _ => default) is not null)
				continue;
			
			var instantiated = InstantiateDeclared(function, result.Arguments);
			if (FunctionGroupType.Matches(instantiated, type))
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
			type = null;
		
		IResolvedExpressionNode? initializer;
		if (node.ExpressionNode is { } initializerNode)
		{
			initializer = type is null
				? MaterializeAsDefault(VisitNode(initializerNode, null))
				: VisitNode(initializerNode, type);
			
			if (type is ArrayType a && a.Length < 0 && initializer.Type is ArrayType)
				type = initializer.Type;
		}
		else
		{
			initializer = null;
			
			if (type is ArrayType a && a.Length < 0)
				return Error(node, "Unsized array type requires an initializer");
		}
		
		type ??= initializer?.Type ?? NativeSymbols.Invalid;
		
		var symbol = new LocalVariableSymbol(node.Identifier, type, node.IsMutable)
		{
			ConstantValue = node.IsMutable || initializer is null || !_typePool.IsCopy(type)
				? null
				: _evaluator.Evaluate(initializer),
			IsDeferred = !node.IsMutable && initializer is null
		};
		
		resolutionContext.LocalScope!.Define(symbol);
		
		return new ResolvedVarStatementNode(symbol, initializer, node);
	}
	
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
	
	public IResolvedStatementNode Visit(RepeatStatementNode node)
	{
		var count = VisitNode(node.Count);
		
		if (count.Type is UntypedType)
			count = MaterializeAsDefault(count);
		
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
		
		return new ResolvedRepeatStatementNode(count, body, symbol, node);
	}
	
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
		
		_unaryOpJobs.Push(new(op.Type));
		var operand = VisitNode(node.Operand, null);
		var consumed = _unaryOpJobs.Pop().Consumed;
		
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
				var resolutionSet = ResolveCallable(candidates, operandArray, MaterializationMode.Overload,
					CurrentTargetType);
				
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
			{ } dereference => Decay(CallDereference(operand, dereference, node)),
		var type when type.GetFunctions("*").Where(static method => method.HasReceiver).ToList() is
			{ Count: > 0 } hidden => Error(node, ReportHiddenMember(node.SourceLocation, "*",
			hidden.Select(static method => method.Function.Visibility)), CurrentTargetType),
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
			var left = Decay(VisitNode(node.Left, null));
			var isConjunction = op.Type == TokenType.OpAmpersandAmpersand;
			var right = Decay(isConjunction
				? VisitInScope(node.Right, GetTrueBindings(left))
				: VisitNode(node.Right, null));
			
			if (isConjunction)
				ReportRepeatedBindings(GetTrueBindings(left).Concat(GetTrueBindings(right)));
			
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
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
				var hint = $"'{signedType.Name}' can't represent every '{unsignedType.Name}' value";
				if (CountBits(signedType) > CountBits(unsignedType))
					hint += signedType == NativeSymbols.IntSize ? " on 32-bit targets" : " on 64-bit targets";
				
				var mismatch = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, op, right);
				return Error(node, mismatch with { Hints = [hint] }, CurrentTargetType);
			}
			
			var candidates = _operatorRegistry.GetBinaryCandidates(op.Type);
			var args = new[] { left, right };
			var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload,
				CurrentTargetType);
			
			if (resolutionSet.IsAmbiguous)
				return Error(node,
					$"Ambiguous operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'",
					CurrentTargetType);
			
			if (!resolutionSet.HasResult)
			{
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
		bool isOwnStore) => ResolveCompoundOperation(node, left) is var (right, operation)
		? new ResolvedAssignmentExpressionNode(left.Type, left, node.Op, right, operation, node, isOwnStore)
		: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	
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
		
		if (FindReceiverError(setter.Kind == FunctionKind.Free, target.Receiver) is { } setterError)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, setterError));
		
		if (!CanAccess(target.Owner, setter.Visibility))
			return RejectAssignment(node, DiagnosticReporter.ReportReadOnly(member, property.Name, setter.Visibility));
		
		var setterInfo = GetFunctionInfo(setter, target.Owner);
		var valueType = setterInfo.Signature.GetDeclaredType(setterInfo.Signature.ParameterTypes.Length - 1);
		if (node.Op.Type == TokenType.OpEqual)
			return CallAccessor(setter, target.Owner, target.Receiver, [VisitNode(node.Right, valueType)], node);
		
		if (property.Getter is not FunctionAccessor { Function: var getter })
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, "Cannot read write-only properties"));
		
		if (FindReceiverError(getter.Kind == FunctionKind.Free, target.Receiver) is { } getterError)
			return RejectAssignment(node, new(DiagnosticSeverity.Error, member, getterError));
		
		if (!CanAccess(target.Owner, getter.Visibility))
			return RejectAssignment(node, DiagnosticReporter.ReportWriteOnly(member, property.Name, getter.Visibility));
		
		var getterInfo = GetFunctionInfo(getter, target.Owner);
		var receiver = target.Receiver is { } place && (TakesMutSelf(getterInfo) || TakesMutSelf(setterInfo))
			? MakeWritable(place)
			: target.Receiver;
		
		var current = CallAccessor(getter, target.Owner, receiver, [], target.Syntax);
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
		
		// TODO Do we need common types anymore?
		var commonType = UnifyTypes(operands);
		if (commonType is null)
			return Error(node, "Cannot chain comparisons between incompatible types", CurrentTargetType);
		
		for (var i = 0; i < operands.Count; i++)
			operands[i] = CoerceToType(operands[i], commonType);
		
		var operations = new List<OperationImpl?>(node.Ops.Length);
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
			operations.Add((OperationImpl)resolution.Callable);
		}
		
		// TODO Aggregate types with implicit AND?
		
		return new ResolvedChainedExpressionNode(NativeSymbols.Bool, operands, operations, node);
	}
	
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
		
		if (target is not BorrowType && Decay(source) is var decayed && decayed != source)
			return ApplyImplicitConversion(decayed, target);
		
		if (_conversionTable.FindImplicit(source.Type, target) is { } conversion)
			return new ResolvedConversionExpressionNode(source, conversion, source.Syntax);
		
		if (source is ResolvedFunctionGroupExpressionNode group)
			return ReportFunctionMismatch(group, target);
		
		return Error(source.Syntax, $"Cannot convert type '{source.Type.Name}' to '{target.Name}'", target);
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
		switch (node.Type)
		{
			case UntypedIntegerType when node is ResolvedLiteralExpressionNode literal:
			{
				var value = (BigInteger)literal.Value!;
				var targetType = FindDefaultIntegerType(value);
				return targetType is not null
					? MaterializeLiteral(node.Syntax, targetType, value)
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
			
			case FunctionGroupType when node is ResolvedFunctionGroupExpressionNode group:
				return MaterializeFunctionAsDefault(group);
			
			default:
				return node;
		}
	}
	
	private IntegerType? FindDefaultIntegerType(BigInteger value) => NativeSymbols.IntegerTypes
		.Where(type => FitsInType(value, type))
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
		if (node is ResolvedFunctionGroupExpressionNode group)
			return MaterializeFunction(group, target);
		
		if (node.Type is NeverType)
			return new ResolvedConversionExpressionNode(node, new NeverConversion(target), node.Syntax);
		
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
			_ => literal
		};
	}
	
	private ResolvedLiteralExpressionNode MaterializeInteger(ResolvedLiteralExpressionNode node, IntegerType target)
	{
		if (node.Type is not UntypedIntegerType)
			return node;
		
		var value = (BigInteger)node.Value!;
		
		if (FitsInType(value, target))
			return MaterializeLiteral(node.Syntax, target, value);
		
		var fallback = SmallestFittingType(value);
		return fallback is not null
			? MaterializeLiteral(node.Syntax, fallback, value)
			: node; // TODO Diagnostic: Too large for any integer type
	}
	
	private ResolvedLiteralExpressionNode MaterializeIntegerAsFloat(ResolvedLiteralExpressionNode node,
		FloatType target)
	{
		var value = (BigInteger)node.Value!;
		return IsExactInFloat(value, target)
			? new ResolvedLiteralExpressionNode(target, (double)value, node.Syntax)
			: MaterializeInteger(node, NativeSymbols.Int32);
	}
	
	private IResolvedExpressionNode MaterializeFloat(ResolvedLiteralExpressionNode node, FloatType target)
	{
		var text = (string)node.Value!;
		var type = FitsInFloat(text, target) ? target : NativeSymbols.Float64;
		return FitsInFloat(text, type)
			? new ResolvedLiteralExpressionNode(type, ParseFloatValue(text, type), node.Syntax)
			: Error(node.Syntax, "Float literal too large to fit any type", target);
	}
	
	private static double ParseFloatValue(string text, FloatType type) => type == NativeSymbols.Float32
		? float.Parse(text, CultureInfo.InvariantCulture)
		: double.Parse(text, CultureInfo.InvariantCulture);
	
	private static bool FitsInFloat(string text, FloatType type) => double.IsFinite(ParseFloatValue(text, type));
	
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
	
	private FunctionInfo GetFunctionInfo(FunctionSymbol function, TypeSymbol owner) => _typePool.InstantiateFunction(
		GetFunctionInfo(function), [..owner.TypeArguments, ..function.DeclaredTypeParameters]);
	
	private TypeSymbol RequireTypeArguments(TypeSymbol type, IExpressionNode node)
	{
		if (type is not RecordSymbol { IsGenericDefinition: true } generic || node is IndexerExpressionNode)
			return type;
		
		Diagnostics.Add(ResolutionContext.ReportTypeArgumentCount(node.SourceLocation, generic));
		return NativeSymbols.Invalid;
	}
	
	private void TrackFunctionUse(FunctionInfo info, IExpressionNode syntax)
	{
		if (!_signatures.IsLocal(info.Symbol) || info.File.Module != CurrentResolutionContext.File.Module)
			_importedFunctions.TryAdd(info.Symbol, info);
		
		if (CurrentResolutionContext.ContainingFunction is not { Symbol: { TypeParameters.IsEmpty: false } caller } ||
		    info.TypeArguments.IsDefaultOrEmpty)
			return;
		
		for (var i = 0; i < info.TypeArguments.Length; i++)
		{
			var argument = info.TypeArguments[i];
			foreach (var parameter in caller.TypeParameters.Where(parameter => Mentions(argument, parameter)))
				_instantiations.Add(new((caller, parameter), (info.Symbol, info.Symbol.TypeParameters[i]),
					argument != parameter, syntax.SourceLocation));
		}
	}
	
	private static bool Mentions(TypeSymbol type, TypeParameterSymbol parameter) => type switch
	{
		TypeParameterSymbol other => other == parameter,
		RecordSymbol record => record.TypeArguments.Any(argument => Mentions(argument, parameter)),
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
				
				if (cost == int.MaxValue)
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
				var conversion = _conversionTable.FindImplicit(candidate.ReturnType, target);
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
			if (arg is ResolvedMutArgumentExpressionNode argument)
			{
				result.Add(ApplyMutArgument(argument, target));
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
		
		var place = argument.Place.Type == declared ? argument.Place : Decay(argument.Place);
		if (target is BorrowType borrow)
			return new ResolvedBorrowExpressionNode(place, borrow, false, argument.Syntax);
		
		return place == argument.Place
			? argument
			: new ResolvedMutArgumentExpressionNode(place, _typePool.GetPointerType(place.Type), argument.Syntax);
	}
	
	private IResolvedExpressionNode PromoteVariadicArgument(IResolvedExpressionNode arg)
	{
		if (arg.Type is UntypedStringType)
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
		CallableResolution resolution) => resolution.ResultConversion is { } conversion
		? new ResolvedConversionExpressionNode(node, conversion, node.Syntax)
		: node;
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		ParameterMode parameterMode, MaterializationMode mode, bool ignoreModes)
	{
		var mutTarget = arg.Type is BorrowType ? null : GetMutTarget(target, parameterMode);
		if (parameterMode == ParameterMode.Mut || mutTarget is not null)
		{
			if (arg is ResolvedMutArgumentExpressionNode mutArgument && mutTarget is not null &&
			    IsMutPlaceOf(mutArgument.Place, mutTarget))
				return (mutArgument.Place.Type == mutTarget ? 0 : 1, null);
			
			return ignoreModes ? (0, null) : (int.MaxValue, null);
		}
		
		if (arg is not ResolvedMutArgumentExpressionNode argument)
			return MatchArg(arg, target, mode);
		
		return ignoreModes ? MatchArg(argument.Place, target, mode) : (int.MaxValue, null);
	}
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		MaterializationMode mode)
	{
		if (arg.Type == target)
			return (0, null);
		
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
				? (function.Symbol.IsExternal == functionType.IsExternal ? 0 : 1, null)
				: (int.MaxValue, null);
		
		if (arg.Type is UntypedType u)
		{
			// Special case for literals: If target type cannot store the value, conversion is impossible
			if (arg is ResolvedLiteralExpressionNode literal && !LiteralFits(literal, target))
				return (int.MaxValue, null);
			
			var cost = u.MaterializationCost(target, mode);
			return (cost, null);
		}
		
		var conversion = _conversionTable.FindImplicit(arg.Type, target);
		return conversion is null ? (Cost: int.MaxValue, null) : (conversion.Cost, conversion);
	}
	
	private bool LiteralFits(ResolvedLiteralExpressionNode literal, TypeSymbol target) => (literal.Type, target) switch
	{
		(UntypedIntegerType, IntegerType type) => FitsInType((BigInteger)literal.Value!, type),
		(UntypedIntegerType, FloatType type) => IsExactInFloat((BigInteger)literal.Value!, type),
		(UntypedFloatType, FloatType type) => FitsInFloat((string)literal.Value!, type),
		_ => true
	};
	
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
	
	private sealed class ReceiverCallable(FunctionInfo info, TypeSymbol type) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public bool IsGeneric { get; init; }
		
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } =
			info.Signature.ParameterTypes.Skip(1).ToImmutableArray(); // Skip implicit self
		
		public TypeSymbol ReturnType { get; } = type;
		public ParameterMode GetMode(int index) => Info.Signature.GetMode(index + 1);
		public string GetParameterName(int index) => Info.Symbol.Parameters[index + 1].Name;
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