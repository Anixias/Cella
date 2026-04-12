using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Symbols;
using Cella.Core.Text;

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
		
		if (node.Body is not IResolvedStatementNode statement)
			return;
		
		_returnTypeStack.Push(returnType);
		VisitNode(statement);
		_returnTypeStack.Pop();
	}
	
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
		
		switch (node.Expression)
		{
			case ResolvedAssignmentExpressionNode e:
			{
				// TODO Better diagnostic
				if (!IsLValue(e.Left))
					_diagnostics.Add("Assignment target must be a variable");
				
				var expected = e.Left.Type;
				var actual = e.Right.Type;
				if (!AreTypesCompatible(expected, actual))
					_diagnostics.Add($"Cannot assign source type '{actual.Name}' to target type '{expected.Name}'");
				
				break;
			}
			
			case ResolvedUnaryOpExpressionNode e when e.Operation?.Op == TokenType.OpAt:
			{
				if (!IsLValue(e.Operand))
					_diagnostics.Add("Cannot take the address of an unstored value");
				
				break;
			}
			
			case ResolvedFunctionCallExpressionNode e:
			{
				var args = e.Arguments;
				var paramTypes = e.Function.Signature.ParameterTypes;
				if (args.Length != paramTypes.Length)
					_diagnostics.Add($"Incorrect number of arguments: Expected {paramTypes.Length}, got {args.Length}");
				else
				{
					for (var i = 0; i < args.Length; i++)
					{
						var expected = paramTypes[i];
						var actual = args[i].Type;
						
						if (!AreTypesCompatible(expected, actual))
							_diagnostics.Add(
								$"Argument type '{actual.Name}' is not assignable to parameter type '{expected.Name}'");
					}
				}
				
				break;
			}
		}
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
}