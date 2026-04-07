using System.Text;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class Resolver : ISyntaxNodeVisitor<IResolvedNode>
{
	private readonly Dictionary<FunctionSymbol, FunctionInfo> _importedFunctions = [];
	private readonly Stack<TypeSymbol?> _targetTypes = [];
	private readonly SymbolTable _symbolTable;
	private readonly SignatureTable _assemblySignatureTable;
	private readonly SignatureTable _dependencySignatureTable;
	private readonly Stack<ResolutionContext> _resolutionContexts = [];
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private FunctionInfo CurrentFunction => CurrentResolutionContext.ContainingFunction!.Value;
	private Scope? CurrentScope => CurrentResolutionContext.LocalScope;
	private TypeSymbol? CurrentTargetType => _targetTypes.TryPeek(out var result) ? result : null;
	
	public Resolver(AssemblySymbol assemblySymbol, IEnumerable<AssemblySymbol> dependencies)
	{
		_symbolTable = assemblySymbol.SymbolTable;
		_assemblySignatureTable = assemblySymbol.SignatureTable;
		_dependencySignatureTable = SignatureTable.Combine(dependencies.Select(static a => a.SignatureTable));
	}
	
	public ResolvedFileNode Resolve(FileNode root) => (ResolvedFileNode)Visit(root);
	
	private IResolvedDeclarationNode VisitNode(IDeclarationNode node) =>
		(IResolvedDeclarationNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		(IResolvedStatementNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) =>
		(IResolvedExpressionNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	public IResolvedNode Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = _assemblySignatureTable.ImportEnvironments[file];
		
		var resolutionContext = new ResolutionContext
		{
			File = file,
			Imports = imports
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
	
	public IResolvedNode Visit(ParameterNode node) => throw new InvalidOperationException();
	
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
		var info = _assemblySignatureTable.Functions[CurrentFunction.Symbol];
		_targetTypes.Push(info.Signature.ReturnType);
		
		var result = new ResolvedReturnStatementNode(node.ExpressionNode is { } expressionNode
			? VisitNode(expressionNode)
			: null);
		
		_targetTypes.Pop();
		return result;
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
	
	public IResolvedNode Visit(CallExpressionNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		var functionName = node.Identifier.Text;
		var symbol = resolutionContext.Resolve(functionName);
		
		// TODO Diagnostics, emit invalid expression instead of throwing exceptions
		if (symbol is null)
			throw new Exception($"Symbol '{functionName}' not found in this scope");
		
		if (symbol is not FunctionSymbol function)
			throw new Exception($"Symbol '{functionName}' is not a function");
		
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
			args.Add(VisitNode(node.Arguments[i]));
			_targetTypes.Pop();
		}
		
		return new ResolvedFunctionCallExpressionNode(info, args);
	}
	
	public IResolvedNode Visit(LiteralExpressionNode node)
	{
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		var tokenType = node.Token.Type;
		if (tokenType == TokenType.IntegerLiteral)
			(type, value) = ParseInteger(valueSpan, CurrentTargetType);
		else if (tokenType == TokenType.KeywordTrue)
			(type, value) = (NativeSymbols.Bool, true);
		else if (tokenType == TokenType.KeywordFalse)
			(type, value) = (NativeSymbols.Bool, false);
		else if (tokenType == TokenType.StringLiteral)
			(type, value) = ParseString(valueSpan, CurrentTargetType);
		
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
			type = resolutionContext.Resolve(specifiedType.Text) as TypeSymbol;
		else
			type = null;
		
		IResolvedExpressionNode? initializer;
		if (node.ExpressionNode is { } initializerNode)
		{
			_targetTypes.Push(type);
			initializer = VisitNode(initializerNode);
			_targetTypes.Pop();
		}
		else
			initializer = null;
		
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
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		_targetTypes.Push(null);
		var operand = VisitNode(node.Operand);
		_targetTypes.Pop();
		
		var op = node.Op;
		
		if (NativeOperations.Resolve(op.Type, operand.Type) is { } nativeType)
		{
			// We optimize away identity operations if it's a native type since it's a no-op
			if (op.Type == TokenType.OpPlus)
				return operand;
			
			return new ResolvedUnaryOpExpressionNode(nativeType, op, operand);
		}
		
		// TODO Based on type of sub-expression and the op token, we search for operator overloads
		
		return new ResolvedUnaryOpExpressionNode(NativeSymbols.Invalid, op, operand);
	}
	
	public IResolvedNode Visit(BinaryOpExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		_targetTypes.Push(null);
		var left = VisitNode(node.Left);
		var right = VisitNode(node.Right);
		_targetTypes.Pop();
		
		var op = node.Op;
		var isAssignment = op.Type == TokenType.OpEqual || op.Type == TokenType.OpPlusEqual ||
		                   op.Type == TokenType.OpMinusEqual || op.Type == TokenType.OpStarEqual ||
		                   op.Type == TokenType.OpSlashEqual;
		
		if (isAssignment)
			return new ResolvedAssignmentExpressionNode(left.Type, left, op, right);
		
		// TODO How to handle implicit upcasts..?
		if (NativeOperations.Resolve(left.Type, op.Type, right.Type) is { } nativeType)
			return new ResolvedBinaryOpExpressionNode(nativeType, left, op, right);
		
		// TODO Based on types of sub-expressions and the op token, we search for operator overloads
		return new ResolvedBinaryOpExpressionNode(NativeSymbols.Invalid, left, op, right);
	}
	
	public IResolvedNode Visit(ChainedExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		var operands = new List<IResolvedExpressionNode>(node.Operands.Length);
		_targetTypes.Push(null);
		
		foreach (var operand in node.Operands)
			operands.Add(VisitNode(operand));
		
		_targetTypes.Pop();
		
		var types = new List<TypeSymbol>(node.Ops.Length);
		for (var i = 0; i < operands.Count - 1; i++)
		{
			var left = operands[i];
			var op = node.Ops[i];
			var right = operands[i + 1];
			
			// TODO Operator overloads
			if (NativeOperations.Resolve(left.Type, op.Type, right.Type) is not { } opNativeType)
				opNativeType = NativeSymbols.Invalid;
			
			types.Add(opNativeType);
		}
		
		// Aggregate types with implicit AND
		var resultType = types[0];
		for (var i = 1; i < types.Count - 1; i++)
		{
			var right = types[i + 1];
			
			if (NativeOperations.Resolve(resultType, TokenType.OpAmpersand, right) is not { } opNativeType)
				opNativeType = NativeSymbols.Invalid;
			
			resultType = opNativeType;
		}
		
		// TODO Implicit cast if needed
		
		return new ResolvedChainedExpressionNode(resultType, operands, node.Ops, types);
	}
	
	private static (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span, TypeSymbol? targetType)
	{
		// TODO Check suffixes
		
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
			}
		
		if (int.TryParse(span, out var intValue2))
			return (NativeSymbols.Int32, intValue2);
		
		if (long.TryParse(span, out var longValue2))
			return (NativeSymbols.Int64, longValue2);
		
		if (Int128.TryParse(span, out var int128Value2))
			return (NativeSymbols.Int128, int128Value2);
		
		return (null, null);
	}
	
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
}