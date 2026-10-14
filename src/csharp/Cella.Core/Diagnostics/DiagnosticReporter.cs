using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Diagnostics;

public static class DiagnosticReporter
{
	public static string DescribeFunction(FunctionSymbol function, bool startsSentence = false) =>
		function.Syntax is LambdaDeclarationNode
			? startsSentence ? "This lambda" : "this lambda"
			: $"'{function.Name}'";
	
	public static Diagnostic ReportUnreachableCode(SourceLocation location) =>
		new(DiagnosticSeverity.Warning, location, "Unreachable code");
	
	public static Diagnostic ReportBinaryOpMismatch(OperatorRegistry registry, IResolvedExpressionNode left, Token op,
		IResolvedExpressionNode right)
	{
		if ((FindInexactInteger(left, right) ?? FindInexactInteger(right, left)) is var (literal, floatType))
			return new(DiagnosticSeverity.Error, literal.Syntax.SourceLocation,
				$"'{literal.Syntax.SourceLocation.GetText()}' can't be represented exactly in '{floatType}'");
		
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
	
	private static (IResolvedExpressionNode Literal, string FloatType)? FindInexactInteger(
		IResolvedExpressionNode literal, IResolvedExpressionNode other)
	{
		if (literal is not ResolvedLiteralExpressionNode { Type: UntypedIntegerType, Value: BigInteger value })
			return null;
		
		return other.Type switch
		{
			FloatType { Kind: PrimitiveTypeKind.Float32 } type when new BigInteger((float)value) != value =>
				(literal, type.Name),
			FloatType type when new BigInteger((double)value) != value => (literal, type.Name),
			UntypedFloatType when new BigInteger((double)value) != value => (literal, NativeSymbols.Float64.Name),
			_ => null
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
	
	public static Diagnostic ReportRefFunctionParameter(FunctionTypeNode function, string? name, bool hasMode,
		bool isExternal)
	{
		var (source, range) = function.SourceLocation;
		var target = source.GetText(new TextRange(function.Keyword.SourceLocation.Range.Start, range.End)).ToString();
		var parameter = name is null ? target : $"{name}: {target}";
		return new Diagnostic(DiagnosticSeverity.Error, function.SourceLocation,
			"Cannot use borrow types for parameters")
		{
			Hints = hasMode || isExternal ? [] : [$"'{parameter}' is already a read-only borrow"]
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
		
		if (module.PrivateMembers.TryGetValue(missingName, out var hidden))
			return ReportHidden(location, $"{module.Path}.{missingName}", hidden.Max(ModuleIndex.GetVisibility), false);
		
		return ReportUndefined(location, $"Module '{module.Path}' has no member '{missingName}'", missingName,
			memberNames.Concat(module.Children.Keys));
	}
	
	public static Diagnostic ReportHidden(SourceLocation location, string name, Visibility visibility, bool isMember) =>
		ReportHidden(location, name, DescribeScope(visibility, isMember));
	
	public static Diagnostic ReportHidden(SourceLocation location, string name, string scope) =>
		new(DiagnosticSeverity.Error, location, $"'{name}' is private to its {scope}");
	
	public static Diagnostic ReportReadOnly(SourceLocation location, string name, Visibility visibility) =>
		new(DiagnosticSeverity.Error, location, $"'{name}' is read-only outside its {DescribeScope(visibility, true)}");
	
	public static Diagnostic ReportWriteOnly(SourceLocation location, string name, Visibility visibility) =>
		new(DiagnosticSeverity.Error, location,
			$"'{name}' is write-only outside its {DescribeScope(visibility, true)}");
	
	private static string DescribeScope(Visibility visibility, bool isMember) => visibility switch
	{
		Visibility.Project => "project",
		Visibility.Private => isMember ? "type" : "file",
		_ => "module"
	};
	
	public static Diagnostic ReportUndefinedCase(SourceLocation location, string enumName, string missingName,
		IEnumerable<string> caseNames) =>
		ReportUndefined(location, $"'{enumName}' has no case '{missingName}'", missingName, caseNames);
	
	public static string DescribeType(TypeSymbol type) => type is NamedTypeSymbol { IsGenericDefinition: true } generic
		? $"{generic.Name}[{string.Join(", ", generic.TypeParameters.Select(static parameter => parameter.Name))}]"
		: type.Name;
	
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
		IEnumerable<string> availableNames) =>
		new(DiagnosticSeverity.Error, location, message)
		{
			Hints = FindSuggestion(missingName, availableNames) is { } hint ? [hint] : []
		};
	
	public static string? FindSuggestion(string missingName, IEnumerable<string> availableNames)
	{
		string? bestMatch = null;
		var bestDistance = int.MaxValue;
		
		if (missingName.Length > 1)
		{
			foreach (var name in availableNames)
			{
				if (Math.Abs(name.Length - missingName.Length) > 3 || name == missingName)
					continue;
				
				var distance = string.Equals(name, missingName, StringComparison.OrdinalIgnoreCase)
					? 0
					: EditDistance(missingName, name);
				
				var threshold = missingName.Length switch
				{
					> 5 => 3,
					> 4 => 2,
					_ => 1
				};
				
				if (distance > threshold || distance >= bestDistance)
					continue;
				
				bestDistance = distance;
				bestMatch = name;
			}
		}
		
		return bestMatch is null ? null : $"Did you mean '{bestMatch}'?";
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
	
	private static int EditDistance(string source, string target)
	{
		var distances = new int[source.Length + 1, target.Length + 1];
		for (var i = 0; i <= source.Length; i++)
			distances[i, 0] = i;
		
		for (var j = 0; j <= target.Length; j++)
			distances[0, j] = j;
		
		for (var i = 1; i <= source.Length; i++)
		{
			for (var j = 1; j <= target.Length; j++)
			{
				var cost = source[i - 1] == target[j - 1] ? 0 : 1;
				var distance = Math.Min(Math.Min(distances[i - 1, j] + 1, distances[i, j - 1] + 1),
					distances[i - 1, j - 1] + cost);
				
				if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1])
					distance = Math.Min(distance, distances[i - 2, j - 2] + 1);
				
				distances[i, j] = distance;
			}
		}
		
		return distances[source.Length, target.Length];
	}
}