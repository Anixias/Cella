using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class TypeChecker(ConstantEvaluator evaluator, TypePool typePool) : IResolvedStatementNodeVisitor,
	IResolvedDeclarationNodeVisitor, IResolvedExpressionNodeVisitor
{
	public DiagnosticList Diagnostics { get; } = new();
	
	private readonly Stack<TypeSymbol?> _returnTypeStack = [];
	private int continueDepth;
	private int breakDepth;
	
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
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.Syntax.SourceLocation,
					$"Cannot return value of type '{expression.Type.Name}': Expected type '{returnType.Name}'"));
			
			return;
		}
		
		if (node.Body is not IResolvedStatementNode statement)
			return;
		
		_returnTypeStack.Push(returnType);
		VisitNode(statement);
		_returnTypeStack.Pop();
	}
	
	public void Visit(ResolvedInvalidDeclarationNode node) =>
		throw new InvalidOperationException();
	
	public void Visit(ResolvedRecordNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
	}
	
	public void Visit(ResolvedFieldNode node)
	{
	}
	
	public void Visit(ResolvedEnumNode node)
	{
	}
	
	public void Visit(ResolvedMethodNode node) => VisitNode(node.FunctionNode);
	
	public void Visit(ResolvedExternalFunctionNode node)
	{
	}
	
	public void Visit(ResolvedGlobalNode node)
	{
		var errorCount = Diagnostics.ErrorCount;
		VisitNode(node.Info.Initializer);
		
		if (node.Info.Value is null && Diagnostics.ErrorCount == errorCount &&
		    FindNonConstant(node.Info.Initializer) is { } blocking)
			Diagnostics.Add(new(DiagnosticSeverity.Error, blocking.Syntax.SourceLocation,
				$"The initial value of '{node.Info.Symbol.Name}' must be a constant"));
	}
	
	public void Visit(ResolvedBlockStatementNode node)
	{
		foreach (var statement in node.Statements)
			VisitNode(statement);
	}
	
	public void Visit(ResolvedBreakExpressionNode node)
	{
		if (breakDepth <= 0)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Breaks are only allowed within loops")); // TODO Allow within switch statements?
	}
	
	public void Visit(ResolvedContinueExpressionNode node)
	{
		if (continueDepth <= 0)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Continues are only allowed within loops"));
	}
	
	public void Visit(ResolvedExpressionStatementNode node)
	{
		if (!IsAllowedAsStatement(node.Expression))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Only assignments and function calls are allowed as statements"));
		
		// ^Need to check for purity of expressions. Pure expressions as statements is either an error or a warning
		VisitNode(node.Expression);
	}
	
	public void Visit(ResolvedIfStatementNode node)
	{
		var conditionType = node.Condition.Type;
		if (!AreTypesCompatible(NativeSymbols.Bool, conditionType))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Condition.Syntax.SourceLocation,
				$"Invalid condition type '{conditionType.Name}': Expected type '{NativeSymbols.Bool.Name}'"));
		
		VisitNode(node.Condition);
		VisitNode(node.Then);
		
		if (node.Else is { } @else)
			VisitNode(@else);
	}
	
	public void Visit(ResolvedInvalidStatementNode node) =>
		throw new InvalidOperationException();
	
	public void Visit(ResolvedMatchStatementNode node)
	{
		if (node.IsMut)
			CheckMutPlace(node.Value);
		
		VisitNode(node.Value);
		foreach (var arm in node.Arms)
			VisitNode(arm.Body);
	}
	
	public void Visit(ResolvedMatchExpressionNode node)
	{
		if (node.IsMut)
			CheckMutPlace(node.Value);
		
		VisitNode(node.Value);
		foreach (var arm in node.Arms)
		{
			VisitNode(arm.Value);
			CheckConsumed(arm.Value);
		}
	}
	
	public void Visit(ResolvedReturnExpressionNode node)
	{
		var expected = _returnTypeStack.Peek();
		var actual = node.Value?.Type ?? NativeSymbols.Void;
		
		if (!AreTypesCompatible(expected, actual))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				$"Cannot return value of type '{actual.Name}': Expected type '{expected?.Name ?? "void"}'"));
		
		if (node.Value is not { } value)
			return;
		
		VisitNode(value);
		CheckConsumed(value);
	}
	
	public void Visit(ResolvedVarStatementNode node)
	{
		var expected = node.Symbol.Type;
		
		if (node.Initializer is { } initializer)
		{
			var actual = initializer.Type;
			if (!AreTypesCompatible(expected, actual))
				Diagnostics.Add(new(DiagnosticSeverity.Error, initializer.Syntax.SourceLocation,
					$"Cannot assign value of type '{actual.Name}': Expected type '{expected.Name}'"));
			
			// Special case: If undef, don't check (it will throw an error)
			if (initializer is not ResolvedUndefExpressionNode)
			{
				VisitNode(initializer);
				CheckConsumed(initializer);
			}
		}
		else if (expected == NativeSymbols.Invalid)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				"Implicitly-typed local variable must have an initializer"));
	}
	
	public void Visit(ResolvedWhileStatementNode node)
	{
		var conditionType = node.Condition.Type;
		if (!AreTypesCompatible(NativeSymbols.Bool, conditionType))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Condition.Syntax.SourceLocation,
				$"Invalid condition type '{conditionType.Name}': Expected type '{NativeSymbols.Bool.Name}'"));
		
		VisitNode(node.Condition);
		
		continueDepth++;
		breakDepth++;
		VisitNode(node.Body);
		breakDepth--;
		continueDepth--;
	}
	
	public void Visit(ResolvedDoWhileStatementNode node)
	{
		var conditionType = node.Condition.Type;
		if (!AreTypesCompatible(NativeSymbols.Bool, conditionType))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Condition.Syntax.SourceLocation,
				$"Invalid condition type '{conditionType.Name}': Expected type '{NativeSymbols.Bool.Name}'"));
		
		VisitNode(node.Condition);
		
		continueDepth++;
		breakDepth++;
		VisitNode(node.Body);
		breakDepth--;
		continueDepth--;
	}
	
	public void Visit(ResolvedLoopStatementNode node)
	{
		continueDepth++;
		breakDepth++;
		VisitNode(node.Body);
		breakDepth--;
		continueDepth--;
	}
	
	public void Visit(ResolvedRepeatStatementNode node)
	{
		var countType = node.Count.Type;
		if (!IsIntegralType(countType))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Count.Syntax.SourceLocation,
				$"Invalid count type '{countType.Name}': Expected an integral type"));
		
		VisitNode(node.Count);
		
		continueDepth++;
		breakDepth++;
		VisitNode(node.Body);
		breakDepth--;
		continueDepth--;
	}
	
	// TEMP Will need implicit conversions, subtyping, traits/interfaces, constraints, etc.
	private bool AreTypesCompatible(TypeSymbol? expected, TypeSymbol? actual) => expected == actual;
	
	private bool IsLValue(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedVarExpressionNode => true,
		ResolvedGlobalExpressionNode => true,
		ResolvedAccessExpressionNode { Member: FieldSymbol } e => IsLValue(e.Target),
		ResolvedIndexerExpressionNode e => IsLValue(e.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
		_ => false
	};
	
	private bool IsAllowedAsStatement(IResolvedExpressionNode expression) => expression switch
	{
		ResolvedFunctionCallExpressionNode => true, // TODO Warn if function is pure?
		ResolvedIndirectCallExpressionNode => true,
		ResolvedAssignmentExpressionNode => true,
		ResolvedReturnExpressionNode or ResolvedBreakExpressionNode or ResolvedContinueExpressionNode => true,
		_ => false
	};
	
	private bool IsIntegralType(TypeSymbol type) => type is IntegerType;
	
	private void ReportZeroDivisor(IResolvedExpressionNode divisor)
	{
		if (evaluator.Evaluate(divisor) is IntegerConstant { Value.IsZero: true })
			Diagnostics.Add(new(DiagnosticSeverity.Error, divisor.Syntax.SourceLocation, "Division by zero"));
	}
	
	private bool IsOverflowingDivision(ResolvedBinaryOpExpressionNode node) =>
		node.Type is IntegerType { IsSigned: true } type &&
		evaluator.Evaluate(node.Right) is IntegerConstant { Value: var divisor } && divisor == BigInteger.MinusOne &&
		evaluator.Evaluate(node.Left) is IntegerConstant { Value: var dividend } && !evaluator.Fits(-dividend, type);
	
	private static VariableSymbol? FindImmutableBinding(IResolvedExpressionNode place) => place switch
	{
		ResolvedVarExpressionNode { Symbol: LocalVariableSymbol { IsMutable: false } local } => local,
		ResolvedVarExpressionNode { Symbol: ParameterSymbol { Mode: ParameterMode.ReadOnly } parameter } => parameter,
		ResolvedGlobalExpressionNode { Symbol: { IsMutable: false } global } => global,
		ResolvedAccessExpressionNode { Member: FieldSymbol } e => FindImmutableBinding(e.Target),
		ResolvedIndexerExpressionNode { Target.Type: ArrayType } e => FindImmutableBinding(e.Target),
		_ => null
	};
	
	private static string DescribeImmutable(VariableSymbol binding) => binding switch
	{
		LocalVariableSymbol { IsPatternBinding: true } => "read-only pattern bindings",
		ParameterSymbol => "read-only parameters",
		_ => "values"
	};
	
	private IResolvedExpressionNode? FindNonConstant(IResolvedExpressionNode node)
	{
		if (evaluator.Evaluate(node) is not null)
			return null;
		
		if (!CanFold(node))
			return node;
		
		return GetOperands(node).Select(FindNonConstant).FirstOrDefault(static n => n is not null) ?? node;
	}
	
	private static bool CanFold(IResolvedExpressionNode node) => node switch
	{
		ResolvedUnaryOpExpressionNode { Operation: NativeImpl { Op: not (TokenType.OpAt or TokenType.OpStar) } } =>
			true,
		ResolvedUnaryOpExpressionNode => false,
		ResolvedBinaryOpExpressionNode { Operation: NativeImpl or ConversionImpl } => true,
		ResolvedBinaryOpExpressionNode => false,
		_ => true
	};
	
	private static IEnumerable<IResolvedExpressionNode> GetOperands(IResolvedExpressionNode node) => node switch
	{
		ResolvedUnaryOpExpressionNode n => [n.Operand],
		ResolvedBinaryOpExpressionNode n => [n.Left, n.Right],
		ResolvedChainedExpressionNode n => n.Operands,
		ResolvedConversionExpressionNode n => [n.Source],
		ResolvedAccessExpressionNode n => [n.Target],
		ResolvedIndexerExpressionNode n => [n.Target, n.Index],
		ResolvedRecordExpressionNode n => n.Fields.Select(static f => f.Value),
		ResolvedEnumCaseExpressionNode n => n.Payload,
		ResolvedIsExpressionNode n => [n.Value],
		ResolvedMatchExpressionNode n => [n.Value],
		ResolvedArrayExpressionNode n => n.Values,
		_ => []
	};
	
	public void Visit(ResolvedAccessExpressionNode node)
	{
		VisitNode(node.Target);
	}
	
	public void Visit(ResolvedArrayExpressionNode node)
	{
		foreach (var value in node.Values)
		{
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedAssignmentExpressionNode node)
	{
		if (node.Left is ResolvedAccessExpressionNode { Member: PropertySymbol { Setter: null } })
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				"Cannot reassign read-only properties"));
		else if (!IsLValue(node.Left))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				"Assignment target must be addressable"));
		else if (FindImmutableBinding(node.Left) is { } binding)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Left.Syntax.SourceLocation,
				$"Cannot reassign {DescribeImmutable(binding)}"));
		
		var expected = node.Left.Type;
		var actual = node.Right.Type;
		if (node.Operation is null && !AreTypesCompatible(expected, actual))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Right.Syntax.SourceLocation,
				$"Cannot assign source type '{actual.Name}' to target type '{expected.Name}'"));
		
		if (node.Op.Type is TokenType.OpSlashEqual or TokenType.OpPercentEqual)
			ReportZeroDivisor(node.Right);
		
		VisitNode(node.Left);
		VisitNode(node.Right);
		if (node.Operation is null)
			CheckConsumed(node.Right);
	}
	
	public void Visit(ResolvedBinaryOpExpressionNode node)
	{
		if (node.Operation is NativeImpl { Op: TokenType.OpSlash or TokenType.OpPercent })
			ReportZeroDivisor(node.Right);
		
		if (node.Operation is NativeImpl { Op: TokenType.OpSlash } && IsOverflowingDivision(node))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				$"Division overflows '{node.Type.Name}'"));
		
		VisitNode(node.Left);
		VisitNode(node.Right);
	}
	
	public void Visit(ResolvedChainedExpressionNode node)
	{
		foreach (var operand in node.Operands)
			VisitNode(operand);
	}
	
	public void Visit(ResolvedConstructorCallExpressionNode node)
	{
		var args = node.Arguments;
		var paramTypes = node.Function.Signature.ParameterTypes;
		if (args.Length != paramTypes.Length - 1) // - 1 for implicit self parameter
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				$"Incorrect number of arguments: Expected {paramTypes.Length}, got {args.Length}"));
			
			return;
		}
		
		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			var expected = paramTypes[i + 1];
			var actual = arg.Type;
			
			if (!AreTypesCompatible(expected, actual))
				Diagnostics.Add(new(DiagnosticSeverity.Error, arg.Syntax.SourceLocation,
					$"Argument type '{actual.Name}' is not assignable to parameter type '{expected.Name}'"));
			
			VisitNode(arg);
			if (node.Function.Signature.GetMode(i + 1) == ParameterMode.Own)
				CheckConsumed(arg);
		}
	}
	
	public void Visit(ResolvedConversionExpressionNode node)
	{
		if (node.Conversion is EnumConversion { To: EnumSymbol { IsExternal: false } enumType } &&
		    evaluator.Evaluate(node.Source) is IntegerConstant { Value: var value } && evaluator.Evaluate(node) is null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Source.Syntax.SourceLocation,
				$"'{enumType.Name}' has no case with the value {value}"));
		
		VisitNode(node.Source);
	}
	
	public void Visit(ResolvedFunctionCallExpressionNode node)
	{
		var args = node.Arguments;
		var signature = node.Function.Signature;
		var paramTypes = signature.ParameterTypes;
		if (signature.IsVariadic ? args.Length < paramTypes.Length : args.Length != paramTypes.Length)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
				$"Incorrect number of arguments: Expected {paramTypes.Length}, got {args.Length}"));
			
			return;
		}
		
		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			if (i >= paramTypes.Length)
			{
				VisitNode(arg);
				continue;
			}
			
			var expected = paramTypes[i];
			var actual = arg.Type;
			
			if (!AreTypesCompatible(expected, actual))
				Diagnostics.Add(new(DiagnosticSeverity.Error, arg.Syntax.SourceLocation,
					$"Argument type '{actual.Name}' is not assignable to parameter type '{expected.Name}'"));
			
			VisitNode(arg);
			if (signature.GetMode(i) == ParameterMode.Own)
				CheckConsumed(arg);
		}
	}
	
	public void Visit(ResolvedFunctionReferenceExpressionNode node)
	{
	}
	
	public void Visit(ResolvedOwnExpressionNode node)
	{
		var location = node.Value.Syntax.SourceLocation;
		var place = SkipAssignment(node.Value);
		if (!IsLValue(place))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, "Cannot move unstored values"));
		else if (place is ResolvedGlobalExpressionNode { Symbol: var global })
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"Cannot move module {(global.IsMutable ? "variables" : "values")}"));
		else if (IsThroughPointer(place) && typePool.IsCopy(node.Value.Type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"Cannot move '{node.Value.Type.Name}' values out of a pointer"));
		
		VisitNode(node.Value);
	}
	
	private void CheckConsumed(IResolvedExpressionNode value)
	{
		var place = SkipAssignment(value);
		if (value is not ResolvedOwnExpressionNode && IsLValue(place) && IsThroughPointer(place) &&
		    !typePool.IsCopy(value.Type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, value.Syntax.SourceLocation,
				$"Cannot move '{value.Type.Name}' values out of a pointer implicitly"));
	}
	
	private static IResolvedExpressionNode SkipAssignment(IResolvedExpressionNode value) =>
		value is ResolvedAssignmentExpressionNode assignment ? assignment.Left : value;
	
	private static bool IsThroughPointer(IResolvedExpressionNode place) => place switch
	{
		ResolvedAccessExpressionNode n => IsThroughPointer(n.Target),
		ResolvedIndexerExpressionNode n => IsThroughPointer(n.Target),
		ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } n => n.Operand is not ResolvedVarExpressionNode
		{
			Symbol: ParameterSymbol { Mode: ParameterMode.Mut } or LocalVariableSymbol { IsMutBinding: true }
		},
		_ => false
	};
	
	public void Visit(ResolvedMutArgumentExpressionNode node)
	{
		CheckMutPlace(node.Place);
		VisitNode(node.Place);
	}
	
	private void CheckMutPlace(IResolvedExpressionNode place)
	{
		var location = place.Syntax.SourceLocation;
		if (!IsLValue(place))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, "Cannot mutably borrow unstored values"));
		else if (place is ResolvedGlobalExpressionNode { Symbol.IsMutable: true })
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, "Cannot mutably borrow module variables"));
		else if (FindImmutableBinding(place) is { } binding)
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"Cannot mutably borrow {DescribeImmutable(binding)}"));
	}
	
	public void Visit(ResolvedFunctionGroupExpressionNode node)
	{
	}
	
	public void Visit(ResolvedEnumCaseExpressionNode node)
	{
		foreach (var value in node.Payload)
		{
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedIsExpressionNode node)
	{
		if (node.IsMut)
			CheckMutPlace(node.Value);
		
		VisitNode(node.Value);
	}
	
	public void Visit(ResolvedIndirectCallExpressionNode node)
	{
		VisitNode(node.Target);
		for (var i = 0; i < node.Arguments.Length; i++)
		{
			VisitNode(node.Arguments[i]);
			if (node.FunctionType.ParameterModes[i] == ParameterMode.Own)
				CheckConsumed(node.Arguments[i]);
		}
	}
	
	public void Visit(ResolvedIndexerExpressionNode node)
	{
		if (node.Target.Type is ArrayType { Length.Sign: >= 0 } array &&
		    evaluator.Evaluate(node.Index) is IntegerConstant { Value: var index } && index >= array.Length)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Index.Syntax.SourceLocation,
				$"Index {index} is out of range for '{array.Name}'"));
		
		VisitNode(node.Target);
		VisitNode(node.Index);
	}
	
	public void Visit(ResolvedInvalidExpressionNode node) =>
		throw new InvalidOperationException();
	
	public void Visit(ResolvedLiteralExpressionNode node)
	{
	}
	
	public void Visit(ResolvedRecordExpressionNode node)
	{
		foreach (var (_, value) in node.Fields)
		{
			VisitNode(value);
			CheckConsumed(value);
		}
	}
	
	public void Visit(ResolvedUnaryOpExpressionNode node)
	{
		if (node.Operation?.Op == TokenType.OpAt && !IsLValue(node.Operand))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Operand.Syntax.SourceLocation,
				"Cannot take the address of unstored values"));
		else if (node is { Operation.Op: TokenType.OpAt } &&
		         node.Operand is ResolvedGlobalExpressionNode { Symbol.IsMutable: true })
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Operand.Syntax.SourceLocation,
				"Cannot take the address of module variables"));
		
		VisitNode(node.Operand);
	}
	
	// TODO Better diagnostics
	public void Visit(ResolvedUndefExpressionNode node)
	{
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.Syntax.SourceLocation,
			"'undef' may only be used as a variable initializer"));
	}
	
	public void Visit(ResolvedVarExpressionNode node)
	{
	}
	
	public void Visit(ResolvedGlobalExpressionNode node)
	{
	}
}