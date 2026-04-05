using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Binding.Operations;
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
		var returnType = node.FunctionInfo.Signature.ReturnType;
		if (node.Body is IResolvedExpressionNode expression)
		{
			if (!AreTypesCompatible(returnType, expression.Type))
				_diagnostics.Add(
					$"Cannot return value of type '{expression.Type.Name}': Expected type '{returnType.Name}'");
			
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
	
	public void Visit(ResolvedExpressionStatementNode node)
	{
		// TODO Disallow certain expressions from being allowed as statements? Only allow assignments, function calls, etc.
		// ^Need to check for purity of expressions. Pure expressions as statements is either an error or a warning
		
		switch (node.Expression)
		{
			case ResolvedBinaryOpExpressionNode e:
			{
				switch (e.Op)
				{
					case BinaryOperation.Assignment:
					case BinaryOperation.AddAssignment:
					case BinaryOperation.SubtractAssignment:
					case BinaryOperation.MultiplyAssignment:
					case BinaryOperation.DivideAssignment:
						// TODO Better diagnostic
						if (!IsLValue(e.Left))
							_diagnostics.Add("Assignment target must be a variable");
						
						break;
				}
				
				break;
			}
		}
	}
	
	private bool IsLValue(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedVarExpressionNode => true,
		_ => false
	};
	
	public void Visit(ResolvedReturnStatementNode node)
	{
		var expected = _returnTypeStack.Peek();
		var actual = node.Expression?.Type;
		
		if (!AreTypesCompatible(expected, actual))
			_diagnostics.Add(
				$"Cannot return value of type '{actual?.Name ?? "void"}': Expected type '{expected?.Name ?? "void"}'");
	}
	
	public void Visit(ResolvedVarStatementNode node)
	{
		var expected = node.Symbol.Type;
		
		if (node.Initializer is { } initializer)
		{
			var actual = initializer.Type;
			if (!AreTypesCompatible(expected, actual))
				_diagnostics.Add(
					$"Cannot assign value of type '{actual.Name}': Expected type '{expected.Name}'");
		}
		else if (expected == NativeSymbols.Invalid)
			_diagnostics.Add("Implicitly-typed local variable must have an initializer");
	}
	
	// TEMP Will need implicit conversions, subtyping, traits/interfaces, constraints, etc.
	private bool AreTypesCompatible(TypeSymbol? expected, TypeSymbol? actual) => expected == actual;
}