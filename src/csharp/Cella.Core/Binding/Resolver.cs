using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class Resolver : ISyntaxNodeVisitor<IResolvedNode>
{
	// Used to fold unary operations on literals
	private sealed class UnaryOpJob(TokenType op)
	{
		public TokenType Op { get; } = op;
		public bool Consumed { get; set; }
	}
	
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
		(IResolvedDeclarationNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		(IResolvedStatementNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) =>
		(IResolvedExpressionNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	public IResolvedNode Visit(FieldNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var type = resolutionContext.ResolveType(node.Type);
		var field = (FieldSymbol)_symbolTable.DeclarationSymbols[node];
		IResolvedExpressionNode? initializer;
		if (node.Initializer is { } initializerNode)
		{
			_targetTypes.Push(type);
			initializer = VisitNode(initializerNode);
			_targetTypes.Pop();
			
			initializer = MaterializeAsDefault(initializer);
			initializer = CoerceToType(initializer, type);
		}
		else
			initializer = null;
		
		return new ResolvedFieldNode(field, type, initializer);
	}
	
	public IResolvedNode Visit(FileNode node)
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
		
		var result = new ResolvedFileNode(file, resolvedDeclarations, _importedFunctions.Values);
		_importedFunctions.Clear();
		return result;
	}
	
	public IResolvedNode Visit(FunctionNode node)
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
		
		return new ResolvedFunctionNode(info, body);
	}
	
	public IResolvedNode Visit(ExternalFunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var info = _assemblySignatureTable.Functions[function];
		return new ResolvedExternalFunctionNode(info);
	}
	
	public IResolvedNode Visit(GenericTypeNode node) => throw new InvalidOperationException();
	public IResolvedNode Visit(IdentifierTypeNode node) => throw new InvalidOperationException();
	public IResolvedNode Visit(ParameterNode node) => throw new InvalidOperationException();
	
	public IResolvedNode Visit(RecordNode node)
	{
		var members = new List<IResolvedDeclarationNode>();
		
		foreach (var member in node.Members)
			members.Add(VisitNode(member));
		
		var record = (RecordSymbol)_symbolTable.DeclarationSymbols[node];
		return new ResolvedRecordNode(record, members);
	}
	
	public IResolvedNode Visit(BlockStatementNode node)
	{
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		var scope = CurrentScope?.CreateChild();
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		_resolutionContexts.Push(resolutionContext);
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		_resolutionContexts.Pop();
		
		return new ResolvedBlockStatementNode(statements);
	}
	
	public IResolvedNode Visit(ReturnStatementNode node)
	{
		var returnType = _assemblySignatureTable.Functions[CurrentFunction.Symbol].Signature.ReturnType;
		_targetTypes.Push(returnType);
		var expression = CoerceToType(node.ExpressionNode is { } expr ? VisitNode(expr) : null, returnType);
		_targetTypes.Pop();
		
		return new ResolvedReturnStatementNode(expression);
	}
	
	public IResolvedNode Visit(BreakStatementNode node)
	{
		LabelSymbol? labelSymbol;
		if (node.ExpressionNode is { } expressionNode)
		{
			// TODO Better diagnostics
			if (expressionNode is not VarExpressionNode varExpr)
				throw new Exception("Expression must be a label");
			
			var name = varExpr.Identifier.Text;
			if (CurrentResolutionContext.Resolve(name) is not { } symbol)
				throw new Exception($"Symbol '{name}' not found in this scope");
			
			if (symbol is not LabelSymbol label)
				throw new Exception($"Symbol '{name}' is not a label");
			
			labelSymbol = label;
		}
		else
			labelSymbol = null;
		
		return new ResolvedBreakStatementNode(labelSymbol);
	}
	
	public IResolvedNode Visit(ContinueStatementNode node)
	{
		LabelSymbol? labelSymbol;
		if (node.ExpressionNode is { } expressionNode)
		{
			// TODO Better diagnostics
			if (expressionNode is not VarExpressionNode varExpr)
				throw new Exception("Expression must be a label");
			
			var name = varExpr.Identifier.Text;
			if (CurrentResolutionContext.Resolve(name) is not { } symbol)
				throw new Exception($"Symbol '{name}' not found in this scope");
			
			if (symbol is not LabelSymbol label)
				throw new Exception($"Symbol '{name}' is not a label");
			
			labelSymbol = label;
		}
		else
			labelSymbol = null;
		
		return new ResolvedContinueStatementNode(labelSymbol);
	}
	
	public IResolvedNode Visit(ExpressionStatementNode node) =>
		new ResolvedExpressionStatementNode(VisitNode(node.ExpressionNode));
	
	public IResolvedNode Visit(CallExpressionNode node) => TryResolveCallTargetAsType(node.Target) is { } targetType
		? VisitTypeCall(node, targetType)
		: VisitFunctionCall(node);
	
	private IResolvedNode VisitTypeCall(CallExpressionNode node, TypeSymbol targetType)
	{
		// TODO Zero-arg default construction
		// TODO Multi-arg constructors
		if (node.Arguments.Length != 1)
			throw new Exception(
				$"No constructor for type '{targetType.Name}' with {node.Arguments.Length} argument(s)");
		
		_targetTypes.Push(targetType);
		var arg = VisitNode(node.Arguments[0]);
		_targetTypes.Pop();
		
		if (arg.Type is UntypedIntegerType && targetType is IntegerType intTarget)
			arg = MaterializeExpression(arg, intTarget);
		else
			arg = MaterializeAsDefault(arg);
		
		if (arg.Type == targetType)
			return arg;
		
		if (_conversionTable.FindExplicit(arg.Type, targetType) is { } conversion)
			return new ResolvedConversionExpressionNode(arg, conversion);
		
		// TODO Look up constructors
		throw new Exception($"No conversion from '{arg.Type.Name}' to '{targetType.Name}'");
	}
	
	private ResolvedFunctionCallExpressionNode VisitFunctionCall(CallExpressionNode node)
	{
		switch (node.Target)
		{
			case VarExpressionNode varExpr:
			{
				var functionName = varExpr.Identifier.Text;
				var symbol = CurrentResolutionContext.Resolve(functionName);
				
				// TODO Diagnostics, emit invalid expression instead of throwing exceptions
				if (symbol is null)
					throw new Exception($"Symbol '{functionName}' not found in this scope");
				
				if (symbol is not FunctionSymbol function)
					throw new Exception($"Symbol '{functionName}' is not a function or type");
				
				if (!_assemblySignatureTable.Functions.TryGetValue(function, out var info))
				{
					info = _dependencySignatureTable.Functions[function];
					_importedFunctions.TryAdd(function, info);
				}
				
				var paramTypes = info.Signature.ParameterTypes;
				var args = new List<IResolvedExpressionNode>(node.Arguments.Length);
				for (var i = 0; i < node.Arguments.Length; i++)
				{
					var paramType = i < paramTypes.Length ? paramTypes[i] : null;
					_targetTypes.Push(paramType);
					var arg = VisitNode(node.Arguments[i]);
					_targetTypes.Pop();
					
					if (paramType is not null)
						arg = CoerceToType(arg, paramType);
					
					args.Add(arg);
				}
				
				return new ResolvedFunctionCallExpressionNode(info, args);
			}
			
			default:
				throw new NotImplementedException();
		}
	}
	
	private TypeSymbol? TryResolveCallTargetAsType(IExpressionNode target)
	{
		switch (target)
		{
			case VarExpressionNode e:
				return CurrentResolutionContext.Resolve(e.Identifier.Text) as TypeSymbol;
			
			case IndexerExpressionNode e:
				return TryResolveIndexerAsGenericType(e);
			
			case AccessExpressionNode:
				// TODO Module qualifiers or nested types
				return null;
			
			default:
				return null;
		}
	}
	
	private TypeSymbol? TryResolveIndexerAsGenericType(IndexerExpressionNode node)
	{
		if (node.Target is not VarExpressionNode varExpr)
		{
			// TODO AccessExpressionNode for module.GenericType[T]
			return null;
		}
		
		return TryResolveGenericTypeFromExpressions(varExpr.Identifier.Text, node.Arguments);
	}
	
	private TypeSymbol? TryResolveGenericTypeFromExpressions(string name, IReadOnlyList<IExpressionNode> arguments)
	{
		var typeArgs = new List<IGenericArgument>(arguments.Count);
		
		foreach (var arg in arguments)
		{
			if (TryResolveExpressionAsType(arg) is { } typeArg)
			{
				typeArgs.Add(new GenericTypeArgument(typeArg));
				continue;
			}
			
			if (arg is not LiteralExpressionNode { Token: { Type: TokenType.IntegerLiteral } token }
			    || !BigInteger.TryParse(token.AsSpan(), out var constVal))
				return null;
			
			typeArgs.Add(new GenericConstArgument(constVal));
		}
		
		return _typePool.ResolveBuiltinGenericType(name, typeArgs);
	}
	
	private TypeSymbol? TryResolveExpressionAsType(IExpressionNode expr) => expr switch
	{
		VarExpressionNode varExpr => CurrentResolutionContext.Resolve(varExpr.Identifier.Text) as TypeSymbol,
		IndexerExpressionNode indexerExpr => TryResolveIndexerAsGenericType(indexerExpr),
		AccessExpressionNode => null, // TODO Module-qualified or nested types
		_ => null
	};
	
	public IResolvedNode Visit(IndexerExpressionNode node)
	{
		var target = VisitNode(node.Target);
		
		// TODO Indexable user-defined types
		var elementType = target.Type switch
		{
			SpanType spanType => spanType.ElementType,
			ViewType viewType => viewType.ElementType,
			ArrayType arrayType => arrayType.ElementType,
			_ => throw new Exception($"Cannot index into type '{target.Type.Name}'")
		};
		
		if (node.Arguments.Length != 1)
			throw new Exception("Array indexer requires exactly one argument");
		
		_targetTypes.Push(NativeSymbols.UIntSize);
		var indexExpr = VisitNode(node.Arguments[0]);
		_targetTypes.Pop();
		
		return new ResolvedIndexerExpressionNode(elementType, target, indexExpr);
	}
	
	public IResolvedNode Visit(AccessExpressionNode node)
	{
		var target = VisitNode(node.Target);
		var memberName = node.Member.Text;
		var resolutionContext = CurrentResolutionContext;
		
		if (resolutionContext.TypePool.ResolveMember(target.Type, memberName) is not { } member)
			throw new Exception($"Type '{target.Type.Name}' has no member '{memberName}'");
		
		var memberType = GetMemberType(member);
		return new ResolvedAccessExpressionNode(target, member, memberType);
	}
	
	public IResolvedNode Visit(ArrayExpressionNode node)
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
			_targetTypes.Push(elementType);
			var value = VisitNode(expression);
			_targetTypes.Pop();
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
		return new ResolvedArrayExpressionNode(type, values);
	}
	
	public IResolvedNode Visit(LiteralExpressionNode node)
	{
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		var tokenType = node.Token.Type;
		switch (tokenType)
		{
			case TokenType.IntegerLiteral:
				(type, value) = ParseInteger(valueSpan, CurrentTargetType);
				break;
			
			case TokenType.KeywordNull:
				(type, value) = (NativeSymbols.VoidPtr, null);
				break;
			
			case TokenType.KeywordTrue:
				(type, value) = (NativeSymbols.Bool, true);
				break;
			
			case TokenType.KeywordFalse:
				(type, value) = (NativeSymbols.Bool, false);
				break;
			
			case TokenType.StringLiteral:
				(type, value) = ParseString(valueSpan, CurrentTargetType);
				break;
			
			case TokenType.CharLiteral:
				(type, value) = ParseChar(valueSpan);
				break;
		}
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value);
	}
	
	public IResolvedNode Visit(VarExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var varName = node.Identifier.Text;
		var symbol = resolutionContext.Resolve(varName);
		
		// TODO Diagnostics, emit invalid expression instead of throwing exceptions
		if (symbol is null)
			throw new Exception($"Symbol '{varName}' not found in this scope");
		
		switch (symbol)
		{
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type);
			
			case VariableSymbol v:
				if (!_assemblySignatureTable.VariableTypes.TryGetValue(v, out var type))
					type = _dependencySignatureTable.VariableTypes[v];
				
				return new ResolvedVarExpressionNode(v, type);
			
			default:
				throw new Exception($"Symbol '{varName}' is not a variable");
		}
	}
	
	public IResolvedNode Visit(IfStatementNode node)
	{
		_targetTypes.Push(NativeSymbols.Bool);
		var condition = VisitNode(node.Condition);
		_targetTypes.Pop();
		
		var then = VisitNode(node.Then);
		var @else = node.Else is null ? null : VisitNode(node.Else);
		
		return new ResolvedIfStatementNode(condition, then, @else);
	}
	
	public IResolvedNode Visit(VarStatementNode node)
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
			_targetTypes.Push(type);
			initializer = VisitNode(initializerNode);
			_targetTypes.Pop();
			
			initializer = MaterializeAsDefault(initializer);
			
			if (type is ArrayType a && a.Length < 0 && initializer.Type is ArrayType)
				type = initializer.Type;
			else if (type is not null)
				initializer = CoerceToType(initializer, type);
		}
		else
		{
			initializer = null;
			
			if (type is ArrayType a && a.Length < 0)
				throw new Exception("Unsized array type requires an initializer");
		}
		
		type ??= initializer?.Type ?? NativeSymbols.Invalid;
		
		var symbol = new LocalVariableSymbol(node, type);
		resolutionContext.LocalScope!.Define(symbol);
		
		return new ResolvedVarStatementNode(symbol, initializer);
	}
	
	public IResolvedNode Visit(WhileStatementNode node)
	{
		var condition = VisitNode(node.Condition);
		
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
		
		return new ResolvedWhileStatementNode(condition, body, symbol);
	}
	
	public IResolvedNode Visit(DoWhileStatementNode node)
	{
		var condition = VisitNode(node.Condition);
		
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
		
		return new ResolvedDoWhileStatementNode(body, condition, symbol);
	}
	
	public IResolvedNode Visit(RepeatStatementNode node)
	{
		var count = VisitNode(node.Count);
		
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
		
		return new ResolvedRepeatStatementNode(count, body, symbol);
	}
	
	public IResolvedNode Visit(LoopStatementNode node)
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
		
		return new ResolvedLoopStatementNode(body, symbol);
	}
	
	public IResolvedNode Visit(UnaryOpExpressionNode node)
	{
		var op = node.Op;
		
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		_targetTypes.Push(null);
		_unaryOpJobs.Push(new(op.Type));
		var operand = VisitNode(node.Operand);
		var consumed = _unaryOpJobs.Pop().Consumed;
		_targetTypes.Pop();
		
		if (consumed)
			return operand;
		
		var operation = _operatorRegistry.ResolveUnary(op.Type, operand.Type);
		return new ResolvedUnaryOpExpressionNode(operand, operation);
	}
	
	public IResolvedNode Visit(BinaryOpExpressionNode node)
	{
		var op = node.Op;
		var isAssignment = op.Type == TokenType.OpEqual || op.Type == TokenType.OpPlusEqual ||
		                   op.Type == TokenType.OpMinusEqual || op.Type == TokenType.OpStarEqual ||
		                   op.Type == TokenType.OpSlashEqual || op.Type == TokenType.OpPercentEqual ||
		                   op.Type == TokenType.OpAmpersandEqual || op.Type == TokenType.OpBarEqual ||
		                   op.Type == TokenType.OpHatEqual;
		
		_targetTypes.Push(null);
		if (isAssignment)
		{
			var left = VisitNode(node.Left);
			_targetTypes.Pop();
			_targetTypes.Push(left.Type);
			var right = CoerceToType(VisitNode(node.Right), left.Type);
			_targetTypes.Pop();
			
			return new ResolvedAssignmentExpressionNode(left.Type, left, op, right);
		}
		else
		{
			// We push null to allow sub-expressions to resolve naturally
			// Then, we attempt to implicit cast to actual type
			var left = VisitNode(node.Left);
			var right = VisitNode(node.Right);
			_targetTypes.Pop();
			
			left = MaterializeWithPeer(left, right.Type);
			right = MaterializeWithPeer(right, left.Type);
			
			var resolutionSet = _operatorRegistry.ResolveBinary(left.Type, op.Type, right.Type);
			if (resolutionSet.IsAmbiguous)
				throw new Exception(
					$"Ambiguous operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'");
			
			if (!resolutionSet.HasResult)
				return new ResolvedBinaryOpExpressionNode(left, right, null);
			
			var resolution = resolutionSet[0];
			var operation = resolution.Operation;
			
			// If result is concrete but children are untyped, resolve them as default types and re-resolve
			if (operation.Result is not UntypedType)
			{
				if (left.Type is UntypedType)
					left = MaterializeAsDefault(left);
				
				if (right.Type is UntypedType)
					right = MaterializeAsDefault(right);
				
				resolutionSet = _operatorRegistry.ResolveBinary(left.Type, op.Type, right.Type);
				if (resolutionSet.IsAmbiguous)
					throw new Exception(
						$"Ambiguous operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'");
				
				if (!resolutionSet.HasResult)
					return new ResolvedBinaryOpExpressionNode(left, right, null);
				
				resolution = resolutionSet[0];
				operation = resolution.Operation;
			}
			
			if (resolution.LeftConversion is { } leftConversion)
				left = new ResolvedConversionExpressionNode(left, leftConversion);
			
			if (resolution.RightConversion is { } rightConversion)
				right = new ResolvedConversionExpressionNode(right, rightConversion);
			
			return new ResolvedBinaryOpExpressionNode(left, right, operation);
		}
	}
	
	public IResolvedNode Visit(ChainedExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		var operands = new List<IResolvedExpressionNode>(node.Operands.Length);
		_targetTypes.Push(null);
		
		foreach (var operand in node.Operands)
			operands.Add(VisitNode(operand));
		
		_targetTypes.Pop();
		
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
		
		TypeSymbol? prevType = null;
		var allSameType = true;
		for (var i = 0; i < operands.Count; i++)
		{
			var newOp = MaterializeAsDefault(operands[i]);
			operands[i] = newOp;
			
			if (i > 0 && prevType != newOp.Type)
				allSameType = false;
			
			prevType = newOp.Type;
		}
		
		if (!allSameType)
		{
			var commonType = operands[0].Type;
			for (var i = 1; i < operands.Count && commonType is not null; i++)
				commonType = FindCommonType(commonType, operands[i].Type);
			
			if (commonType is null)
				throw new Exception("Cannot chain comparisons between incompatible types");
			
			for (var i = 0; i < operands.Count; i++)
				operands[i] = CoerceToType(operands[i], commonType);
		}
		
		var operations = new List<OperationImpl?>(node.Ops.Length);
		for (var i = 0; i < operands.Count - 1; i++)
		{
			var left = operands[i];
			var op = node.Ops[i];
			var right = operands[i + 1];
			
			var resolutionSet = _operatorRegistry.ResolveBinary(left.Type, op.Type, right.Type);
			if (resolutionSet.IsAmbiguous)
				throw new Exception(
					$"Ambiguous operation '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'");
			
			operations.Add(resolutionSet.HasResult ? resolutionSet[0].Operation : null);
		}
		
		// Aggregate types with implicit AND
		var resultType = operations[0]?.Result ?? NativeSymbols.Invalid;
		for (var i = 1; i < operations.Count; i++)
		{
			var right = operations[i]?.Result ?? NativeSymbols.Invalid;
			var resolutionSet = _operatorRegistry.ResolveBinary(resultType, TokenType.OpAmpersand, right);
			if (resolutionSet.IsAmbiguous)
				throw new Exception($"Ambiguous operation '&' between '{resultType.Name}' and '{right.Name}'");
			
			resultType = resolutionSet.HasResult ? resolutionSet[0].Operation.Result : NativeSymbols.Invalid;
		}
		
		return new ResolvedChainedExpressionNode(resultType, operands, operations);
	}
	
	[return: NotNullIfNotNull(nameof(source))]
	private IResolvedExpressionNode? ApplyImplicitConversion(IResolvedExpressionNode? source, TypeSymbol target)
	{
		if (source is null)
			return null;
		
		if (source.Type == target)
			return source;
		
		if (_conversionTable.FindImplicit(source.Type, target) is { } conversion)
			return new ResolvedConversionExpressionNode(source, conversion);
		
		// TODO Diagnostic
		return source;
	}
	
	private (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span, TypeSymbol? targetType)
	{
		// TODO Check suffixes
		
		// Consume unary minus jobs
		if (_unaryOpJobs.TryPeek(out var job) && job.Op == TokenType.OpMinus)
		{
			// We have to allocate a new string
			Span<char> newSpan = new char[span.Length + 1];
			newSpan[0] = '-';
			span.CopyTo(newSpan[1..]);
			span = newSpan;
			job.Consumed = true;
		}
		
		if (targetType is PrimitiveType primitiveType)
			switch (primitiveType.Kind)
			{
				case PrimitiveTypeKind.Int8:
					if (sbyte.TryParse(span, out var sbyteValue))
						return (NativeSymbols.Int8, sbyteValue);
					
					break;
				
				case PrimitiveTypeKind.Int16:
					if (short.TryParse(span, out var shortValue))
						return (NativeSymbols.Int16, shortValue);
					
					break;
				
				case PrimitiveTypeKind.Int32:
					if (int.TryParse(span, out var intValue))
						return (NativeSymbols.Int32, intValue);
					
					break;
				
				case PrimitiveTypeKind.Int64:
					if (long.TryParse(span, out var longValue))
						return (NativeSymbols.Int64, longValue);
					
					break;
				
				case PrimitiveTypeKind.Int128:
					if (Int128.TryParse(span, out var int128Value))
						return (NativeSymbols.Int128, int128Value);
					
					break;
				
				case PrimitiveTypeKind.IntSize:
					if (BigInteger.TryParse(span, out var intSizeValue) &&
					    intSizeValue.GetBitLength() < _pointerBitSize)
						return (NativeSymbols.IntSize, intSizeValue);
					
					break;
				
				case PrimitiveTypeKind.UInt8:
					if (byte.TryParse(span, out var byteValue))
						return (NativeSymbols.UInt8, byteValue);
					
					break;
				
				case PrimitiveTypeKind.UInt16:
					if (ushort.TryParse(span, out var ushortValue))
						return (NativeSymbols.UInt16, ushortValue);
					
					break;
				
				case PrimitiveTypeKind.UInt32:
					if (uint.TryParse(span, out var uintValue))
						return (NativeSymbols.UInt32, uintValue);
					
					break;
				
				case PrimitiveTypeKind.UInt64:
					if (ulong.TryParse(span, out var ulongValue))
						return (NativeSymbols.UInt64, ulongValue);
					
					break;
				
				case PrimitiveTypeKind.UInt128:
					if (UInt128.TryParse(span, out var uint128Value))
						return (NativeSymbols.UInt128, uint128Value);
					
					break;
				
				case PrimitiveTypeKind.UIntSize:
					if (BigInteger.TryParse(span, out var uintSizeValue) &&
					    uintSizeValue.GetBitLength() < _pointerBitSize)
						return (NativeSymbols.UIntSize, uintSizeValue);
					
					break;
			}
		
		if (BigInteger.TryParse(span, out var untypedValue))
			return (NativeSymbols.UntypedInteger, untypedValue);
		
		return (null, null);
	}
	
	private (TypeSymbol type, uint value) ParseChar(ReadOnlySpan<char> span) => (NativeSymbols.Char, span[0]);
	
	private (TypeSymbol? type, object? value) ParseString(ReadOnlySpan<char> span, TypeSymbol? targetType)
	{
		// TODO Check prefixes/suffixes
		
		if (targetType is PrimitiveType primitiveType)
			switch (primitiveType.Kind)
			{
				case PrimitiveTypeKind.CStr:
					return ParseCStr(span);
			}
		
		return ParseStr(span);
		
		static (TypeSymbol? type, object? value) ParseStr(ReadOnlySpan<char> span)
		{
			var byteCount = Encoding.UTF8.GetByteCount(span);
			var bytes = new byte[byteCount];
			Encoding.UTF8.GetBytes(span, bytes);
			var charCount = (ulong)Encoding.UTF8.GetCharCount(bytes);
			return (NativeSymbols.Str, new StrValue(charCount, bytes));
		}
		
		static (TypeSymbol? type, object? value) ParseCStr(ReadOnlySpan<char> span)
		{
			var byteCount = Encoding.UTF8.GetByteCount(span);
			var result = new byte[byteCount + 1];
			Encoding.UTF8.GetBytes(span, result);
			return (NativeSymbols.CStr, result);
		}
	}
	
	private IResolvedExpressionNode MaterializeWithPeer(IResolvedExpressionNode operand, TypeSymbol peerType)
	{
		// Only care about untyped integer literals
		if (operand is not ResolvedLiteralExpressionNode { Type: UntypedIntegerType } literal)
			return operand;
		
		return peerType switch
		{
			UntypedIntegerType => operand,
			IntegerType intType when literal.Value is BigInteger value && FitsInType(value, intType) =>
				MaterializeLiteral(intType, value),
			_ => MaterializeAsDefault(literal)
		};
	}
	
	private IResolvedExpressionNode MaterializeAsDefault(IResolvedExpressionNode node)
	{
		if (node.Type is not UntypedIntegerType)
			return node;
		
		if (node is not ResolvedLiteralExpressionNode { Value: BigInteger value })
			return MaterializeExpression(node, NativeSymbols.Int32);
		
		var targetType = SmallestFittingType(value) ?? throw new InvalidOperationException();
		return MaterializeLiteral(targetType, value);
	}
	
	private ResolvedLiteralExpressionNode MaterializeLiteral(IntegerType type, BigInteger value) =>
		new(type, ConvertInteger(value, type));
	
	private static object? ConvertInteger(BigInteger value, IntegerType type) => type.Kind switch
	{
		PrimitiveTypeKind.Int8 => (sbyte)value,
		PrimitiveTypeKind.Int16 => (short)value,
		PrimitiveTypeKind.Int32 => (int)value,
		PrimitiveTypeKind.Int64 => (long)value,
		PrimitiveTypeKind.Int128 => (Int128)value,
		PrimitiveTypeKind.IntSize => (object?)value,
		PrimitiveTypeKind.UInt8 => (byte)value,
		PrimitiveTypeKind.UInt16 => (ushort)value,
		PrimitiveTypeKind.UInt32 => (uint)value,
		PrimitiveTypeKind.UInt64 => (ulong)value,
		PrimitiveTypeKind.UInt128 => (UInt128)value,
		PrimitiveTypeKind.UIntSize => value,
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
		_ => false
	};
	
	private IResolvedExpressionNode MaterializeExpression(IResolvedExpressionNode node, IntegerType target)
	{
		if (node.Type is not UntypedIntegerType)
			return node;
		
		switch (node)
		{
			case ResolvedLiteralExpressionNode { Value: BigInteger value } literal:
			{
				if (FitsInType(value, target))
					return MaterializeLiteral(target, value);
				
				var fallback = SmallestFittingType(value);
				return fallback is not null
					? MaterializeLiteral(fallback, value)
					: literal; // TODO Diagnostic: Too large for any integer type
			}
			
			case ResolvedUnaryOpExpressionNode unary:
			{
				var operand = MaterializeExpression(unary.Operand, target);
				var resolution = unary.Operation?.Op is { } op
					? _operatorRegistry.ResolveUnary(op, operand.Type)
					: null;
				
				return new ResolvedUnaryOpExpressionNode(operand, resolution);
			}
			
			case ResolvedBinaryOpExpressionNode binary:
			{
				var left = MaterializeExpression(binary.Left, target);
				var right = MaterializeExpression(binary.Right, target);
				
				if (left.Type != right.Type)
				{
					// Try widening the narrower side
					if (FindCommonType(left.Type, right.Type) is { } common)
					{
						left = CoerceToType(left, common);
						right = CoerceToType(right, common);
					}
				}
				
				var resolutionSet = binary.Operation?.Op is { } op
					? _operatorRegistry.ResolveBinary(left.Type, op, right.Type)
					: BinaryResolutionSet.None;
				
				if (resolutionSet.IsAmbiguous)
					throw new Exception($"Ambiguous operation '{binary.Operation!.Op!.Representation}' between " +
					                    $"'{left.Type.Name}' and '{right.Type.Name}'");
				
				if (!resolutionSet.HasResult)
					return new ResolvedBinaryOpExpressionNode(left, right, null);
				
				var resolution = resolutionSet[0];
				
				if (resolution.LeftConversion is { } leftConversion)
					left = new ResolvedConversionExpressionNode(left, leftConversion);
				
				if (resolution.RightConversion is { } rightConversion)
					right = new ResolvedConversionExpressionNode(right, rightConversion);
				
				return new ResolvedBinaryOpExpressionNode(left, right, resolution.Operation);
			}
			
			default:
				return node;
		}
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
		
		if (_conversionTable.FindImplicit(a, b) is not null)
			return b;
		
		if (_conversionTable.FindImplicit(b, a) is not null)
			return a;
		
		return null;
	}
	
	[return: NotNullIfNotNull(nameof(node))]
	private IResolvedExpressionNode? CoerceToType(IResolvedExpressionNode? node, TypeSymbol target)
	{
		if (node?.Type is UntypedIntegerType && target is IntegerType intTarget)
			node = MaterializeExpression(node, intTarget);
		
		return ApplyImplicitConversion(node, target);
	}
	
	private TypeSymbol GetMemberType(MemberSymbol member) =>
		_typePool.TryGetTypeOfMember(member, out var type) ? type : NativeSymbols.Invalid;
}