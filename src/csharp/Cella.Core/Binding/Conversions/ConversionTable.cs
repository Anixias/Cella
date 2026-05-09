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
				// Always explicit and costs more than widening
				table.Add(new IntegerConversion(intTypes[i].S, intTypes[j].S, ConversionKind.Explicit, 2));
				table.Add(new IntegerConversion(intTypes[i].U, intTypes[j].U, ConversionKind.Explicit, 2));
				table.Add(new IntegerConversion(intTypes[i].S, intTypes[j].U, ConversionKind.Explicit, 2));
				table.Add(new IntegerConversion(intTypes[i].U, intTypes[j].S, ConversionKind.Explicit, 2));
			}
		}
		
		// Integer sign changing & to/from size types
		for (var i = 0; i < intTypes.Length; i++)
		{
			// Sign change
			// Always explicit, no cost due to no runtime overhead
			table.Add(new IntegerConversion(intTypes[i].S, intTypes[i].U, ConversionKind.Explicit, 0));
			table.Add(new IntegerConversion(intTypes[i].U, intTypes[i].S, ConversionKind.Explicit, 0));
			
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
			
			// Any integer type -> char
			// Explicit for all except i32/u32
			if (intTypes[i].U == NativeSymbols.UInt32)
				table.Add(new FreeConversion(NativeSymbols.UInt32, NativeSymbols.Char, ConversionKind.Implicit));
			else
				table.Add(new IntegerConversion(intTypes[i].U, NativeSymbols.Char, ConversionKind.Explicit, 1));
			
			if (intTypes[i].S == NativeSymbols.Int32)
				table.Add(new FreeConversion(NativeSymbols.Int32, NativeSymbols.Char, ConversionKind.Implicit));
			else
				table.Add(new IntegerConversion(intTypes[i].S, NativeSymbols.Char, ConversionKind.Explicit, 1));
		}
		
		// char -> Any integer type
		// Explicit if dest smaller than char (u32) or usize/isize
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.Int8, ConversionKind.Explicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.UInt8, ConversionKind.Explicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.Int16, ConversionKind.Explicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.UInt16, ConversionKind.Explicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.Int32, ConversionKind.Explicit, 1));
		table.Add(new FreeConversion(NativeSymbols.Char, NativeSymbols.UInt32, ConversionKind.Implicit));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.Int64, ConversionKind.Implicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.UInt64, ConversionKind.Implicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.Int128, ConversionKind.Implicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.UInt128, ConversionKind.Implicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.IntSize, ConversionKind.Explicit, 1));
		table.Add(new IntegerConversion(NativeSymbols.Char, NativeSymbols.UIntSize, ConversionKind.Explicit, 1));
		
		// cstr <-> ptr
		table.Add(new FreeConversion(NativeSymbols.CStr, NativeSymbols.VoidPtr, ConversionKind.Implicit));
		table.Add(new FreeConversion(NativeSymbols.VoidPtr, NativeSymbols.CStr, ConversionKind.Explicit));
		
		return table;
	}
}