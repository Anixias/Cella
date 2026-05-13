using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class Resolver : IStatementNodeVisitor<IResolvedStatementNode>,
	IExpressionNodeVisitor<IResolvedExpressionNode>, IDeclarationNodeVisitor<IResolvedDeclarationNode>
{
	// Used to fold unary operations on literals
	private sealed class UnaryOpJob(TokenType op)
	{
		public TokenType Op { get; } = op;
		public bool Consumed { get; set; }
	}
	
	public DiagnosticList Diagnostics { get; } = new();
	
	private readonly SymbolTable _symbolTable;
	private readonly SignatureTable _assemblySignatureTable;
	private readonly SignatureTable _dependencySignatureTable;
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
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private FunctionInfo CurrentFunction => CurrentResolutionContext.ContainingFunction!.Value;
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	private TypeSymbol? CurrentTargetType => _targetTypes.TryPeek(out var result) ? result : null;
	
	public Resolver(AssemblySymbol assemblySymbol, IEnumerable<AssemblySymbol> dependencies, TypePool typePool,
		uint pointerBitSize)
	{
		_typePool = typePool;
		_conversionTable = typePool.ConversionTable;
		_operatorRegistry = typePool.OperatorRegistry;
		_pointerBitSize = pointerBitSize;
		_symbolTable = assemblySymbol.SymbolTable;
		_assemblySignatureTable = assemblySymbol.SignatureTable;
		_dependencySignatureTable = SignatureTable.Combine(dependencies.Select(static a => a.SignatureTable));
		
		var ptrBits = (int)pointerBitSize;
		_isizeMinValue = -BigInteger.Pow(2, ptrBits - 1);
		_isizeMaxValue = BigInteger.Pow(2, ptrBits - 1) - 1;
		_usizeMinValue = BigInteger.Zero;
		_usizeMaxValue = BigInteger.Pow(2, ptrBits) - 1;
	}
	
	public ResolvedFileNode Resolve(FileNode root) => (ResolvedFileNode)Visit(root);
	
	private IResolvedDeclarationNode VisitNode(IDeclarationNode node) =>
		((IDeclarationNodeVisitor<IResolvedDeclarationNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		((IStatementNodeVisitor<IResolvedStatementNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) =>
		((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node, TypeSymbol? targetType)
	{
		_targetTypes.Push(targetType);
		try
		{
			var result = VisitNode(node);
			return targetType is null
				? result
				: CoerceToType(result, targetType);
		}
		finally
		{
			_targetTypes.Pop();
		}
	}
	
	public IResolvedDeclarationNode Visit(FieldNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var type = resolutionContext.ResolveType(node.Type);
		var field = (FieldSymbol)_symbolTable.DeclarationSymbols[node];
		IResolvedExpressionNode? initializer;
		if (node.Initializer is { } initializerNode)
			initializer = VisitNode(initializerNode, type);
		else
			initializer = null;
		
		return new ResolvedFieldNode(field, type, initializer, node);
	}
	
	public IResolvedDeclarationNode Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = _assemblySignatureTable.ImportEnvironments[file];
		
		var resolutionContext = new ResolutionContext
		{
			File = file,
			Imports = imports,
			TypePool = _typePool
		};
		
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
		var info = _assemblySignatureTable.Functions[function];
		
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
		var info = _assemblySignatureTable.Functions[function];
		
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
		var info = _assemblySignatureTable.Functions[function];
		return new ResolvedExternalFunctionNode(info, node);
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
	
	public IResolvedStatementNode Visit(BlockStatementNode node)
	{
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		var scope = CurrentScope?.CreateChild();
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		_resolutionContexts.Push(resolutionContext);
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		_resolutionContexts.Pop();
		
		return new ResolvedBlockStatementNode(statements, node);
	}
	
	public IResolvedStatementNode Visit(ReturnStatementNode node)
	{
		var returnType = _assemblySignatureTable.Functions[CurrentFunction.Symbol].Signature.ReturnType;
		var expression = node.ExpressionNode is { } expr ? VisitNode(expr, returnType) : null;
		return new ResolvedReturnStatementNode(expression, node);
	}
	
	public IResolvedStatementNode Visit(BreakStatementNode node)
	{
		if (node.ExpressionNode is not { } expressionNode)
			return new ResolvedBreakStatementNode(null, node);
		
		if (expressionNode is not VarExpressionNode varExpr)
			return Error(node, "Expression must be a label", expressionNode);
		
		var name = varExpr.Identifier.Text;
		if (CurrentResolutionContext.Resolve(name) is not { } symbol)
		{
			var diagnostic = DiagnosticReporter.ReportUndefinedSymbol(node, name, GetVisibleSymbolNames());
			return Error(node, diagnostic);
		}
		
		if (symbol is not LabelSymbol label)
			return Error(node, $"Symbol '{name}' is not a label", varExpr);
		
		return new ResolvedBreakStatementNode(label, node);
	}
	
	public IResolvedStatementNode Visit(ContinueStatementNode node)
	{
		if (node.ExpressionNode is not { } expressionNode)
			return new ResolvedContinueStatementNode(null, node);
		
		if (expressionNode is not VarExpressionNode varExpr)
			return Error(node, "Expression must be a label", expressionNode);
		
		var name = varExpr.Identifier.Text;
		if (CurrentResolutionContext.Resolve(name) is not { } symbol)
		{
			var diagnostic = DiagnosticReporter.ReportUndefinedSymbol(node, name, GetVisibleSymbolNames());
			return Error(node, diagnostic);
		}
		
		if (symbol is not LabelSymbol label)
			return Error(node, $"Symbol '{name}' is not a label", varExpr);
		
		return new ResolvedContinueStatementNode(label, node);
	}
	
	public IResolvedStatementNode Visit(ExpressionStatementNode node) =>
		new ResolvedExpressionStatementNode(VisitNode(node.ExpressionNode), node);
	
	public IResolvedExpressionNode Visit(CallExpressionNode node) =>
		_typePool.TryResolveExpressionAsType(node.Target, CurrentResolutionContext.Resolve) is { } targetType
			? VisitTypeCall(node, targetType)
			: VisitFunctionCall(node);
	
	private IResolvedExpressionNode VisitTypeCall(CallExpressionNode node, TypeSymbol targetType)
	{
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
		if (node.Target is not VarExpressionNode varExpr)
			throw new NotImplementedException();
		
		var functionName = varExpr.Identifier.Text;
		var symbol = CurrentResolutionContext.Resolve(functionName);
		
		if (symbol is null)
		{
			var diagnostic = DiagnosticReporter.ReportUndefinedSymbol(node, functionName, GetVisibleSymbolNames());
			return Error(node, diagnostic, CurrentTargetType);
		}
		
		var functionSymbols = symbol switch
		{
			FunctionSymbol f => [f],
			AmbiguousSymbol a => a.Candidates.OfType<FunctionSymbol>().ToArray(),
			_ => []
		};
		
		if (functionSymbols.Length == 0)
			return Error(node, $"Symbol '{functionName}' is not a function or type", CurrentTargetType, varExpr);
		
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
			return Error(node, $"Call to '{functionName}' is ambiguous", CurrentTargetType, varExpr);
		
		// TODO If only one candidate, we could report the unmatched arguments instead of the whole function?
		if (!resolutionSet.HasResult)
			return Error(node, $"No overload of '{functionName}' accepts these arguments", CurrentTargetType, varExpr);
		
		var resolution = resolutionSet[0];
		var callable = (FunctionCallable)resolution.Callable;
		var info = callable.Info;
		
		TrackImportedFunction(info);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		var result = new ResolvedFunctionCallExpressionNode(info, resolvedArgs, node);
		return ApplyResultResolution(result, resolution);
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
		var target = VisitNode(node.Target);
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
			
			elementType = values[0].Type;
			for (var i = 1; i < values.Count; i++)
			{
				elementType = FindCommonType(elementType, values[i].Type);
				if (elementType is not null)
					continue;
				
				// TODO Diagnostic: incompatible element types
				elementType = NativeSymbols.Invalid;
				break;
			}
			
			for (var i = 0; i < values.Count; i++)
				values[i] = CoerceToType(values[i], elementType);
		}
		
		var type = _typePool.GetArrayType(elementType, node.Values.Length);
		return new ResolvedArrayExpressionNode(type, values, node);
	}
	
	public IResolvedExpressionNode Visit(LiteralExpressionNode node)
	{
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		var tokenType = node.Token.Type;
		(type, value) = tokenType switch
		{
			TokenType.IntegerLiteral => ParseInteger(valueSpan),
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
		var target = _typePool.TryResolveExpressionAsType(node.Expression, CurrentResolutionContext.Resolve)
		             ?? VisitNode(node.Expression).Type;
		
		if (IsInvalid(target))
			return new ResolvedInvalidExpressionNode(node);
		
		var sizeInBits = _typePool.SizeTable.GetSize(target).CountBits(_pointerBitSize);
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
		
		switch (symbol)
		{
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type, node);
			
			case VariableSymbol v:
				if (!_assemblySignatureTable.VariableTypes.TryGetValue(v, out var type))
					type = _dependencySignatureTable.VariableTypes[v];
				
				return new ResolvedVarExpressionNode(v, type, node);
			
			default:
				return Error(node, $"Symbol '{varName}' is not a variable", CurrentTargetType);
		}
	}
	
	public IResolvedStatementNode Visit(IfStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		var then = VisitNode(node.Then);
		var @else = node.Else is null ? null : VisitNode(node.Else);
		
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
		
		var symbol = new LocalVariableSymbol(node, type);
		resolutionContext.LocalScope!.Define(symbol);
		
		return new ResolvedVarStatementNode(symbol, initializer, node);
	}
	
	public IResolvedStatementNode Visit(WhileStatementNode node)
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
		_ => IsInvalid(operand.Type)
			? new ResolvedUnaryOpExpressionNode(operand, new NativeImpl(opType, CurrentTargetType ??
				NativeSymbols.Invalid), node)
			: Error(node, $"Cannot dereference type '{operand.Type.Name}'", CurrentTargetType)
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
		if (IsInvalid(operand.Type) || operand.Type is not PointerType { PointerKind: PointerKind.Owning } ptrType)
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
			or TokenType.OpBarEqual or TokenType.OpHatEqual;
		
		if (isAssignment)
		{
			var left = VisitNode(node.Left, null);
			var right = VisitNode(node.Right, left.Type);
			return new ResolvedAssignmentExpressionNode(left.Type, left, op, right, node);
		}
		else
		{
			var left = VisitNode(node.Left, null);
			var right = VisitNode(node.Right, null);
			
			if (AnyInvalid(left, right))
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			
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
			
			var result = new ResolvedBinaryOpExpressionNode(resolvedArgs[0], resolvedArgs[1], operation, node);
			return ApplyResultResolution(result, resolution);
		}
	}
	
	public IResolvedExpressionNode Visit(ChainedExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		var operands = new List<IResolvedExpressionNode>(node.Operands.Length);
		foreach (var operand in node.Operands)
			operands.Add(VisitNode(operand, null));
		
		// Materialize untyped integer operands left to right
		for (var i = 0; i < operands.Count - 1; i++)
		{
			operands[i] = MaterializeWithPeer(operands[i], operands[i + 1].Type);
			operands[i + 1] = MaterializeWithPeer(operands[i + 1], operands[i].Type);
		}
		
		// Propagate materialized operands back right to left
		for (var i = operands.Count - 1; i > 0; i--)
		{
			operands[i] = MaterializeWithPeer(operands[i], operands[i - 1].Type);
			operands[i - 1] = MaterializeWithPeer(operands[i - 1], operands[i].Type);
		}
		
		for (var i = 0; i < operands.Count - 1; i++)
			operands[i] = MaterializeAsDefault(operands[i]);
		
		// TODO Do we need common types anymore?
		var commonType = operands[0].Type;
		for (var i = 1; i < operands.Count && commonType is not null; i++)
			commonType = FindCommonType(commonType, operands[i].Type);
		
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
		
		return Error(source.Syntax, $"Cannot convert type '{source.Type.Name}' to '{target.Name}'", target);
	}
	
	private (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span)
	{
		// TODO Check suffixes
		
		// Consume unary minus jobs
		if (_unaryOpJobs.TryPeek(out var job) && job is { Op: TokenType.OpMinus, Consumed: false })
		{
			// We have to allocate a new string
			Span<char> newSpan = new char[span.Length + 1];
			newSpan[0] = '-';
			span.CopyTo(newSpan[1..]);
			span = newSpan;
			job.Consumed = true;
		}
		
		if (BigInteger.TryParse(span, out var untypedValue))
			return (NativeSymbols.UntypedInteger, untypedValue);
		
		return (null, null);
	}
	
	private (TypeSymbol type, uint value) ParseChar(ReadOnlySpan<char> span) =>
		(NativeSymbols.Char, span[0]);
	
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
			
			case UntypedNullType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeNull(literal, NativeSymbols.VoidPtr);
			
			case UntypedStringType when node is ResolvedLiteralExpressionNode literal:
				return MaterializeStr(literal);
			
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
		if (node is not ResolvedLiteralExpressionNode literal)
			return node;
		
		return node.Type switch
		{
			UntypedIntegerType when target is IntegerType t => MaterializeInteger(literal, t),
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
		var charCount = (ulong)Encoding.UTF8.GetCharCount(bytes);
		return new ResolvedLiteralExpressionNode(NativeSymbols.Str, new StrValue(charCount, bytes), node.Syntax);
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
	
	private TypeSymbol? FindCommonType(TypeSymbol a, TypeSymbol b)
	{
		if (a == b)
			return a;
		
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
	
	private ResolvedInvalidStatementNode Error(IStatementNode node, Diagnostic diagnostic)
	{
		Diagnostics.Add(diagnostic);
		return new(node);
	}
	
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
	
	private FunctionInfo GetFunctionInfo(FunctionSymbol function) =>
		_assemblySignatureTable.Functions.TryGetValue(function, out var info)
			? info
			: _dependencySignatureTable.Functions[function];
	
	private void TrackImportedFunction(FunctionInfo info)
	{
		if (!_assemblySignatureTable.Functions.ContainsKey(info.Symbol) ||
		    info.File.Module != CurrentResolutionContext.File.Module)
			_importedFunctions.TryAdd(info.Symbol, info);
	}
	
	private ResolutionSet ResolveCallable(IEnumerable<ICallable> candidates,
		IReadOnlyList<IResolvedExpressionNode> args, MaterializationMode mode, TypeSymbol? target = null)
	{
		var options = new List<CallableResolution>();
		
		foreach (var candidate in candidates)
		{
			if (candidate.ParameterTypes.Length != args.Count)
				continue;
			
			var argumentCost = 0;
			var argumentConversions = new Conversion?[args.Count];
			var valid = true;
			
			for (var i = 0; i < args.Count; i++)
			{
				var (cost, conversion) = MatchArg(args[i], candidate.ParameterTypes[i], mode);
				
				if (cost == int.MaxValue)
				{
					valid = false;
					break;
				}
				
				argumentCost += cost;
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
				if (conversion is null)
					continue;
				
				// Exact returns beat converted returns, so set to a higher cost
				resultRank = 1;
				resultCost = conversion.Cost;
				resultConversion = conversion;
			}
			
			var totalCost = new CallableCost(resultRank, argumentCost, resultCost);
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
			// Special case for integer literals: If target type cannot store the value, conversion is impossible
			if (target is IntegerType i && u is UntypedIntegerType && arg is ResolvedLiteralExpressionNode literal)
			{
				var value = (BigInteger)literal.Value!;
				if (!FitsInType(value, i))
					return (int.MaxValue, null);
			}
			
			var cost = u.MaterializationCost(target, mode);
			return (cost, null);
		}
		
		var conversion = _conversionTable.FindImplicit(arg.Type, target);
		return conversion is null ? (Cost: int.MaxValue, null) : (conversion.Cost, conversion);
	}
	
	private readonly record struct CallableResolution
	(
		ICallable Callable,
		CallableCost Cost,
		Conversion? ResultConversion,
		Conversion?[] ArgumentConversions
	);
	
	private readonly record struct CallableCost(int ResultRank, int ArgumentCost, int ResultCost)
		: IComparable<CallableCost>
	{
		public int CompareTo(CallableCost other)
		{
			var resultRankComparison = ResultRank.CompareTo(other.ResultRank);
			if (resultRankComparison != 0)
				return resultRankComparison;
			
			var argumentCostComparison = ArgumentCost.CompareTo(other.ArgumentCost);
			if (argumentCostComparison != 0)
				return argumentCostComparison;
			
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
}
