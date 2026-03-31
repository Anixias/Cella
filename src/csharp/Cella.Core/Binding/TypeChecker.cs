using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class TypeChecker : IResolvedStatementNodeVisitor, IResolvedDeclarationNodeVisitor
{
	public IReadOnlyList<string> Diagnostics => _diagnostics;
	
	private readonly Stack<TypeSymbol?> _returnTypeStack = [];
	private readonly List<string> _diagnostics = []; // TODO More info needed
	
	public void Check(ResolvedFileNode root) => VisitNode(root);
	
	private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
	private void VisitNode(IResolvedDeclarationNode node) => ((IResolvedDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(ResolvedFileNode node)
	{
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
	}
	
	public void Visit(ResolvedFunctionNode node)
	{
		var returnType = node.FunctionSymbol.ReturnType;
		if (node.Body is IResolvedExpressionNode expression)
		{
			if (!AreTypesCompatible(returnType, expression.Type))
				_diagnostics.Add(
					$"Cannot return value of type '{expression.Type.Name}': Expected type '{returnType?.Name ?? "void"}'");
			
			return;
		}
		
		// Should be impossible, but just in case
		if (node.Body is not IResolvedStatementNode statement)
			return;
		
		_returnTypeStack.Push(returnType);
		VisitNode(statement);
		_returnTypeStack.Pop();
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