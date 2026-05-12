using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Diagnostics;

public static class DiagnosticReporter
{
	public static Diagnostic ReportUnreachableCode(SourceLocation location) =>
		new(DiagnosticSeverity.Warning, location, "Unreachable code");
	
	public static Diagnostic ReportBinaryOpMismatch(OperatorRegistry registry, IResolvedExpressionNode left, Token op,
		IResolvedExpressionNode right)
	{
		var message = $"No operation defined for '{op.Text}' between '{left.Type.Name}' and '{right.Type.Name}'";
		var hints = new List<string>();
		
		// One side is a pointer to the other side's type
		if (left.Type is PointerType leftPtr && leftPtr.BaseType == right.Type)
		{
			if (HasExactBinary(registry, leftPtr.BaseType, op.Type, right.Type))
			{
				hints.Add($"Did you mean '{ApplyUnary(TokenType.OpStar, left.Syntax)} {op.Text} " +
				          $"{right.Syntax.SourceLocation.GetText()}'?");
			}
		}
		else if (right.Type is PointerType rightPtr && rightPtr.BaseType == left.Type)
		{
			if (HasExactBinary(registry, left.Type, op.Type, rightPtr.BaseType))
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
	
	public static Diagnostic ReportUndefinedSymbol(ISyntaxNode node, string missingName,
		IEnumerable<string> availableNames)
	{
		var message = $"Symbol '{missingName}' not found in this scope";
		var hints = new List<string>();
		
		string? bestMatch = null;
		var bestDistance = int.MaxValue;
		
		if (missingName.Length > 1)
		{
			foreach (var name in availableNames)
			{
				if (Math.Abs(name.Length - missingName.Length) > 3)
					continue;
				
				var distance = LevenshteinDistance(missingName, name);
				var threshold = missingName.Length switch
				{
					> 5 => 3,
					> 2 => 2,
					_ => 1
				};
				
				if (distance > threshold || distance >= bestDistance)
					continue;
				
				bestDistance = distance;
				bestMatch = name;
			}
		}
		
		if (bestMatch is null)
		{
			// TODO Check for missing imports?
		}
		else
		{
			hints.Add($"Did you mean '{bestMatch}'?");
		}
		
		return new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation, message)
		{
			Hints = hints.ToImmutableArray()
		};
	}
	
	private static bool HasExactBinary(OperatorRegistry registry, TypeSymbol left, TokenType op,
		TypeSymbol right)
	{
		foreach (var candidate in registry.GetBinaryCandidates(op))
		{
			var parameters = candidate.ParameterTypes;
			if (parameters.Length == 2 && parameters[0] == left && parameters[1] == right)
				return true;
		}
		
		return false;
	}
	
	private static string ApplyUnary(TokenType op, IExpressionNode operand) => operand.IsContained
		? $"{op.Representation}{operand.SourceLocation.GetText()}"
		: $"{op.Representation}({operand.SourceLocation.GetText()})";
	
	private static int LevenshteinDistance(string source, string target)
	{
		if (string.IsNullOrEmpty(source))
			return string.IsNullOrEmpty(target) ? 0 : target.Length;
		
		if (string.IsNullOrEmpty(target))
			return source.Length;
		
		var v0 = new int[target.Length + 1];
		var v1 = new int[target.Length + 1];
		
		for (var i = 0; i < v0.Length; i++)
			v0[i] = i;
		
		for (var i = 0; i < source.Length; i++)
		{
			v1[0] = i + 1;
			
			for (var j = 0; j < target.Length; j++)
			{
				var cost = source[i] == target[j] ? 0 : 1;
				v1[j + 1] = Math.Min(v1[j] + 1, Math.Min(v0[j + 1] + 1, v0[j] + cost));
			}
			
			for (var j = 0; j < v0.Length; j++)
				v0[j] = v1[j];
		}
		
		return v1[target.Length];
	}
}