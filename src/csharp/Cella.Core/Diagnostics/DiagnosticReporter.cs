using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Diagnostics;

public static class DiagnosticReporter
{
	public static Diagnostic ReportBinaryOpMismatch(OperatorRegistry registry, IResolvedExpressionNode left, Token op,
		IResolvedExpressionNode right)
	{
		var message = $"No operation defined for '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'";
		var hints = new List<string>();
		
		// One side is a pointer to the other side's type
		if (left.Type is PointerType leftPtr && leftPtr.BaseType == right.Type)
		{
			var resolutionSet = registry.ResolveBinary(leftPtr.BaseType, op.Type, right.Type);
			if (resolutionSet.HasResult)
			{
				hints.Add($"Did you mean '{ApplyUnary(TokenType.OpStar, left.Syntax)} {op.Text} " +
				          $"{right.Syntax.SourceLocation.GetText()}'?");
			}
		}
		else if (right.Type is PointerType rightPtr && rightPtr.BaseType == left.Type)
		{
			var resolutionSet = registry.ResolveBinary(left.Type, op.Type, rightPtr.BaseType);
			if (resolutionSet.HasResult)
			{
				hints.Add($"Did you mean '{left.Syntax.SourceLocation.GetText()} {op.Text} " +
				          $"{ApplyUnary(TokenType.OpStar, right.Syntax)}'?");
			}
		}
		
		var (source, range) = left.Syntax.SourceLocation;
		range = range.Join(right.Syntax.SourceLocation.Range);
		var location = new SourceLocation(source, range);
		
		return new Diagnostic(DiagnosticSeverity.Error, location, message)
		{
			Hints = hints.ToImmutableArray()
		};
	}
	
	private static string ApplyUnary(TokenType op, IExpressionNode operand) => operand.IsContained
		? $"{op.Representation}{operand.SourceLocation.GetText()}"
		: $"{op.Representation}({operand.SourceLocation.GetText()})";
}