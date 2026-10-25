using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ConstantBodyChecker(TypePool typePool, DiagnosticList diagnostics)
	: IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor
{
	private int errors;
	private string name = "";
	private TraitType? constantSelf;
	
	public Diagnostic? FirstError { get; private set; }
	
	public bool CheckDefault(ResolvedFunctionNode node, TraitSymbol trait)
	{
		constantSelf = typePool.GetTraitType(trait, [..trait.TypeParameters]);
		try
		{
			return Check(node);
		}
		finally
		{
			constantSelf = null;
		}
	}
	
	public bool Check(ResolvedFunctionNode node)
	{
		errors = 0;
		var function = node.FunctionInfo;
		name = function.Symbol.Name;
		if (function.Signature.ReturnType is NeverType)
			Report(node.Syntax is FunctionNode { ReturnType: { } returnType }
				? returnType.SourceLocation
				: node.Syntax.SourceLocation, "Cannot return 'never' from constant functions");
		
		var parameters = function.Symbol.Parameters;
		for (var i = 0; i < parameters.Length && i < function.Signature.ParameterTypes.Length; i++)
		{
			if ((function.Symbol.Kind != FunctionKind.Constructor || i > 0) &&
			    function.Signature.GetMode(i) != ParameterMode.Imm)
				CheckStorable(function.Signature.GetDeclaredType(i), parameters[i].Definition);
		}
		
		switch (node.Body)
		{
			case IResolvedStatementNode statement:
				VisitNode(statement);
				break;
			
			case IResolvedExpressionNode expression:
				VisitNode(expression);
				break;
		}
		
		return errors == 0;
	}
	
	private void Report(SourceLocation location, string message)
	{
		errors++;
		var diagnostic = new Diagnostic(DiagnosticSeverity.Error, location, message);
		FirstError ??= diagnostic;
		diagnostics.Add(diagnostic);
	}
	
	private void CheckStorable(TypeSymbol type, SourceLocation location)
	{
		if (!typePool.NeedsDrop(type))
			return;
		
		Report(location, FindDroppingParameter(type) is { } parameter
			? $"'{name}' requires '{parameter.Name}: copy'"
			: "Cannot use values with destructors in constant functions");
	}
	
	private TypeParameterSymbol? FindDroppingParameter(TypeSymbol type) => type switch
	{
		TypeParameterSymbol parameter => typePool.NeedsDrop(parameter) ? parameter : null,
		ArrayType array => FindDroppingParameter(array.ElementType),
		_ => type.TypeArguments.Select(FindDroppingParameter).FirstOrDefault(static parameter => parameter is not null)
	};
	
	private void CheckCall(FunctionInfo function, IExpressionNode syntax)
	{
		if (function.Symbol.IsConstant || IsConstantThroughBound(function))
			return;
		
		Report(syntax.SourceLocation, function.Symbol switch
		{
			{ IsConversion: true } => $"Cannot convert to '{function.Signature.ReturnType.Name}' in constant functions",
			{ Kind: FunctionKind.Constructor } =>
				$"Cannot construct '{function.Signature.GetDeclaredType(0).Name}' in constant functions",
			{ Property: not null, Syntax: FunctionNode accessor } symbol =>
				$"Cannot {DescribeAccess(accessor)} '{symbol.Name[..symbol.Name.LastIndexOf('.')]}' in constant functions",
			var symbol => $"Cannot call '{symbol.Name}' in constant functions"
		});
	}
	
	private static string DescribeAccess(FunctionNode accessor) =>
		accessor.Identifier.Type == TokenType.KeywordGet ? "read" : "set";
	
	private bool IsConstantThroughBound(FunctionInfo function) => function switch
	{
		{ Symbol.Trait: { } trait, TypeArguments.IsDefaultOrEmpty: true } =>
			constantSelf is { } own && typePool.Requires(own, trait, own.Trait.Self),
		{ Symbol.Trait: { } trait, TypeArguments: [TypeParameterSymbol self, ..] } =>
			typePool.HasConstantBound(self, trait) || constantSelf is { } own && self == own.Trait.Self &&
			typePool.Requires(own, trait, self),
		{ Symbol.Trait: { } trait, TypeArguments: [var self, ..] } =>
			typePool.IsConstantConformance(self, typePool.GetTraitType(trait, TypePool.GetTraitArguments(function))),
		_ => false
	};
	
	private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
	
	private void VisitNode(IResolvedExpressionNode node) => ((IResolvedExpressionNodeVisitor)this).Visit(node);
	
	private void VisitAll(IEnumerable<IResolvedExpressionNode> nodes)
	{
		foreach (var node in nodes)
			VisitNode(node);
	}
	
	private void VisitPatterns(IEnumerable<ResolvedPattern> patterns)
	{
		foreach (var pattern in patterns)
			VisitPattern(pattern);
	}
	
	private void VisitPattern(ResolvedPattern pattern)
	{
		if (pattern.Test is { } test)
			VisitNode(test);
		
		foreach (var element in pattern.Elements.OfType<ResolvedPattern>())
			VisitPattern(element);
	}
	
	public void Visit(ResolvedBlockStatementNode node)
	{
		foreach (var statement in node.Statements)
			VisitNode(statement);
	}
	
	public void Visit(ResolvedDropStatementNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot use 'drop' in constant functions");
	
	public void Visit(ResolvedExpressionStatementNode node) => VisitNode(node.Expression);
	
	public void Visit(ResolvedRangeForStatementNode node)
	{
		if (node.Source is { Value: var source })
			VisitNode(source);
		
		VisitNode(node.Start);
		VisitNode(node.End);
		VisitNode(node.Body);
	}
	
	public void Visit(ResolvedCursorForStatementNode node)
	{
		VisitNode(node.Cursor.Value);
		VisitNode(node.Step);
		if (node.Element is { Value: var element })
			VisitNode(element);
		
		VisitNode(node.Body);
	}
	
	public void Visit(ResolvedIfStatementNode node)
	{
		VisitNode(node.Condition);
		VisitNode(node.Then);
		if (node.Else is { } otherwise)
			VisitNode(otherwise);
	}
	
	public void Visit(ResolvedMatchStatementNode node)
	{
		VisitNode(node.Value);
		foreach (var arm in node.Arms)
		{
			VisitPatterns(arm.Patterns);
			if (arm.Guard is { } guard)
				VisitNode(guard);
			
			VisitNode(arm.Body);
		}
	}
	
	public void Visit(ResolvedVarStatementNode node)
	{
		if (node.Initializer is { } initializer)
			VisitNode(initializer);
		else
			CheckStorable(node.Symbol.Type, node.Symbol.Identifier.SourceLocation);
	}
	
	public void Visit(ResolvedWhileStatementNode node)
	{
		VisitNode(node.Condition);
		VisitNode(node.Body);
	}
	
	public void Visit(ResolvedDoWhileStatementNode node)
	{
		VisitNode(node.Body);
		VisitNode(node.Condition);
	}
	
	public void Visit(ResolvedLoopStatementNode node) => VisitNode(node.Body);
	
	public void Visit(ResolvedInvalidStatementNode node)
	{
	}
	
	public void Visit(ResolvedAccessExpressionNode node) => VisitNode(node.Target);
	
	public void Visit(ResolvedAlignOfExpressionNode node)
	{
	}
	
	public void Visit(ResolvedArrayBuildExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot call function values in constant functions");
	
	public void Visit(ResolvedArrayExpressionNode node) => VisitAll(node.Values);
	
	public void Visit(ResolvedAssignmentExpressionNode node)
	{
		VisitNode(node.Left);
		VisitNode(node.Right);
	}
	
	public void Visit(ResolvedAtomicExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot use 'atomic' in constant functions");
	
	public void Visit(ResolvedBinaryOpExpressionNode node)
	{
		if (node.Operation is PointerOffsetImpl or PointerDifferenceImpl)
			Report(node.Syntax.SourceLocation, "Cannot use pointers in constant functions");
		
		VisitNode(node.Left);
		VisitNode(node.Right);
	}
	
	public void Visit(ResolvedBorrowExpressionNode node) => VisitNode(node.Place);
	
	public void Visit(ResolvedBreakExpressionNode node)
	{
	}
	
	public void Visit(ResolvedCaseNameExpressionNode node)
	{
	}
	
	public void Visit(ResolvedChainedExpressionNode node)
	{
		foreach (var link in node.Links)
		{
			if (link.Function is { } function)
				CheckCall(function, node.Syntax);
		}
		
		VisitAll(node.Operands);
	}
	
	public void Visit(ResolvedClosureExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot declare lambdas in constant functions");
	
	public void Visit(ResolvedConstructorCallExpressionNode node)
	{
		if (node.Function.Symbol.Syntax is ConstructorConstraintNode)
			Report(node.Syntax.SourceLocation, $"Cannot construct '{node.Type.Name}' in constant functions");
		else if (!typePool.IsDefaultConstructor(node.Function))
			CheckCall(node.Function, node.Syntax);
		
		VisitAll(node.Arguments);
	}
	
	public void Visit(ResolvedContinueExpressionNode node)
	{
	}
	
	public void Visit(ResolvedConversionExpressionNode node)
	{
		if (node.Conversion is CallableConversion or ExternalFunctionConversion or DynConversion or
		    DynUpcastConversion or DynTestConversion or DynLayoutConversion or DynCastConversion)
			Report(node.Syntax.SourceLocation, $"Cannot convert to '{node.Type.Name}' in constant functions");
		
		VisitNode(node.Source);
	}
	
	public void Visit(ResolvedEnumCaseExpressionNode node) => VisitAll(node.Payload);
	
	public void Visit(ResolvedErasedCallExpressionNode node)
	{
	}
	
	public void Visit(ResolvedFStrExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot use 'fstr' values in constant functions");
	
	public void Visit(ResolvedFStrPartExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot use 'fstr' values in constant functions");
	
	public void Visit(ResolvedFunctionCallExpressionNode node)
	{
		if (node.Syntax is PanicExpressionNode)
			return;
		
		CheckCall(node.Function, node.Syntax);
		VisitAll(node.Arguments);
	}
	
	public void Visit(ResolvedFunctionGroupExpressionNode node)
	{
	}
	
	public void Visit(ResolvedFunctionReferenceExpressionNode node)
	{
	}
	
	public void Visit(ResolvedGlobalExpressionNode node)
	{
		if (!node.Symbol.IsConstant)
			Report(node.Syntax.SourceLocation, node.Symbol.ContainingType is null
				? "Cannot use module values in constant functions"
				: "Cannot use static values in constant functions");
	}
	
	public void Visit(ResolvedIndexerExpressionNode node)
	{
		if (node.Target.Type is PointerType { BaseType: not ArrayType })
			Report(node.Syntax.SourceLocation, "Cannot use pointers in constant functions");
		
		VisitNode(node.Target);
		VisitNode(node.Index);
	}
	
	public void Visit(ResolvedIndirectCallExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot call function values in constant functions");
	
	public void Visit(ResolvedInterpolatedStringExpressionNode node) => VisitAll(node.Values);
	
	public void Visit(ResolvedIsExpressionNode node)
	{
		VisitNode(node.Value);
		VisitPattern(node.Pattern);
	}
	
	public void Visit(ResolvedLiteralExpressionNode node)
	{
	}
	
	public void Visit(ResolvedMatchExpressionNode node)
	{
		VisitNode(node.Value);
		foreach (var arm in node.Arms)
		{
			VisitPatterns(arm.Patterns);
			if (arm.Guard is { } guard)
				VisitNode(guard);
			
			VisitNode(arm.Value);
		}
	}
	
	public void Visit(ResolvedMutArgumentExpressionNode node) => VisitNode(node.Place);
	
	public void Visit(ResolvedNewExpressionNode node)
	{
		if (node.Type is TypeParameterSymbol)
			Report(node.Syntax.SourceLocation, $"Cannot construct '{node.Type.Name}' in constant functions");
		else if (typePool.FindNewConstructor(node.Type) is { } constructor)
			CheckCall(constructor, node.Syntax);
	}
	
	public void Visit(ResolvedOwnExpressionNode node) => VisitNode(node.Value);
	
	public void Visit(ResolvedPackExpressionNode node) => VisitAll(node.Values);
	
	public void Visit(ResolvedPropertyAssignmentExpressionNode node)
	{
		CheckCall(node.Getter, node.Syntax);
		CheckCall(node.Setter, node.Syntax);
		if (node.Receiver is { } receiver)
			VisitNode(receiver);
		
		VisitNode(node.Right);
	}
	
	public void Visit(ResolvedPropertyExpressionNode node) => throw new InvalidOperationException();
	
	public void Visit(ResolvedQuestionExpressionNode node)
	{
		CheckCall(node.Info, node.Syntax);
		VisitNode(node.Operand);
	}
	
	public void Visit(ResolvedRecordExpressionNode node) => VisitAll(node.Fields.Select(static field => field.Value));
	
	public void Visit(ResolvedReturnExpressionNode node)
	{
		if (node.Value is { } value)
			VisitNode(value);
	}
	
	public void Visit(ResolvedSizeOfExpressionNode node)
	{
	}
	
	public void Visit(ResolvedTrapExpressionNode node)
	{
	}
	
	public void Visit(ResolvedTypeConstantExpressionNode node)
	{
	}
	
	public void Visit(ResolvedUnaryOpExpressionNode node)
	{
		switch (node.Operation?.Op)
		{
			case TokenType.OpAt:
				Report(node.Syntax.SourceLocation, "Cannot take addresses in constant functions");
				break;
			
			case TokenType.OpStar when node.Syntax is UnaryOpExpressionNode && node.Operand.Type is PointerType:
				Report(node.Syntax.SourceLocation, "Cannot use pointers in constant functions");
				break;
		}
		
		VisitNode(node.Operand);
	}
	
	public void Visit(ResolvedUndefExpressionNode node) =>
		Report(node.Syntax.SourceLocation, "Cannot use 'undef' in constant functions");
	
	public void Visit(ResolvedValueParameterExpressionNode node)
	{
	}
	
	public void Visit(ResolvedVarExpressionNode node)
	{
	}
	
	public void Visit(ResolvedInvalidExpressionNode node)
	{
	}
}