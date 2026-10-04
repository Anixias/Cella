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
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	private TypeSymbol? CurrentTargetType => _targetTypes.TryPeek(out var result) ? result : null;
	
	public Resolver(SymbolTable symbolTable, SignatureCollector signatures, TypePool typePool, uint pointerBitSize)
	{
		_typePool = typePool;
		_conversionTable = typePool.ConversionTable;
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
			EvaluateConstant = EvaluateConstant
		};
		
		_importedFunctions.Clear();
		_resolutionContexts.Push(resolutionContext);
		
		var resolvedDeclarations = new List<IResolvedDeclarationNode>(node.Declarations.Length);
		foreach (var declaration in node.Declarations)
			resolvedDeclarations.Add(VisitNode(declaration));
		
		_resolutionContexts.Pop();
		
		var result = new ResolvedFileNode(file, resolvedDeclarations, _importedFunctions.Values, node);
		_importedFunctions.Clear();
		return result;
	}
	
	public IResolvedDeclarationNode Visit(ConstructorNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetFunctionInfo(function);
		
		var resolutionContext = CurrentResolutionContext with
		{
			ContainingFunction = info,
			LocalScope = info.Scope
		};
		
		_resolutionContexts.Push(resolutionContext);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedFunctionNode(info, body, node);
	}
	
	public IResolvedDeclarationNode Visit(FunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _signatures.GetFunctionInfo(function);
		
		var resolutionContext = CurrentResolutionContext with
		{
			ContainingFunction = info,
			LocalScope = info.Scope
		};
		
		_resolutionContexts.Push(resolutionContext);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedFunctionNode(info, body, node);
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
	
	public IResolvedDeclarationNode Visit(RecordNode node)
	{
		var members = new List<IResolvedDeclarationNode>();
		
		foreach (var member in node.Members)
			members.Add(VisitNode(member));
		
		var record = (RecordSymbol)_symbolTable.DeclarationSymbols[node];
		return new ResolvedRecordNode(record, members, node);
	}
	
	public IResolvedDeclarationNode Visit(EnumNode node) =>
		new ResolvedEnumNode((EnumSymbol)_symbolTable.DeclarationSymbols[node], node);
	
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
			return VisitEnumCase(access, enumType, node);
		
		return CurrentResolutionContext.TryResolveExpressionAsType(node.Target) switch
		{
			null => VisitFunctionCall(node),
			InvalidType => new ResolvedInvalidExpressionNode(node, CurrentTargetType),
			var targetType => VisitTypeCall(node, targetType)
		};
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
		if (value.Type is PointerType
		    {
			    PointerKind: PointerKind.Mutable or PointerKind.Immutable or PointerKind.Owning
		    })
			value = ResolveDereference(TokenType.OpStar, value, node);
		
		if (value.Type is EnumSymbol enumType)
			return (value, enumType);
		
		if (!IsInvalid(value))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{value.Type.Name}' is not an enum"));
		
		return (value, null);
	}
	
	private ResolvedPattern? ResolvePattern(PatternNode pattern, EnumSymbol enumType)
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
		
		var bindings = CreateBindings(pattern, payloadTypes);
		ReportRepeatedBindings(bindings.OfType<LocalVariableSymbol>());
		return new ResolvedPattern(enumCase, bindings);
	}
	
	private static ImmutableArray<LocalVariableSymbol?> CreateBindings(PatternNode pattern,
		IReadOnlyList<TypeSymbol>? types) =>
	[
		..pattern.Bindings.Select((token, i) => token.Text == "_"
			? null
			: new LocalVariableSymbol(token, types?[i] ?? NativeSymbols.Invalid, false) { IsPatternBinding = true })
	];
	
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
				
				Diagnostics.Add(DiagnosticReporter.ReportUndefinedType(typeName.SourceLocation, typeName.Text,
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
	
	private void ReportDeclarationBody(IStatementNode body, string owner)
	{
		if (body is VarStatementNode declaration)
			Diagnostics.Add(new(DiagnosticSeverity.Error, declaration.SourceLocation,
				$"A declaration can't be the whole body of {owner}"));
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
		var (value, enumType) = ResolveMatchedValue(node.Value);
		if (enumType is not null && ResolvePattern(node.Pattern, enumType) is { } pattern)
			return new ResolvedIsExpressionNode(value, pattern, node);
		
		var invalid = new ResolvedInvalidExpressionNode(node, NativeSymbols.Bool);
		_failedPatterns[invalid] = CreateBindings(node.Pattern, null);
		return invalid;
	}
	
	public IResolvedStatementNode Visit(MatchStatementNode node)
	{
		var (value, enumType) = ResolveMatchedValue(node.Value);
		var arms = new List<ResolvedMatchArm>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var pattern = arm.Pattern is { } syntax && enumType is not null ? ResolvePattern(syntax, enumType) : null;
			var bindings = pattern?.Bindings ?? (arm.Pattern is { } failed ? CreateBindings(failed, null) : []);
			ReportDeclarationBody(arm.Body, "a match arm");
			arms.Add(new ResolvedMatchArm(pattern, VisitInScope(arm.Body, bindings)));
			summaries.Add(SummarizeArm(arm.Pattern, pattern, enumType, arm.SourceLocation));
		}
		
		ReportArmConflicts(summaries);
		return new ResolvedMatchStatementNode(value, arms, node);
	}
	
	public IResolvedExpressionNode Visit(MatchExpressionNode node)
	{
		var target = CurrentTargetType;
		var (value, enumType) = ResolveMatchedValue(node.Value);
		var patterns = new List<ResolvedPattern?>(node.Arms.Length);
		var values = new List<IResolvedExpressionNode>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var pattern = arm.Pattern is { } syntax && enumType is not null ? ResolvePattern(syntax, enumType) : null;
			var bindings = pattern?.Bindings ?? (arm.Pattern is { } failed ? CreateBindings(failed, null) : []);
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
		return new ResolvedMatchExpressionNode(value, arms, type, node);
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
		if (targetType is RecordSymbol record && _typePool.GetConstructors(record).Count == 0)
			return VisitRecordConstruction(node, record);
		
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
		
		if (_conversionTable.FindExplicit(arg.Type, targetType) is { } conversion)
			return new ResolvedConversionExpressionNode(arg, conversion, node);
		
		return VisitConstructorCall(node, targetType, arg);
	}
	
	private IResolvedExpressionNode VisitRecordConstruction(CallExpressionNode node, RecordSymbol record)
	{
		var fields = _typePool.GetMembers(record).OfType<FieldSymbol>().ToArray();
		if (node.Arguments.Length > 0 && node.Arguments.Length != fields.Length)
		{
			var args = node.Arguments.Select(a => VisitNode(a, null)).ToArray();
			if (AnyInvalid(args))
				return new ResolvedInvalidExpressionNode(node, record);
			
			var names = string.Join(", ", fields.Select(static f => f.Name));
			var diagnostic = new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
				$"No constructor for '{record.Name}' accepts these arguments")
			{
				Hints =
				[
					fields.Length == 0
						? $"'{record.Name}' has no fields"
						: $"'{record.Name}' takes no arguments, or one for each field: {names}"
				]
			};
			
			return Error(node, diagnostic, record);
		}
		
		var values = node.Arguments.Select((a, i) => VisitNode(a, GetMemberType(fields[i]))).ToArray();
		return new ResolvedRecordExpressionNode(record, fields.Zip(values), node);
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
			args[i] = VisitNode(node.Arguments[i], null);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, targetType);
		
		var ctorCandidates = _typePool.GetConstructors(targetType)
			.Select(info => new ConstructorCallable(info, targetType));
		
		var resolutionSet = ResolveCallable(ctorCandidates, args, MaterializationMode.Overload, targetType);
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Conversion to '{targetType.Name}' is ambiguous", targetType, node);
		
		if (!resolutionSet.HasResult)
		{
			var message = args.Length == 1
				? $"No constructor for '{targetType.Name}' accepts argument of type '{args[0].Type.Name}'"
				: $"No constructor for '{targetType.Name}' accepts these arguments";
			
			return Error(node, message, targetType, node);
		}
		
		var resolution = resolutionSet[0];
		var callable = (ConstructorCallable)resolution.Callable;
		var info = callable.Info;
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		var result = new ResolvedConstructorCallExpressionNode(info, resolvedArgs, targetType, node);
		
		if (resolution.ResultConversion is { } resultConversion && resultConversion.To != targetType)
			throw new InvalidOperationException(); // Should be impossible
		
		TrackImportedFunction(info);
		
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
				
				var diagnostic = DiagnosticReporter.ReportUndefinedSymbol(node, varExpr.Identifier.Text,
					GetVisibleSymbolNames());
				
				return Error(node, diagnostic, CurrentTargetType);
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				symbol = ResolutionContext.ResolveMember(module, access.Member.Text);
				break;
			
			case AccessExpressionNode access:
				return VisitMemberCall(node, access);
			
			default:
				return VisitIndirectCall(node, VisitNode(node.Target, null));
		}
		
		var functionName = GetName(node.Target);
		var functionSymbols = GetFunctions(symbol);
		
		if (functionSymbols.Length == 0)
			return VisitIndirectCall(node, VisitNode(node.Target, null));
		
		var args = new IResolvedExpressionNode[node.Arguments.Length];
		for (var i = 0; i < node.Arguments.Length; i++)
			args[i] = VisitNode(node.Arguments[i], null);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var candidates = functionSymbols
			.Select(GetFunctionInfo)
			.Select(static info => new FunctionCallable(info))
			.ToArray();
		
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload, CurrentTargetType);
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Call to '{functionName}' is ambiguous", CurrentTargetType, node.Target);
		
		// TODO If only one candidate, we could report the unmatched arguments instead of the whole function?
		if (!resolutionSet.HasResult)
			return Error(node, $"No overload of '{functionName}' accepts these arguments", CurrentTargetType,
				node.Target);
		
		var resolution = resolutionSet[0];
		var callable = (FunctionCallable)resolution.Callable;
		var info = callable.Info;
		
		TrackImportedFunction(info);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		var result = new ResolvedFunctionCallExpressionNode(info, resolvedArgs, node);
		return ApplyResultResolution(result, resolution);
	}
	
	private IResolvedExpressionNode VisitMemberCall(CallExpressionNode node, AccessExpressionNode access)
	{
		if (access.Target is VarExpressionNode name &&
		    CurrentResolutionContext.Resolve(name.Identifier.Text) is TypeSymbol)
			return Error(node, "Member calls are not supported yet", CurrentTargetType, node.Target);
		
		var target = VisitNode(access.Target, null);
		if (IsInvalid(target))
			return VisitIndirectCall(node, target);
		
		if (FindField(target.Type, access.Member.Text) is null)
			return Error(node, "Member calls are not supported yet", CurrentTargetType, node.Target);
		
		return VisitIndirectCall(node, ResolveAccess(access, target));
	}
	
	private FieldSymbol? FindField(TypeSymbol type, string name)
	{
		var lookupType = type is PointerType
		{
			PointerKind: PointerKind.Mutable or PointerKind.Immutable or PointerKind.Owning
		} pointer
			? pointer.BaseType
			: type;
		
		return _typePool.ResolveMember(lookupType, name) as FieldSymbol;
	}
	
	private IResolvedExpressionNode VisitIndirectCall(CallExpressionNode node, IResolvedExpressionNode target)
	{
		if (target.Type is UntypedType)
			target = MaterializeAsDefault(target);
		
		var args = node.Arguments.Select(argument => VisitNode(argument, null)).ToArray();
		if (IsInvalid(target) || AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (target.Type is not FunctionType functionType)
			return Error(node, $"Cannot call a value of type '{target.Type.Name}'", CurrentTargetType, node.Target);
		
		var resolutionSet = ResolveCallable([new FunctionTypeCallable(functionType)], args,
			MaterializationMode.Overload);
		
		if (!resolutionSet.HasResult)
			return Error(node, $"'{functionType.Name}' doesn't accept these arguments", CurrentTargetType, node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolutionSet[0]);
		return new ResolvedIndirectCallExpressionNode(target, resolvedArgs, functionType, node);
	}
	
	public IResolvedExpressionNode Visit(HeapExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var typePool = resolutionContext.TypePool;
		
		IResolvedExpressionNode? initializer;
		TypeSymbol elementType;
		
		switch (node.Target)
		{
			case ITypeNode n:
				elementType = resolutionContext.ResolveType(n);
				initializer = null;
				break;
			
			case IExpressionNode n when resolutionContext.TryResolveExpressionAsType(n) is { } type:
				elementType = type;
				initializer = null;
				break;
			
			case IExpressionNode n:
				var targetElementType = CurrentTargetType is PointerType { PointerKind: PointerKind.Owning } ptrType
					? ptrType.BaseType
					: null;
				
				initializer = targetElementType is null ? VisitNode(n) : VisitNode(n, targetElementType);
				
				if (initializer.Type is UntypedType)
				{
					initializer = targetElementType is not null
						? MaterializeExpression(initializer, targetElementType)
						: MaterializeAsDefault(initializer);
				}
				
				elementType = initializer.Type;
				break;
			
			default:
				return Error(node, "'heap' must be followed by a type or an expression",
					CurrentTargetType);
		}
		
		if (IsInvalid(elementType))
			return new ResolvedInvalidExpressionNode(node);
		
		var ownType = typePool.GetPointerType(elementType, PointerKind.Owning);
		return new ResolvedHeapExpressionNode(initializer, ownType, node);
	}
	
	public IResolvedExpressionNode Visit(IndexerExpressionNode node)
	{
		var target = VisitNode(node.Target);
		if (IsInvalid(target))
		{
			foreach (var argument in node.Arguments)
				VisitNode(argument);
			
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		// TODO Indexable user-defined types
		
		// Auto-dereferencing through high-level pointer types
		var lookupType = target.Type;
		if (target.Type is PointerType
		    {
			    PointerKind: PointerKind.Mutable or PointerKind.Immutable or PointerKind.Owning
		    } ptrType)
		{
			lookupType = ptrType.BaseType;
			target = ResolveDereference(TokenType.OpStar, target, node.Target);
		}
		
		TypeSymbol elementType;
		switch (lookupType)
		{
			case SpanType spanType:
				elementType = spanType.ElementType;
				break;
			
			case ViewType viewType:
				elementType = viewType.ElementType;
				break;
			
			case ArrayType arrayType:
				elementType = arrayType.ElementType;
				break;
			
			default:
				return Error(node, $"Cannot index into type '{target.Type.Name}'", CurrentTargetType, target.Syntax);
		}
		
		if (node.Arguments.Length != 1)
			return Error(node, "Array indexer requires exactly one argument", elementType);
		
		var indexExpr = VisitNode(node.Arguments[0], NativeSymbols.UIntSize);
		return new ResolvedIndexerExpressionNode(elementType, target, indexExpr, node);
	}
	
	public IResolvedExpressionNode Visit(AccessExpressionNode node)
	{
		if (ResolveEnumType(node.Target) is { } enumType)
			return VisitEnumCase(node, enumType, null);
		
		if (CurrentResolutionContext.ResolveModule(node.Target) is { } module)
			return VisitModuleMember(node, module);
		
		return ResolveAccess(node, VisitNode(node.Target));
	}
	
	private IResolvedExpressionNode VisitModuleMember(AccessExpressionNode node, ModulePathSymbol module)
	{
		if (ResolutionContext.ResolveMember(module, node.Member.Text) is { } member)
			return ResolveSymbolValue(node, member);
		
		var diagnostic = DiagnosticReporter.ReportUndefinedMember(node.Member.SourceLocation, module, node.Member.Text);
		return Error(node, diagnostic, CurrentTargetType);
	}
	
	private IResolvedExpressionNode ResolveAccess(AccessExpressionNode node, IResolvedExpressionNode target)
	{
		if (IsInvalid(target))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var memberName = node.Member.Text;
		var resolutionContext = CurrentResolutionContext;
		
		// Auto-dereferencing through high-level pointer types
		var lookupType = target.Type;
		if (target.Type is PointerType
		    {
			    PointerKind: PointerKind.Mutable or PointerKind.Immutable or PointerKind.Owning
		    } ptrType)
		{
			lookupType = ptrType.BaseType;
			target = ResolveDereference(TokenType.OpStar, target, node.Target);
		}
		
		if (resolutionContext.TypePool.ResolveMember(lookupType, memberName) is not { } member)
			return Error(node, $"Type '{lookupType.Name}' has no member '{memberName}'", CurrentTargetType,
				node.Member.SourceLocation);
		
		var memberType = GetMemberType(member);
		return new ResolvedAccessExpressionNode(target, member, memberType, node);
	}
	
	public IResolvedExpressionNode Visit(ArrayExpressionNode node)
	{
		var values = new List<IResolvedExpressionNode>(node.Values.Length);
		
		var elementType = CurrentTargetType switch
		{
			ArrayType t => t.ElementType,
			SpanType t => t.ElementType,
			ViewType t => t.ElementType,
			_ => null
		};
		
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
	
	public IResolvedExpressionNode Visit(LiteralExpressionNode node)
	{
		if (node.Token.Type == TokenType.InterpolatedStringLiteral)
		{
			// TODO String interpolation
			var diagnostic = new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
				"String interpolation is not supported yet")
			{
				Hints = ["Use '\\{' for a literal brace"]
			};
			
			return Error(node, diagnostic, CurrentTargetType);
		}
		
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
			IExpressionNode expression => CurrentResolutionContext.TryResolveExpressionAsType(expression)
			                              ?? MaterializeAsDefault(VisitNode(expression)).Type,
			_ => NativeSymbols.Invalid
		};
		
		if (IsInvalid(target) || _typePool.SizeTable.TryGetSize(target) is not { } size)
			return new ResolvedInvalidExpressionNode(node);
		
		var sizeInBits = size.CountBits(_pointerBitSize);
		var sizeInBytes = new BigInteger((sizeInBits + 7) / 8);
		
		return new ResolvedLiteralExpressionNode(NativeSymbols.UntypedInteger, sizeInBytes, node);
	}
	
	public IResolvedExpressionNode Visit(VarExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var varName = node.Identifier.Text;
		var symbol = resolutionContext.Resolve(varName);
		
		if (symbol is null)
		{
			var diagnostic = DiagnosticReporter.ReportUndefinedSymbol(node, varName, GetVisibleSymbolNames());
			return Error(node, diagnostic, CurrentTargetType);
		}
		
		return ResolveSymbolValue(node, symbol);
	}
	
	private IResolvedExpressionNode ResolveSymbolValue(IExpressionNode node, Symbol symbol)
	{
		switch (symbol)
		{
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type, node);
			
			case GlobalSymbol g:
				return new ResolvedGlobalExpressionNode(g, _signatures.GetGlobalType(g), node);
			
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
	
	private IResolvedExpressionNode ResolveFunctionValue(IExpressionNode node, Symbol symbol)
	{
		var name = GetName(node);
		var functions = GetFunctions(symbol);
		
		if (functions.Length == 0)
			return Error(node, $"Reference to '{name}' is ambiguous", CurrentTargetType);
		
		var infos = functions.Select(GetFunctionInfo).ToArray();
		var typeName = infos is [var single] ? GetNaturalType(single).Name : name;
		return new ResolvedFunctionGroupExpressionNode(new FunctionGroupType(name, infos, typeName), node);
	}
	
	private FunctionType GetNaturalType(FunctionInfo function) => _typePool.GetFunctionType(
		function.Symbol.IsExternal, function.Signature.ParameterTypes, function.Signature.ReturnType);
	
	private IResolvedExpressionNode MaterializeFunction(ResolvedFunctionGroupExpressionNode node, TypeSymbol target)
	{
		if (target is not FunctionType type || node.Group.Find(type) is not { } function)
			return node;
		
		TrackImportedFunction(function);
		return new ResolvedFunctionReferenceExpressionNode(function, type, node.Syntax);
	}
	
	private IResolvedExpressionNode MaterializeFunctionAsDefault(ResolvedFunctionGroupExpressionNode node)
	{
		var group = node.Group;
		if (group.Functions is not [var function])
			return Error(node.Syntax, $"Reference to '{group.FunctionName}' is ambiguous", NativeSymbols.Invalid);
		
		if (function.Signature.IsVariadic)
			return Error(node.Syntax, $"Variadic function '{group.FunctionName}' can't be used as a value",
				NativeSymbols.Invalid);
		
		TrackImportedFunction(function);
		return new ResolvedFunctionReferenceExpressionNode(function, GetNaturalType(function), node.Syntax);
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
		
		ReportDeclarationBody(node.Then, "an 'if'");
		var then = VisitInScope(node.Then, GetTrueBindings(condition));
		
		if (node.Else is { } elseNode)
			ReportDeclarationBody(elseNode, "an 'else'");
		
		var @else = node.Else is null ? null : VisitInScope(node.Else, []);
		
		return new ResolvedIfStatementNode(condition, then, @else, node);
	}
	
	public IResolvedStatementNode Visit(VarStatementNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		if (!node.IsMutable && node.ExpressionNode is null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				"A 'val' without an initial value is not supported yet"));
		
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
			ConstantValue = node.IsMutable || initializer is null ? null : _evaluator.Evaluate(initializer)
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
		ReportDeclarationBody(node.Body, "a loop");
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
		ReportDeclarationBody(node.Body, "a loop");
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
		ReportDeclarationBody(node.Body, "a loop");
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
		ReportDeclarationBody(node.Body, "a loop");
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
		
		switch (op.Type)
		{
			// Special unary operators that aren't stored in the registry
			case TokenType.OpAt:
				return ResolveAddressOf(op.Type, operand, node);
			
			case TokenType.OpStar:
				return ResolveDereference(op.Type, operand, node);
			
			case TokenType.KeywordMut:
			case TokenType.KeywordImm:
				return ResolveBorrow(op.Type, operand, node);
			
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
		_ => Error(node, $"Cannot dereference type '{operand.Type.Name}'", CurrentTargetType)
	};
	
	private ResolvedUnaryOpExpressionNode ResolveAddressOf(TokenType opType, IResolvedExpressionNode operand,
		UnaryOpExpressionNode node)
	{
		var ptrType = _typePool.GetPointerType(operand.Type, PointerKind.Unsafe);
		return new(operand, new NativeImpl(opType, ptrType), node);
	}
	
	private IResolvedExpressionNode ResolveBorrow(TokenType opType, IResolvedExpressionNode operand,
		UnaryOpExpressionNode node)
	{
		if (operand.Type is not PointerType { PointerKind: PointerKind.Owning } ptrType)
			return Error(node, $"Cannot borrow type '{operand.Type.Name}'", null);
		
		var borrowType = _typePool.GetPointerType(ptrType.BaseType, opType switch
		{
			TokenType.KeywordMut => PointerKind.Mutable,
			TokenType.KeywordImm => PointerKind.Immutable,
			_ => throw new InvalidOperationException()
		});
		
		return new ResolvedConversionExpressionNode(operand, new FreeConversion(operand.Type, borrowType,
			ConversionKind.Implicit), node);
	}
	
	public IResolvedExpressionNode Visit(BinaryOpExpressionNode node)
	{
		var op = node.Op;
		var isAssignment = op.Type is TokenType.OpEqual or TokenType.OpPlusEqual or TokenType.OpMinusEqual
			or TokenType.OpStarEqual or TokenType.OpSlashEqual or TokenType.OpPercentEqual or TokenType.OpAmpersandEqual
			or TokenType.OpBarEqual or TokenType.OpHatEqual or TokenType.OpLessLessEqual
			or TokenType.OpGreaterGreaterEqual or TokenType.OpLessLessLessEqual
			or TokenType.OpGreaterGreaterGreaterEqual;
		
		if (isAssignment)
		{
			var left = VisitNode(node.Left, null);
			if (op.Type != TokenType.OpEqual)
				return ResolveCompoundAssignment(node, MaterializeAsDefault(left));
			
			var right = VisitNode(node.Right, left.Type);
			return new ResolvedAssignmentExpressionNode(left.Type, left, op, right, null, node);
		}
		else
		{
			var left = VisitNode(node.Left, null);
			var isConjunction = op.Type == TokenType.OpAmpersandAmpersand;
			var right = isConjunction
				? VisitInScope(node.Right, GetTrueBindings(left))
				: VisitNode(node.Right, null);
			
			if (isConjunction)
				ReportRepeatedBindings(GetTrueBindings(left).Concat(GetTrueBindings(right)));
			
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
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
	
	private IResolvedExpressionNode ResolveCompoundAssignment(BinaryOpExpressionNode node, IResolvedExpressionNode left)
	{
		var candidates = _operatorRegistry.GetBinaryCandidates(node.Op.Type)
			.Where(candidate => candidate.ReturnType == left.Type && candidate.ParameterTypes[0] == left.Type)
			.ToList();
		
		var rightTypes = candidates.Select(static candidate => candidate.ParameterTypes[1]).Distinct().ToList();
		var right = VisitNode(node.Right, rightTypes.Count == 1 ? rightTypes[0] : null);
		if (AnyInvalid(left, right))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var args = new[] { left, right };
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		if (resolutionSet.Count != 1)
		{
			var diagnostic = DiagnosticReporter.ReportBinaryOpMismatch(_operatorRegistry, left, node.Op, right);
			return Error(node, diagnostic, CurrentTargetType);
		}
		
		var resolution = resolutionSet[0];
		var resolvedRight = ApplyArgumentResolution(args, resolution)[1];
		if (FindShiftRangeError(node, left.Type, resolvedRight) is { } rangeError)
			return Error(node, rangeError, CurrentTargetType);
		
		var operation = (OperationImpl)resolution.Callable;
		return new ResolvedAssignmentExpressionNode(left.Type, left, node.Op, resolvedRight, operation, node);
	}
	
	public IResolvedExpressionNode Visit(ChainedExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		var operands = new List<IResolvedExpressionNode>(node.Operands.Length);
		foreach (var operand in node.Operands)
			operands.Add(VisitNode(operand, null));
		
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
			case UntypedIntegerType u when node is ResolvedLiteralExpressionNode literal:
			{
				var value = (BigInteger)literal.Value!;
				
				// Cheapest default type that can fit the value
				var targetType = NativeSymbols.IntegerTypes
					.Where(t => FitsInType(value, t))
					.Select(t => new { Type = t, Cost = u.MaterializationCost(t, MaterializationMode.Default) })
					.Where(static x => x.Cost != int.MaxValue)
					.OrderBy(static x => x.Cost)
					.FirstOrDefault()?.Type;
				
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
		
		if (_conversionTable.FindImplicit(a, b) is not null)
			return b;
		
		if (_conversionTable.FindImplicit(b, a) is not null)
			return a;
		
		return null;
	}
	
	[return: NotNullIfNotNull(nameof(node))]
	private IResolvedExpressionNode? CoerceToType(IResolvedExpressionNode? node, TypeSymbol target)
	{
		if (node?.Type is UntypedType)
			node = MaterializeExpression(node, target);
		
		return ApplyImplicitConversion(node, target);
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
	
	private void TrackImportedFunction(FunctionInfo info)
	{
		if (!_signatures.IsLocal(info.Symbol) || info.File.Module != CurrentResolutionContext.File.Module)
			_importedFunctions.TryAdd(info.Symbol, info);
	}
	
	private ResolutionSet ResolveCallable(IEnumerable<ICallable> candidates,
		IReadOnlyList<IResolvedExpressionNode> args, MaterializationMode mode, TypeSymbol? target = null)
	{
		var options = new List<CallableResolution>();
		
		foreach (var candidate in candidates)
		{
			var parameterCount = candidate.ParameterTypes.Length;
			if (candidate.IsVariadic ? args.Count < parameterCount : args.Count != parameterCount)
				continue;
			
			var conversionCost = 0;
			var materializationCost = 0;
			var argumentConversions = new Conversion?[args.Count];
			var valid = true;
			
			for (var i = 0; i < parameterCount; i++)
			{
				var (cost, conversion) = MatchArg(args[i], candidate.ParameterTypes[i], mode);
				
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
				if (conversion is null && candidate.ReturnType != NativeSymbols.Void)
					continue;
				
				// Exact returns beat converted returns, so set to a higher cost
				resultRank = 1;
				resultCost = conversion?.Cost ?? 0;
				resultConversion = conversion;
			}
			
			var totalCost = new CallableCost(resultRank, conversionCost, materializationCost, resultCost);
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
			
			if (arg.Type is UntypedType)
				arg = MaterializeExpression(arg, target);
			
			if (arg.Type is UntypedType)
				arg = MaterializeAsDefault(arg);
			
			if (resolution.ArgumentConversions[i] is { } conversion)
				arg = new ResolvedConversionExpressionNode(arg, conversion, arg.Syntax);
			
			result.Add(arg);
		}
		
		return result;
	}
	
	private IResolvedExpressionNode PromoteVariadicArgument(IResolvedExpressionNode arg)
	{
		if (arg.Type is UntypedStringType)
			arg = MaterializeExpression(arg, NativeSymbols.CStr);
		
		if (arg.Type is UntypedType)
			arg = MaterializeAsDefault(arg);
		
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
			
			case PointerType { PointerKind: PointerKind.Unsafe }:
			case PrimitiveType { Kind: PrimitiveTypeKind.CStr }:
				return arg;
			
			default:
				return Error(arg.Syntax, $"Cannot pass type '{arg.Type.Name}' to a variadic ext function",
					NativeSymbols.Invalid);
		}
	}
	
	private uint CountBits(TypeSymbol type) => _typePool.SizeTable.GetSize(type).CountBits(_pointerBitSize);
	
	private IResolvedExpressionNode ApplyResultResolution(IResolvedExpressionNode node,
		CallableResolution resolution) => resolution.ResultConversion is { } conversion
		? new ResolvedConversionExpressionNode(node, conversion, node.Syntax)
		: node;
	
	private (int Cost, Conversion? Conversion) MatchArg(IResolvedExpressionNode arg, TypeSymbol target,
		MaterializationMode mode)
	{
		if (arg.Type == target)
			return (0, null);
		
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
		int ResultCost
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
			
			return ResultCost.CompareTo(other.ResultCost);
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
	
	private sealed class FunctionCallable(FunctionInfo info) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		public ImmutableArray<TypeSymbol> ParameterTypes => Info.Signature.ParameterTypes;
		public TypeSymbol ReturnType => Info.Signature.ReturnType;
		public bool IsVariadic => Info.Signature.IsVariadic;
	}
	
	private sealed class FunctionTypeCallable(FunctionType type) : ICallable
	{
		public ImmutableArray<TypeSymbol> ParameterTypes => type.ParameterTypes;
		public TypeSymbol ReturnType => type.ReturnType;
	}
	
	private sealed class ConstructorCallable(FunctionInfo info, TypeSymbol type) : ICallable
	{
		public FunctionInfo Info { get; } = info;
		
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } =
			info.Signature.ParameterTypes.Skip(1).ToImmutableArray(); // Skip implicit self
		
		public TypeSymbol ReturnType { get; } = type;
	}
}

public interface ICallable
{
	ImmutableArray<TypeSymbol> ParameterTypes { get; }
	TypeSymbol ReturnType { get; }
	bool IsVariadic => false;
}