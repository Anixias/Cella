using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class TypeChecker : IResolvedNodeVisitor
{
	private readonly Stack<TypeSymbol?> _returnTypeStack = [];
	private readonly List<string> _diagnostics = []; // TODO More info needed
	
	public void Check(IResolvedNode root) => VisitNode(root);
	
	private void VisitNode(IResolvedNode node) => ((IResolvedNodeVisitor)this).Visit(node);
	
	public void Visit(ResolvedFileNode node)
	{
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
	}
	
	public void Visit(ResolvedFunctionNode node)
	{
		_returnTypeStack.Push(node.FunctionSymbol.ReturnType);
		VisitNode(node.Body);
		_returnTypeStack.Pop();
	}
	
	public void Visit(ResolvedLiteralExpressionNode node)
	{
	}
	
	public void Visit(ResolvedBlockStatementNode node)
	{
		foreach (var statement in node.Statements)
			VisitNode(statement);
	}
	
	public void Visit(ResolvedReturnStatementNode node)
	{
		var expected = _returnTypeStack.Peek();
		var actual = node.Expression?.Type;
		
		if (!AreTypesCompatible(expected, actual))
			_diagnostics.Add(
				$"Cannot return value of type '{actual?.Name ?? "void"}': Expected type '{expected?.Name ?? "void"}'");
	}
	
	// TEMP Will need implicit conversions, subtyping, traits/interfaces, constraints, etc.
	private bool AreTypesCompatible(TypeSymbol? expected, TypeSymbol? actual) => expected == actual;
}