using Cella.Core.Symbols;

namespace Cella.Core.Binding.Conversions;

public sealed class ConversionTable
{
	private readonly Dictionary<(TypeSymbol From, TypeSymbol To), Conversion> _conversions = [];
	
	public void Add(Conversion conversion) => _conversions[(conversion.From, conversion.To)] = conversion;
	
	public IEnumerable<Conversion> FindAllImplicit(TypeSymbol type)
	{
		foreach (var ((from, _), conversion) in _conversions)
		{
			if (from != type)
				continue;
			
			if (conversion.Kind == ConversionKind.Implicit)
				yield return conversion;
		}
	}
	
	// TODO Chaining?
	public Conversion? FindImplicit(TypeSymbol from, TypeSymbol to) =>
		_conversions.TryGetValue((from, to), out var conversion) && conversion.Kind == ConversionKind.Implicit
			? conversion
			: null;
	
	// TODO Chaining?
	public Conversion? FindExplicit(TypeSymbol from, TypeSymbol to) =>
		_conversions.GetValueOrDefault((from, to));
	
	public static ConversionTable CreateNative()
	{
		var table = new ConversionTable();
		
		// Create types sorted by size (ascending)
		var intTypes = new (IntegerType S, IntegerType U)[]
		{
			(NativeSymbols.Int8, NativeSymbols.UInt8),
			(NativeSymbols.Int16, NativeSymbols.UInt16),
			(NativeSymbols.Int32, NativeSymbols.UInt32),
			(NativeSymbols.Int64, NativeSymbols.UInt64),
			(NativeSymbols.Int128, NativeSymbols.UInt128)
		};
		
		// TODO Floating point types
		
		// Integer widening
		for (var i = 0; i < intTypes.Length - 1; i++)
		{
			for (var j = i + 1; j < intTypes.Length; j++)
			{
				// Sign change must be explicit
				table.Add(new IntegerConversion(intTypes[i].S, intTypes[j].S, ConversionKind.Implicit, 1));
				table.Add(new IntegerConversion(intTypes[i].U, intTypes[j].U, ConversionKind.Implicit, 1));
				table.Add(new IntegerConversion(intTypes[i].S, intTypes[j].U, ConversionKind.Explicit, 1));
				table.Add(new IntegerConversion(intTypes[i].U, intTypes[j].S, ConversionKind.Explicit, 1));
			}
		}
		
		// Integer narrowing
		for (var i = intTypes.Length - 1; i > 0; i--)
		{
			for (var j = 0; j < i; j++)
			{
				// Always explicit
				table.Add(new IntegerConversion(intTypes[i].S, intTypes[j].S, ConversionKind.Explicit, 1));
				table.Add(new IntegerConversion(intTypes[i].U, intTypes[j].U, ConversionKind.Explicit, 1));
				table.Add(new IntegerConversion(intTypes[i].S, intTypes[j].U, ConversionKind.Explicit, 1));
				table.Add(new IntegerConversion(intTypes[i].U, intTypes[j].S, ConversionKind.Explicit, 1));
			}
		}
		
		// Integer sign changing & to/from size types
		for (var i = 0; i < intTypes.Length; i++)
		{
			// Sign change
			// Always explicit
			table.Add(new IntegerConversion(intTypes[i].S, intTypes[i].U, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(intTypes[i].U, intTypes[i].S, ConversionKind.Explicit, 1));
			
			// Integer native size conversions
			// These are always explicit because it depends on the target whether it is a widening/narrowing/neither
			table.Add(new IntegerConversion(intTypes[i].S, NativeSymbols.IntSize, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(intTypes[i].U, NativeSymbols.IntSize, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(NativeSymbols.IntSize, intTypes[i].S, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(NativeSymbols.IntSize, intTypes[i].U, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(intTypes[i].S, NativeSymbols.UIntSize, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(intTypes[i].U, NativeSymbols.UIntSize, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(NativeSymbols.UIntSize, intTypes[i].S, ConversionKind.Explicit, 1));
			table.Add(new IntegerConversion(NativeSymbols.UIntSize, intTypes[i].U, ConversionKind.Explicit, 1));
		}
		
		return table;
	}
}