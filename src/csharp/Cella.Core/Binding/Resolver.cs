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

public sealed class Resolver(CollectorContext context) : ISyntaxNodeVisitor<IResolvedNode>
{
	private readonly Stack<Scope> _scopes = [];
	private readonly Stack<FunctionSymbol> _functions = [];
	private readonly Stack<TypeSymbol?> _targetTypes = [];
	private Scope CurrentScope => _scopes.Peek();
	private FunctionSymbol CurrentFunction => _functions.Peek();
	private TypeSymbol? CurrentTargetType => _targetTypes.Peek();
	
	private Scope GetScope(IDeclarationNode node) => context.DeclarationScopes[node];
	
	private Scope PushNodeScope(IDeclarationNode node)
	{
		var scope = GetScope(node);
		_scopes.Push(scope);
		return scope;
	}
	
	private Scope PopScope() => _scopes.Pop();
	
	public ResolvedFileNode Resolve(FileNode root) => (ResolvedFileNode)Visit(root);
	
	private IResolvedDeclarationNode VisitNode(IDeclarationNode node) =>
		(IResolvedDeclarationNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		(IResolvedStatementNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) =>
		(IResolvedExpressionNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	public IResolvedNode Visit(CallExpressionNode node)
	{
		var functionName = node.Identifier.GetText();
		var resolvedName = CurrentScope.Resolve(functionName);
		
		// TODO Diagnostics, emit invalid expression instead of throwing exceptions
		if (resolvedName is null)
			throw new Exception($"Symbol '{functionName}' not found in this scope");
		
		if (resolvedName is not FunctionSymbol function)
			throw new Exception($"Symbol '{functionName}' is not a function");
		
		return new ResolvedFunctionCallExpression(function, node.Arguments.Select(VisitNode));
	}
	
	public IResolvedNode Visit(FileNode node)
	{
		PushNodeScope(node);
		var resolvedDeclarations = new List<IResolvedDeclarationNode>(node.Declarations.Length);
		
		foreach (var declaration in node.Declarations)
			resolvedDeclarations.Add(VisitNode(declaration));
		
		PopScope();
		var module = (ModuleSymbol)context.DeclarationSymbols[node];
		return new ResolvedFileNode(module, resolvedDeclarations);
	}
	
	public IResolvedNode Visit(BlockStatementNode node)
	{
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		_scopes.Push(CurrentScope.CreateChild());
		
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		PopScope();
		return new ResolvedBlockStatementNode(statements);
	}
	
	public IResolvedNode Visit(FunctionNode node)
	{
		var function = (FunctionSymbol)context.DeclarationSymbols[node];
		
		_scopes.Push(context.DeclarationScopes[node]);
		_functions.Push(function);
		
		var body = Visit(node.Body);
		
		_functions.Pop();
		PopScope();
		
		return new ResolvedFunctionNode(function, body);
	}
	
	public IResolvedNode Visit(LiteralExpressionNode node)
	{
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		if (node.Token.Type == TokenType.IntegerLiteral)
			(type, value) = ParseInteger(valueSpan, CurrentTargetType);
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value);
	}
	
	public IResolvedNode Visit(UnaryOpExpressionNode node)
	{
		// We push null to allow sub-expressions to resolve naturally; then, we attempt to implicit cast to actual type
		_targetTypes.Push(null);
		var operand = VisitNode(node.Operand);
		_targetTypes.Pop();
		
		UnaryOperation op;
		if (node.Op.Type == TokenType.OpPlus)
			op = UnaryOperation.Identity;
		else if (node.Op.Type == TokenType.OpMinus)
			op = UnaryOperation.Negation;
		else
			throw new InvalidOperationException();
		
		if (NativeOperations.Resolve(op, operand.Type) is { } nativeType)
		{
			// We optimize away identity operations if it's a native type since it's a no-op
			if (op == UnaryOperation.Identity)
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
		
		BinaryOperation op;
		if (node.Op.Type == TokenType.OpPlus)
			op = BinaryOperation.Addition;
		else if (node.Op.Type == TokenType.OpMinus)
			op = BinaryOperation.Subtraction;
		else if (node.Op.Type == TokenType.OpStar)
			op = BinaryOperation.Multiplication;
		else if (node.Op.Type == TokenType.OpSlash)
			op = BinaryOperation.Division;
		else
			throw new InvalidOperationException();
		
		// TODO How to handle implicit upcasts..?
		if (NativeOperations.Resolve(left.Type, op, right.Type) is { } nativeType)
			return new ResolvedBinaryOpExpressionNode(nativeType, left, op, right);
		
		// TODO Based on types of sub-expressions and the op token, we search for operator overloads
		
		return new ResolvedBinaryOpExpressionNode(NativeSymbols.Invalid, left, op, right);
	}
	
	public IResolvedNode Visit(ReturnStatementNode node)
	{
		_targetTypes.Push(CurrentFunction.ReturnType);
		
		var result = new ResolvedReturnStatementNode(node.ExpressionNode is { } expressionNode
			? VisitNode(expressionNode)
			: null);
		
		_targetTypes.Pop();
		return result;
	}
	
	private static (TypeSymbol? Type, object? Value) ParseInteger(ReadOnlySpan<char> span, TypeSymbol? targetType)
	{
		// TODO Check suffixes
		
		if (targetType is PrimitiveType primitiveType)
			switch (primitiveType.Kind)
			{
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
			}
		
		if (int.TryParse(span, out var intValue2))
			return (NativeSymbols.Int32, intValue2);
		
		if (long.TryParse(span, out var longValue2))
			return (NativeSymbols.Int64, longValue2);
		
		if (Int128.TryParse(span, out var int128Value2))
			return (NativeSymbols.Int128, int128Value2);
		
		return (null, null);
	}
}