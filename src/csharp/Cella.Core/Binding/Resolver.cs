using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
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
	
	private IResolvedNode VisitNode(ISyntaxNode node) => ((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
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
		{
			(type, value) = ParseInteger(valueSpan, CurrentTargetType);
		}
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value);
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