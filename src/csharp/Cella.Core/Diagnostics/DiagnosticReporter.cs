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
	
	public static Diagnostic ReportBorrowParameter(BorrowTypeNode borrow, string? name, bool hasMode,
		bool isExternal)
	{
		var target = borrow.Target.SourceLocation.GetText();
		var parameter = name is null ? target : $"{name}: {target}";
		string? hint = (hasMode, borrow.IsMutable, isExternal) switch
		{
			(false, true, _) => $"Did you mean 'mut {parameter}'?",
			(false, false, false) => $"'{parameter}' is already a read-only borrow",
			_ => null
		};
		
		return new Diagnostic(DiagnosticSeverity.Error, borrow.SourceLocation, "Cannot use borrow types for parameters")
		{
			Hints = hint is null ? [] : [hint]
		};
	}
	
	public static Diagnostic ReportUndefinedSymbol(ISyntaxNode node, string missingName,
		IEnumerable<string> availableNames) =>
		ReportUndefined(node.SourceLocation, $"Symbol '{missingName}' not found in this scope", missingName,
			availableNames);
	
	public static Diagnostic ReportUndefinedType(SourceLocation location, string missingName,
		IEnumerable<string> availableNames) =>
		ReportUndefined(location, $"Type '{missingName}' not found in this scope", missingName, availableNames);
	
	public static Diagnostic ReportUndefinedModule(SourceLocation location, string missingName,
		IEnumerable<string> availableNames) =>
		ReportUndefined(location, $"Module '{missingName}' not found", missingName, availableNames);
	
	public static Diagnostic ReportUndefinedMember(SourceLocation location, ModulePathSymbol module, string missingName,
		IEnumerable<string> memberNames)
	{
		if (module.Parent is null)
			return ReportUndefinedModule(location, missingName, module.Children.Keys);
		
		if (module.PrivateMembers.ContainsKey(missingName))
			return new(DiagnosticSeverity.Error, location, $"'{module.Path}.{missingName}' isn't public");
		
		return ReportUndefined(location, $"Module '{module.Path}' has no member '{missingName}'", missingName,
			memberNames.Concat(module.Children.Keys));
	}
	
	public static Diagnostic ReportHidden(SourceLocation location, string name, Visibility visibility, bool isMember) =>
		new(DiagnosticSeverity.Error, location, $"'{name}' is private to its {DescribeScope(visibility, isMember)}");
	
	public static Diagnostic ReportReadOnly(SourceLocation location, string name, Visibility visibility) =>
		new(DiagnosticSeverity.Error, location, $"'{name}' is read-only outside its {DescribeScope(visibility, true)}");
	
	private static string DescribeScope(Visibility visibility, bool isMember) =>
		visibility != Visibility.Private ? "module" : isMember ? "type" : "file";
	
	public static Diagnostic ReportUndefinedCase(SourceLocation location, string enumName, string missingName,
		IEnumerable<string> caseNames) =>
		ReportUndefined(location, $"'{enumName}' has no case '{missingName}'", missingName, caseNames);
	
	public static string JoinNames(IReadOnlyList<string> names) => names.Count == 1
		? $"'{names[0]}'"
		: $"{string.Join(", ", names.Take(names.Count - 1).Select(static name => $"'{name}'"))} and '{names[^1]}'";
	
	public static IEnumerable<Diagnostic> ReportDuplicates(IEnumerable<Token> identifiers,
		Func<string, string> describe) => identifiers
		.GroupBy(static identifier => identifier.Text)
		.Where(static sameName => sameName.Count() > 1)
		.SelectMany(sameName => sameName.Select(identifier =>
			new Diagnostic(DiagnosticSeverity.Error, identifier.SourceLocation, describe(sameName.Key))));
	
	private static Diagnostic ReportUndefined(SourceLocation location, string message, string missingName,
		IEnumerable<string> availableNames)
	{
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
		
		return new Diagnostic(DiagnosticSeverity.Error, location, message)
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