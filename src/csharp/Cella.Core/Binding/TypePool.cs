using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class TypePool(TypeMemberTable memberTable, ConversionTable conversionTable,
	OperatorRegistry operatorRegistry)
{
	private readonly Dictionary<(TypeSymbol, BigInteger), ArrayType> _arrayTypes = [];
	private readonly Dictionary<TypeSymbol, SpanType> _spanTypes = [];
	private readonly Dictionary<TypeSymbol, ViewType> _viewTypes = [];
	private readonly Dictionary<(TypeSymbol, PointerKind), PointerType> _pointerTypes = [];
	
	public TypeSymbol? ResolveBuiltinGenericType(string name, IReadOnlyList<IGenericArgument> typeArgs) => name switch
	{
		"ptr" when typeArgs.Count == 0 => NativeSymbols.VoidPtr,
		"ptr" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			GetPointerType(t, PointerKind.Unsafe),
		"mut" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			GetPointerType(t, PointerKind.Mutable),
		"imm" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			GetPointerType(t, PointerKind.Immutable),
		"own" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			GetPointerType(t, PointerKind.Owning),
		"span" when typeArgs is [GenericTypeArgument { Type: { } t }] => GetSpanType(t),
		"view" when typeArgs is [GenericTypeArgument { Type: { } t }] => GetViewType(t),
		"array" when typeArgs is [GenericTypeArgument { Type: { } t }, GenericConstArgument { Value: var v }] =>
			GetArrayType(t, v),
		"array" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			new ArrayType(t, BigInteger.MinusOne), // Don't use GetArrayType; we don't want to actually create it
		_ => null
	};
	
	public PointerType GetPointerType(TypeSymbol baseType, PointerKind kind)
	{
		var key = (baseType, kind);
		if (_pointerTypes.TryGetValue(key, out var existing))
			return existing;
		
		var ptrType = new PointerType(baseType, kind);
		_pointerTypes[key] = ptrType;
		
		// Conversions
		switch (kind)
		{
			case PointerKind.Unsafe:
				break;
			
			default:
				// All pointers except ptr[T] can be implicitly converted to ptr[T]
				var unsafeType = new PointerType(baseType, PointerKind.Unsafe);
				conversionTable.Add(new NativeConversion(ptrType, unsafeType, ConversionKind.Implicit, 0));
				break;
		}
		
		// All pointers can be implicitly converted to ptr / explicitly converted from ptr
		conversionTable.Add(new NativeConversion(ptrType, PointerType.VoidPtr, ConversionKind.Implicit, 0));
		conversionTable.Add(new NativeConversion(PointerType.VoidPtr, ptrType, ConversionKind.Explicit, 0));
		
		return ptrType;
	}
	
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
		
		// To pointer
		var ptrType = GetPointerType(elementType, PointerKind.Unsafe);
		operatorRegistry.CreateUnary(TokenType.OpAt, arrayType, new NativeImpl(TokenType.OpAt, ptrType));
		
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

public interface IGenericArgument;

public sealed record GenericTypeArgument(TypeSymbol Type) : IGenericArgument;
public sealed record GenericConstArgument(BigInteger Value) : IGenericArgument;