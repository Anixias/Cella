using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedExpressionNode Visit(IsExpressionNode node)
	{
		var isMut = node.Mode is not null;
		var (value, _) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, isMut, [node.Pattern]);
		var ownsValue = OwnsValue(value);
		if (ResolveTopPattern(node.Pattern, value.Type, isMut, ownsValue) is { } pattern)
			return new ResolvedIsExpressionNode(value, pattern, isMut || pattern.HasMutBindings, ownsValue, node);
		
		var invalid = new ResolvedInvalidExpressionNode(node, NativeSymbols.Bool);
		_failedPatterns[invalid] = [..CreateBindings(node.Pattern, isMut, ownsValue)];
		return invalid;
	}
	
	private (List<ResolvedPattern> Patterns, List<LocalVariableSymbol?> Bindings) ResolveArmPatterns(
		ImmutableArray<PatternNode> syntax, TypeSymbol subjectType, bool isMut, bool ownsValue)
	{
		var patterns = new List<ResolvedPattern>();
		var bindings = new List<LocalVariableSymbol?>();
		foreach (var pattern in syntax)
		{
			if (ResolveTopPattern(pattern, subjectType, isMut, ownsValue) is { } resolved)
			{
				patterns.Add(resolved);
				bindings.AddRange(resolved.AllBindings);
			}
			else
			{
				bindings.AddRange(CreateBindings(pattern, isMut, ownsValue));
			}
		}
		
		if (syntax.Length > 1)
		{
			foreach (var binding in bindings.OfType<LocalVariableSymbol>())
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					"Cannot bind names in arms with several patterns"));
		}
		
		return (patterns, bindings);
	}
	
	private IResolvedExpressionNode? ResolveGuard(IExpressionNode? guard, List<LocalVariableSymbol?> bindings)
	{
		if (guard is null)
			return null;
		
		foreach (var binding in bindings.OfType<LocalVariableSymbol>())
		{
			if (!binding.IsBorrowBinding && !_typePool.IsCopy(binding.Type))
				Diagnostics.Add(new(DiagnosticSeverity.Error, binding.Identifier.SourceLocation,
					"Cannot move values in arms with 'if'"));
		}
		
		return VisitInScope(guard, bindings, NativeSymbols.Bool);
	}
	
	public IResolvedStatementNode Visit(MatchStatementNode node)
	{
		var isMut = node.Mode is not null;
		var (value, enumType) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, isMut,
			node.Arms.SelectMany(static arm => arm.Patterns));
		
		var ownsValue = OwnsValue(value);
		var arms = new List<ResolvedMatchArm>(node.Arms.Length);
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		foreach (var arm in node.Arms)
		{
			var (patterns, bindings) = ResolveArmPatterns(arm.Patterns, value.Type, isMut, ownsValue);
			var guard = ResolveGuard(arm.Guard, bindings);
			ReportDeclarationBody(arm.Body);
			arms.Add(new ResolvedMatchArm(patterns, guard, VisitInScope(arm.Body, bindings)));
			summaries.AddRange(SummarizeArm(arm.Patterns, patterns, guard, enumType, arm.SourceLocation));
		}
		
		ReportArmConflicts(summaries);
		ReportUnreachableElse(value.Type, summaries,
			arms.Where(static arm => arm.Guard is null).SelectMany(static arm => arm.Patterns));
		
		var borrowsMut = isMut || arms.Any(static arm => arm.Patterns.Any(static pattern => pattern.HasMutBindings));
		return new ResolvedMatchStatementNode(value, arms, borrowsMut, ownsValue, node);
	}
	
	public IResolvedExpressionNode Visit(MatchExpressionNode node)
	{
		var target = CurrentTargetType;
		var hint = target is null ? ExpectedType : null;
		var isMut = node.Mode is not null;
		var (value, enumType) = ResolveMatchedValue(node.Value);
		value = TakeOwnership(value, node.Value, isMut,
			node.Arms.SelectMany(static arm => arm.Patterns));
		
		var ownsValue = OwnsValue(value);
		var patterns = new List<List<ResolvedPattern>>(node.Arms.Length);
		var guards = new List<IResolvedExpressionNode?>(node.Arms.Length);
		var values = new IResolvedExpressionNode?[node.Arms.Length];
		var summaries = new List<MatchArmSummary>(node.Arms.Length);
		var bare = new List<(int Index, IExpressionNode Value, IEnumerable<LocalVariableSymbol?> Bindings)>();
		for (var i = 0; i < node.Arms.Length; i++)
		{
			var arm = node.Arms[i];
			var (armPatterns, bindings) = ResolveArmPatterns(arm.Patterns, value.Type, isMut, ownsValue);
			var guard = ResolveGuard(arm.Guard, bindings);
			patterns.Add(armPatterns);
			guards.Add(guard);
			if (arm.Value is not { } armValue)
				values[i] = new ResolvedInvalidExpressionNode(node);
			else if (target is null && IsCaseNameSyntax(armValue) &&
			         !bindings.Any(binding => binding?.Name == GetCaseToken(armValue)?.Text))
				bare.Add((i, armValue, bindings));
			else
				values[i] = VisitInScope(armValue, bindings, target, hint);
			
			summaries.AddRange(SummarizeArm(arm.Patterns, armPatterns, guard, enumType, arm.SourceLocation));
		}
		
		List<TypeSymbol> peers =
		[
			..values.OfType<IResolvedExpressionNode>().Select(static value => value.Type),
			..hint is null ? [] : new[] { hint }
		];
		
		foreach (var (i, armValue, bindings) in bare)
			values[i] = VisitCaseName(armValue, peers) ?? VisitInScope(armValue, bindings);
		
		ReportArmConflicts(summaries);
		var unguarded = patterns.Where((_, i) => guards[i] is null).SelectMany(static armPatterns => armPatterns)
			.ToList();
		
		ReportUnreachableElse(value.Type, summaries, unguarded);
		if (!summaries.Any(static arm => arm.IsElse || arm.IsFailed) && !IsInvalid(value))
			ReportUncovered(node, value.Type, unguarded);
		
		var arms = values
			.Select(value => value is ResolvedCaseNameExpressionNode caseName && HasCase(hint, caseName.Name.Text)
				? MaterializeExpression(caseName, hint!)
				: value!)
			.ToList();
		
		var valued = arms.Where(static arm => arm.Type is not NeverType).ToList();
		var type = target ??
		           (valued is [{ Type: UntypedType and not (UntypedNullType or UntypedUndefType) } first, ..] &&
		            valued.All(arm => arm.Type == first.Type)
			           ? first.Type
			           : UnifyTypes(arms));
		
		if (type is null)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Keyword.SourceLocation,
				HoldsDistinctClosures(arms.Select(static v => v.Type))
					? "The arms of this match hold different 'own fun' closures"
					: $"The arms of this match have incompatible types: {JoinTypeNames(arms)}"));
			
			type = NativeSymbols.Invalid;
		}
		
		var resolved = arms.Select((v, i) =>
			new ResolvedMatchExpressionArm(patterns[i], guards[i], CoerceToType(v, type)));
		
		var borrowsMut = isMut || patterns.Any(static armPatterns => armPatterns.Any(static p => p.HasMutBindings));
		return new ResolvedMatchExpressionNode(value, resolved, borrowsMut, ownsValue, type, node);
	}
	
	private readonly record struct MatchArmSummary
	(
		bool IsElse,
		EnumCaseSymbol? Case,
		BigInteger? Value,
		SourceLocation Location
	)
	{
		public TypeSymbol? Type { get; init; }
		public object? Constant { get; init; }
		public bool IsFailed { get; init; }
	}
	
	private IEnumerable<MatchArmSummary> SummarizeArm(ImmutableArray<PatternNode> syntax,
		List<ResolvedPattern> patterns, IResolvedExpressionNode? guard, EnumSymbol? enumType, SourceLocation location)
	{
		if (syntax.IsEmpty)
			return [new MatchArmSummary(true, null, null, location)];
		
		if (guard is not null || patterns.Count != syntax.Length)
			return [new MatchArmSummary(false, null, null, location) { IsFailed = patterns.Count != syntax.Length }];
		
		return patterns.Select((pattern, i) =>
		{
			var enumCase = pattern.IsConditional ? null : pattern.Case;
			return new MatchArmSummary(false, enumCase,
				enumCase is not null ? _typePool.GetCaseValue(enumType!, enumCase) : null, syntax[i].SourceLocation)
			{
				Type = pattern.IsConditional ? null : pattern.TestedType,
				Constant = pattern.Constant switch
				{
					IntegerConstant integer => integer.Value,
					BoolConstant flag => flag.Value,
					StringConstant text => text.Value,
					_ => null
				}
			};
		});
	}
	
	private void ReportArmConflicts(IReadOnlyList<MatchArmSummary> arms)
	{
		var elseArms = arms.Where(static arm => arm.IsElse).ToList();
		if (elseArms.Count > 1)
		{
			foreach (var arm in elseArms)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location, "A match can have only one 'else' arm"));
		}
		else if (elseArms.Count == 1 && !arms[^1].IsElse)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, elseArms[0].Location, "'else' must be the last arm"));
		}
		
		var repeatedTypes = arms
			.Where(static arm => arm.Type is not null)
			.GroupBy(static arm => arm.Type)
			.Where(static sameType => sameType.Count() > 1);
		
		foreach (var sameType in repeatedTypes)
		{
			foreach (var arm in sameType)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location,
					$"'{sameType.Key!.Name}' is matched more than once"));
		}
		
		var repeatedValues = arms
			.Where(static arm => arm.Constant is not null)
			.GroupBy(static arm => arm.Constant)
			.Where(static sameValue => sameValue.Count() > 1);
		
		foreach (var sameValue in repeatedValues)
		{
			List<string> texts = [..sameValue.Select(static arm => arm.Location.GetText().ToString()).Distinct()];
			var message = texts is [var text]
				? $"'{text}' is matched more than once"
				: $"{DiagnosticReporter.JoinNames(texts)} are the same value";
			
			foreach (var arm in sameValue)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location, message));
		}
		
		var repeated = arms
			.Where(static arm => arm.Value is not null)
			.GroupBy(static arm => arm.Value)
			.Where(static sameCase => sameCase.Count() > 1);
		
		foreach (var sameCase in repeated)
		{
			List<string> names = [..sameCase.Select(static arm => arm.Case!.Name).Distinct()];
			var message = names is [var name]
				? $"'{name}' is matched more than once"
				: $"{DiagnosticReporter.JoinNames(names)} are the same case";
			
			foreach (var arm in sameCase)
				Diagnostics.Add(new(DiagnosticSeverity.Error, arm.Location, message));
		}
	}
	
	private void ReportUnreachableElse(TypeSymbol type, List<MatchArmSummary> arms,
		IEnumerable<ResolvedPattern> patterns)
	{
		if (arms is [.., { IsElse: true } last] && arms.Count(static arm => arm.IsElse) == 1 &&
		    !arms.Any(static arm => arm.IsFailed) && !IsInvalid(type) && _coverage.Covers(type, patterns))
			Diagnostics.Add(DiagnosticReporter.ReportUnreachableCode(last.Location) with
			{
				UnnecessaryLocation = last.Location,
				Fixes = [DiagnosticFix.RemoveListItem(DiagnosticReporter.RemoveUnreachableTitle, last.Location, ',')]
			});
	}
	
	private void ReportUncovered(MatchExpressionNode node, TypeSymbol type, List<ResolvedPattern> patterns)
	{
		var keyword = node.Keyword;
		if (type is not EnumSymbol { IsExternal: false } enumType)
		{
			if (!_coverage.Covers(type, patterns))
				Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation,
					$"This match doesn't handle every value of '{type.Name}'"));
			
			return;
		}
		
		var missing = _coverage.FindMissingCases(enumType, patterns).ToList();
		if (missing.Count == 0)
			return;
		
		var names = DiagnosticReporter.JoinNames([..missing.Select(static enumCase => enumCase.Name)]);
		Diagnostics.Add(new(DiagnosticSeverity.Error, keyword.SourceLocation,
			$"This match doesn't handle every case of '{enumType.Name}': {names}")
		{
			Fixes = AddMissingArms(node, missing) is { } fix ? [fix] : []
		});
	}
	
	private static DiagnosticFix? AddMissingArms(MatchExpressionNode node, IEnumerable<EnumCaseSymbol> missing)
	{
		if (node.Arms is not [.., var last])
			return null;
		
		var source = node.SourceLocation.Source;
		var end = Math.Max(last.SourceLocation.Range.End, last.Value?.SourceLocation.Range.End ?? 0);
		var arms = missing.Select(static enumCase => enumCase.Fields.IsEmpty
			? $"{enumCase.Name} => panic"
			: $"{enumCase.Name}({string.Join(", ", enumCase.Fields.Select(static _ => "_"))}) => panic");
		
		var isOneLine = source.GetLineColumn(end).Line == source.GetLineColumn(node.SourceLocation.Range.End - 1).Line;
		var indent = source.FindIndent(last.SourceLocation.Range.Start);
		var newline = source.FindNewline();
		var text = isOneLine
			? string.Concat(arms.Select(static arm => $", {arm}"))
			: string.Concat(arms.Select(arm => $"{newline}{indent}{arm}"));
		
		return DiagnosticFix.Replace("Add missing cases", new(source, new(end, end)), text);
	}
}