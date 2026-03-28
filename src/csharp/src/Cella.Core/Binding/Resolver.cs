using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class Resolver(Collector collector) : ISyntaxNodeVisitor<IResolvedNode>
{
	private readonly Stack<Scope> _scopes = [];
	private Scope CurrentScope => _scopes.Peek();
	
	private Scope GetScope(IDeclarationNode node) => collector.DeclarationScopes[node];
	
	public IResolvedNode Resolve(ISyntaxNode root) => VisitNode(root);
	
	private IResolvedDeclarationNode VisitNode(IDeclarationNode node) =>
		(IResolvedDeclarationNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedStatementNode VisitNode(IStatementNode node) =>
		(IResolvedStatementNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedExpressionNode VisitNode(IExpressionNode node) =>
		(IResolvedExpressionNode)((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	private IResolvedNode VisitNode(ISyntaxNode node) => ((ISyntaxNodeVisitor<IResolvedNode>)this).Visit(node);
	
	public IResolvedNode Visit(FileNode node)
	{
		_scopes.Push(collector.DeclarationScopes[node]);
		var resolvedDeclarations = new List<IResolvedDeclarationNode>(node.Declarations.Length);
		
		foreach (var declaration in node.Declarations)
			resolvedDeclarations.Add(VisitNode(declaration));
		
		_scopes.Pop();
		var module = (ModuleSymbol)collector.DeclarationSymbols[node];
		return new ResolvedFileNode(module, resolvedDeclarations);
	}
	
	public IResolvedNode Visit(BlockStatementNode node)
	{
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		_scopes.Push(CurrentScope.CreateChild());
		
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		_scopes.Pop();
		return new ResolvedBlockStatementNode(statements);
	}
	
	public IResolvedNode Visit(FunctionNode node)
	{
		var function = (FunctionSymbol)collector.DeclarationSymbols[node];
		_scopes.Push(collector.DeclarationScopes[node]);
		var body = Visit(node.Body);
		_scopes.Pop();
		return new ResolvedFunctionNode(function, body);
	}
	
	public IResolvedNode Visit(LiteralExpressionNode node)
	{
		var valueSpan = node.Token.AsSpan();
		
		TypeSymbol? type = null;
		object? value = null;
		
		// TODO Need context like what the target variable's type is declared as
		// If not declared, we use type of token + value suffixes/format + range of value to determine type
		if (node.Token.Type == TokenType.IntegerLiteral)
		{
			// Assuming no suffixes or target type...
			if (int.TryParse(valueSpan, out var intValue))
			{
				value = intValue;
				type = NativeSymbols.Int32;
			}
			else if (long.TryParse(valueSpan, out var longValue))
			{
				value = longValue;
				type = NativeSymbols.Int64;
			}
			else if (Int128.TryParse(valueSpan, out var int128Value))
			{
				value = int128Value;
				type = NativeSymbols.Int128;
			}
		}
		
		// TODO We should emit diagnostics here
		type ??= NativeSymbols.Invalid;
		
		return new ResolvedLiteralExpressionNode(type, value);
	}
	
	public IResolvedNode Visit(ReturnStatementNode node) =>
		new ResolvedReturnStatementNode(VisitNode(node.ExpressionNode));
}