using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class PatternCoverage(TypePool typePool, ConstantEvaluator evaluator)
{
	public bool Covers(TypeSymbol type, IEnumerable<ResolvedPattern> patterns) =>
		Covers(CreateRows(patterns), [type]);

	public IEnumerable<EnumCaseSymbol> FindMissingCases(EnumSymbol type, IEnumerable<ResolvedPattern> patterns)
	{
		var rows = CreateRows(patterns);
		return type.Cases
			.Where(enumCase => !CoversCase(rows, type, enumCase, []))
			.DistinctBy(enumCase => typePool.GetCaseValue(type, enumCase));
	}

	private static List<ImmutableList<ResolvedPattern?>> CreateRows(IEnumerable<ResolvedPattern> patterns) =>
		[..patterns.Select(static pattern => ImmutableList.Create<ResolvedPattern?>(pattern))];

	private bool Covers(List<ImmutableList<ResolvedPattern?>> rows, ImmutableList<TypeSymbol> types)
	{
		if (rows.Count == 0)
			return false;

		if (types.IsEmpty)
			return true;

		var rest = types.RemoveAt(0);
		if (rows.All(static row => row[0] is null))
			return Covers([..rows.Select(static row => row.RemoveAt(0))], rest);

		switch (types[0])
		{
			case EnumSymbol { IsExternal: false } enumType:
				return enumType.Cases.All(enumCase => CoversCase(rows, enumType, enumCase, rest));

			case RecordSymbol record when rows.Any(static row => row[0] is { Record: not null, TestedType: null }):
				return CoversFields(rows, record, rest);

			case var type when FindLimits(type) is var (min, max):
				return CoversSpans(rows, min, max, rest);

			default:
				return Covers([..rows.Where(static row => row[0] is null).Select(static row => row.RemoveAt(0))], rest);
		}
	}

	private bool CoversCase(List<ImmutableList<ResolvedPattern?>> rows, EnumSymbol enumType, EnumCaseSymbol enumCase,
		ImmutableList<TypeSymbol> rest)
	{
		var payloadTypes = typePool.GetPayloadTypes(enumType, enumCase);
		var value = typePool.GetCaseValue(enumType, enumCase);
		var expanded = new List<ImmutableList<ResolvedPattern?>>();
		foreach (var row in rows)
		{
			var tail = row.RemoveAt(0);
			if (row[0] is null)
				expanded.Add(tail.InsertRange(0, Enumerable.Repeat<ResolvedPattern?>(null, payloadTypes.Length)));
			else if (row[0]!.Case is { } covered && typePool.GetCaseValue(enumType, covered) == value)
				expanded.Add(tail.InsertRange(0,
					payloadTypes.Select((_, i) => i < row[0]!.Elements.Length ? row[0]!.Elements[i] : null)));
		}

		return Covers(expanded, rest.InsertRange(0, payloadTypes));
	}

	private bool CoversFields(List<ImmutableList<ResolvedPattern?>> rows, RecordSymbol record,
		ImmutableList<TypeSymbol> rest)
	{
		var fields = typePool.GetMembers(record).OfType<FieldSymbol>().ToList();
		var expanded = new List<ImmutableList<ResolvedPattern?>>();
		foreach (var row in rows)
		{
			var tail = row.RemoveAt(0);
			if (row[0] is not { } head)
				expanded.Add(tail.InsertRange(0, Enumerable.Repeat<ResolvedPattern?>(null, fields.Count)));
			else if (head is { Record: not null, TestedType: null })
				expanded.Add(tail.InsertRange(0, fields.Select(field =>
					head.Fields.IndexOf(field) is var index and >= 0 ? head.Elements[index] : null)));
		}

		return Covers(expanded, rest.InsertRange(0, fields.Select(typePool.GetTypeOfMember)));
	}

	private bool CoversSpans(List<ImmutableList<ResolvedPattern?>> rows, BigInteger min, BigInteger max,
		ImmutableList<TypeSymbol> rest)
	{
		var points = new SortedSet<BigInteger> { min, max + 1 };
		foreach (var (low, high) in rows.SelectMany(static row => row[0]?.Spans ?? []))
		{
			points.Add(BigInteger.Clamp(low, min, max + 1));
			points.Add(BigInteger.Clamp(high + 1, min, max + 1));
		}

		var bounds = points.ToList();
		for (var i = 0; i < bounds.Count - 1; i++)
		{
			var (low, high) = (bounds[i], bounds[i + 1] - 1);
			var matching = rows
				.Where(row => row[0] is null || row[0]!.Spans.Any(span => span.Low <= low && high <= span.High))
				.Select(static row => row.RemoveAt(0))
				.ToList();

			if (!Covers(matching, rest))
				return false;
		}

		return true;
	}

	private (BigInteger Min, BigInteger Max)? FindLimits(TypeSymbol type) => type switch
	{
		IntegerType integer => evaluator.GetLimits(integer),
		_ when type == NativeSymbols.Bool => (BigInteger.Zero, BigInteger.One),
		_ => null
	};
}
