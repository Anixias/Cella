using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class TypePool(TypeMemberTable memberTable, ConversionTable conversionTable)
{
	private readonly Dictionary<(TypeSymbol, BigInteger), ArrayType> _arrayTypes = [];
	private readonly Dictionary<TypeSymbol, SpanType> _spanTypes = [];
	private readonly Dictionary<TypeSymbol, ViewType> _viewTypes = [];
	
	public ArrayType GetArrayType(TypeSymbol elementType, BigInteger length)
	{
		var key = (elementType, length);
		if (_arrayTypes.TryGetValue(key, out var existing))
			return existing;
		
		var arrayType = new ArrayType(elementType, length);
		_arrayTypes[key] = arrayType;
		memberTable.CreateArrayMembers(arrayType);
		
		// To span
		var spanType = GetSpanType(elementType);
		conversionTable.Add(new NativeConversion(arrayType, spanType, ConversionKind.Implicit, 1));
		
		// To view
		var viewType = GetViewType(elementType);
		conversionTable.Add(new NativeConversion(arrayType, viewType, ConversionKind.Implicit, 1));
		
		return arrayType;
	}
	
	public SpanType GetSpanType(TypeSymbol elementType)
	{
		if (_spanTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var spanType = new SpanType(elementType);
		_spanTypes[elementType] = spanType;
		memberTable.CreateSpanMembers(spanType);
		
		// To view
		var viewType = GetViewType(elementType);
		conversionTable.Add(new NativeConversion(spanType, viewType, ConversionKind.Implicit, 1));
		
		return spanType;
	}
	
	public ViewType GetViewType(TypeSymbol elementType)
	{
		if (_viewTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var viewType = new ViewType(elementType);
		_viewTypes[elementType] = viewType;
		memberTable.CreateViewMembers(viewType);
		return viewType;
	}
}