using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class TypeChecker : IResolvedStatementNodeVisitor, IResolvedDeclarationNodeVisitor,
	IResolvedExpressionNodeVisitor
{
	public IReadOnlyList<string> Diagnostics => _diagnostics;
	
	private readonly Stack<TypeSymbol?> _returnTypeStack = [];
	private readonly List<string> _diagnostics = []; // TODO More info needed
	
	public void Check(ResolvedFileNode root) => VisitNode(root);
	
	private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
	private void VisitNode(IResolvedDeclarationNode node) => ((IResolvedDeclarationNodeVisitor)this).Visit(node);
	private void VisitNode(IResolvedExpressionNode node) => ((IResolvedExpressionNodeVisitor)this).Visit(node);
	
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
		
		if (node.Body is not IResolvedStatementNode statement)
			return;
		
		_returnTypeStack.Push(returnType);
		VisitNode(statement);
		_returnTypeStack.Pop();
	}
	
	public void Visit(ResolvedRecordNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
	}
	
	public void Visit(ResolvedFieldNode node)
	{
		if (node.Initializer is not { } initializer)
			return;
		
		var expected = node.Type;
		var actual = initializer.Type;
		if (!AreTypesCompatible(expected, actual))
			_diagnostics.Add(
				$"Cannot assign value of type '{actual.Name}': Expected type '{expected.Name}'");
	}
	
	public void Visit(ResolvedMethodNode node) => VisitNode(node.FunctionNode);
	
	public void Visit(ResolvedExternalFunctionNode node)
	{
	}
	
	public void Visit(ResolvedBlockStatementNode node)
	{
		foreach (var statement in node.Statements)
			VisitNode(statement);
	}
	
	public void Visit(ResolvedBreakStatementNode node)
	{
	}
	
	public void Visit(ResolvedContinueStatementNode node)
	{
	}
	
	public void Visit(ResolvedExpressionStatementNode node)
	{
		if (!IsAllowedAsStatement(node.Expression))
			_diagnostics.Add("Only assignments and function calls are allowed as statements");
		
		// ^Need to check for purity of expressions. Pure expressions as statements is either an error or a warning
		VisitNode(node.Expression);
	}
	
	public void Visit(ResolvedIfStatementNode node)
	{
		var conditionType = node.Condition.Type;
		if (!AreTypesCompatible(NativeSymbols.Bool, conditionType))
			_diagnostics.Add(
				$"Invalid condition type '{conditionType.Name}': Expected type '{NativeSymbols.Bool.Name}'");
		
		VisitNode(node.Then);
		
		if (node.Else is { } @else)
			VisitNode(@else);
	}
	
	public void Visit(ResolvedReturnStatementNode node)
	{
		var expected = _returnTypeStack.Peek();
		var actual = node.Expression?.Type ?? NativeSymbols.Void;
		
		if (!AreTypesCompatible(expected, actual))
			_diagnostics.Add(
				$"Cannot return value of type '{actual.Name}': Expected type '{expected?.Name ?? "void"}'");
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
			
			// Special case: If undef, don't check (it will throw an error)
			if (initializer is not ResolvedUndefExpressionNode)
				VisitNode(initializer);
		}
		else if (expected == NativeSymbols.Invalid)
			_diagnostics.Add("Implicitly-typed local variable must have an initializer");
	}
	
	public void Visit(ResolvedWhileStatementNode node)
	{
		var conditionType = node.Condition.Type;
		if (!AreTypesCompatible(NativeSymbols.Bool, conditionType))
			_diagnostics.Add(
				$"Invalid condition type '{conditionType.Name}': Expected type '{NativeSymbols.Bool.Name}'");
		
		VisitNode(node.Body);
	}
	
	public void Visit(ResolvedDoWhileStatementNode node)
	{
		var conditionType = node.Condition.Type;
		if (!AreTypesCompatible(NativeSymbols.Bool, conditionType))
			_diagnostics.Add(
				$"Invalid condition type '{conditionType.Name}': Expected type '{NativeSymbols.Bool.Name}'");
		
		VisitNode(node.Body);
	}
	
	public void Visit(ResolvedLoopStatementNode node) => VisitNode(node.Body);
	
	public void Visit(ResolvedRepeatStatementNode node)
	{
		var countType = node.Count.Type;
		if (!IsIntegralType(countType))
			_diagnostics.Add(
				$"Invalid count type '{countType.Name}': Expected an integral type");
		
		VisitNode(node.Body);
	}
	
	// TEMP Will need implicit conversions, subtyping, traits/interfaces, constraints, etc.
	private bool AreTypesCompatible(TypeSymbol? expected, TypeSymbol? actual) => expected == actual;
	
	private bool IsLValue(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedVarExpressionNode => true,
		ResolvedAccessExpressionNode => true,
		ResolvedIndexerExpressionNode => true,
		_ => false
	};
	
	private bool IsAllowedAsStatement(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedFunctionCallExpressionNode => true, // TODO Warn if function is pure?
		ResolvedAssignmentExpressionNode => true,
		_ => false
	};
	
	private bool IsIntegralType(TypeSymbol type) => type is PrimitiveType pt && pt.Kind switch
	{
		PrimitiveTypeKind.Int8 => true,
		PrimitiveTypeKind.Int16 => true,
		PrimitiveTypeKind.Int32 => true,
		PrimitiveTypeKind.Int64 => true,
		PrimitiveTypeKind.Int128 => true,
		PrimitiveTypeKind.IntSize => true,
		PrimitiveTypeKind.UInt8 => true,
		PrimitiveTypeKind.UInt16 => true,
		PrimitiveTypeKind.UInt32 => true,
		PrimitiveTypeKind.UInt64 => true,
		PrimitiveTypeKind.UInt128 => true,
		PrimitiveTypeKind.UIntSize => true,
		_ => false
	};
	
	public void Visit(ResolvedAccessExpressionNode node)
	{
		VisitNode(node.Target);
	}
	
	public void Visit(ResolvedArrayExpressionNode node)
	{
		foreach (var value in node.Values)
			VisitNode(value);
	}
	
	public void Visit(ResolvedAssignmentExpressionNode node)
	{
		// TODO Better diagnostic
		if (!IsLValue(node.Left))
			_diagnostics.Add("Assignment target must be a variable");
		
		var expected = node.Left.Type;
		var actual = node.Right.Type;
		if (!AreTypesCompatible(expected, actual))
			_diagnostics.Add($"Cannot assign source type '{actual.Name}' to target type '{expected.Name}'");
		
		VisitNode(node.Right);
	}
	
	public void Visit(ResolvedBinaryOpExpressionNode node)
	{
		VisitNode(node.Left);
		VisitNode(node.Right);
	}
	
	public void Visit(ResolvedChainedExpressionNode node)
	{
		foreach (var operand in node.Operands)
			VisitNode(operand);
	}
	
	public void Visit(ResolvedConversionExpressionNode node)
	{
		VisitNode(node.Source);
	}
	
	public void Visit(ResolvedFunctionCallExpressionNode node)
	{
		var args = node.Arguments;
		var paramTypes = node.Function.Signature.ParameterTypes;
		if (args.Length != paramTypes.Length)
		{
			_diagnostics.Add($"Incorrect number of arguments: Expected {paramTypes.Length}, got {args.Length}");
			return;
		}
		
		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			var expected = paramTypes[i];
			var actual = arg.Type;
			
			if (!AreTypesCompatible(expected, actual))
				_diagnostics.Add($"Argument type '{actual.Name}' is not assignable to parameter type " +
				                 $"'{expected.Name}'");
			
			VisitNode(arg);
		}
	}
	
	public void Visit(ResolvedIndexerExpressionNode node)
	{
		VisitNode(node.Target);
		VisitNode(node.Index);
	}
	
	public void Visit(ResolvedLiteralExpressionNode node)
	{
	}
	
	public void Visit(ResolvedUnaryOpExpressionNode node)
	{
		if (node.Operation?.Op == TokenType.OpAt && !IsLValue(node.Operand))
			_diagnostics.Add("Cannot take the address of an unstored value");
		
		VisitNode(node.Operand);
	}
	
	public void Visit(ResolvedUndefExpressionNode node) =>
		_diagnostics.Add("'undef' may only be used as a variable initializer");
	
	public void Visit(ResolvedVarExpressionNode node)
	{
	}
}