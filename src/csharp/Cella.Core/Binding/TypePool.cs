using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class TypePool
{
	public ConversionTable ConversionTable { get; }
	public OperatorRegistry OperatorRegistry { get; }
	public SizeTable SizeTable { get; }
	
	private readonly Dictionary<TypeSymbol, OrderedDictionary<string, MemberSymbol>> _members = [];
	private readonly Dictionary<(TypeSymbol, BigInteger), ArrayType> _arrayTypes = [];
	private readonly Dictionary<TypeSymbol, BufferType> _bufferTypes = [];
	private readonly Dictionary<TypeSymbol, SpanType> _spanTypes = [];
	private readonly Dictionary<TypeSymbol, ViewType> _viewTypes = [];
	private readonly Dictionary<(TypeSymbol, PointerKind), PointerType> _pointerTypes = [];
	private readonly Dictionary<TypedMemberSymbol, TypeSymbol> _memberTypes = [];
	
	public TypePool(ConversionTable conversionTable, OperatorRegistry operatorRegistry, SizeTable sizeTable)
	{
		ConversionTable = conversionTable;
		OperatorRegistry = operatorRegistry;
		SizeTable = sizeTable;
		
		CreateNativeMembers();
	}
	
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
		"buffer" when typeArgs is [GenericTypeArgument { Type: { } t }] => GetBufferType(t),
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
		SizeTable.Register(ptrType, StorageSize.Ptr);
		
		// Conversions
		switch (kind)
		{
			case PointerKind.Unsafe:
				break;
			
			default:
				// All pointers except ptr[T] can be implicitly converted to ptr[T]
				var unsafeType = new PointerType(baseType, PointerKind.Unsafe);
				ConversionTable.Add(new NativeConversion(ptrType, unsafeType, ConversionKind.Implicit, 0));
				break;
		}
		
		// All pointers can be implicitly converted to ptr / explicitly converted from ptr
		ConversionTable.Add(new NativeConversion(ptrType, PointerType.VoidPtr, ConversionKind.Implicit, 0));
		ConversionTable.Add(new NativeConversion(PointerType.VoidPtr, ptrType, ConversionKind.Explicit, 0));
		
		// All pointers can be explicitly converted to/from usize and isize
		ConversionTable.Add(new NativeConversion(ptrType, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, ptrType, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(ptrType, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, ptrType, ConversionKind.Explicit, 0));
		
		return ptrType;
	}
	
	public ArrayType GetArrayType(TypeSymbol elementType, BigInteger length)
	{
		var key = (elementType, length);
		if (_arrayTypes.TryGetValue(key, out var existing))
			return existing;
		
		var arrayType = new ArrayType(elementType, length);
		_arrayTypes[key] = arrayType;
		CreateArrayMembers(arrayType);
		SizeTable.Register(arrayType, StorageSize.Product(SizeTable.GetSize(elementType), length));
		
		// To buffer
		var bufferType = GetBufferType(elementType);
		ConversionTable.Add(new NativeConversion(arrayType, bufferType, ConversionKind.Implicit, 1));
		
		// To span
		var spanType = GetSpanType(elementType);
		ConversionTable.Add(new NativeConversion(arrayType, spanType, ConversionKind.Implicit, 1));
		
		// To view
		var viewType = GetViewType(elementType);
		ConversionTable.Add(new NativeConversion(arrayType, viewType, ConversionKind.Implicit, 1));
		
		// To pointer
		var arrayPtrType = GetPointerType(arrayType, PointerKind.Unsafe);
		var elementPtrType = GetPointerType(elementType, PointerKind.Unsafe);
		ConversionTable.Add(new NativeConversion(arrayPtrType, elementPtrType, ConversionKind.Implicit, 0));
		
		return arrayType;
	}
	
	public BufferType GetBufferType(TypeSymbol elementType)
	{
		if (_bufferTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var bufferType = new BufferType(elementType);
		_bufferTypes[elementType] = bufferType;
		CreateBufferMembers(bufferType);
		SizeTable.Register(bufferType, StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr));
		
		// To span
		var spanType = GetSpanType(elementType);
		ConversionTable.Add(new NativeConversion(bufferType, spanType, ConversionKind.Implicit, 1));
		
		// To view
		var viewType = GetViewType(elementType);
		ConversionTable.Add(new NativeConversion(bufferType, viewType, ConversionKind.Implicit, 1));
		
		return bufferType;
	}
	
	public SpanType GetSpanType(TypeSymbol elementType)
	{
		if (_spanTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var spanType = new SpanType(elementType);
		_spanTypes[elementType] = spanType;
		CreateSpanMembers(spanType);
		SizeTable.Register(spanType, StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr));
		
		// To view
		var viewType = GetViewType(elementType);
		ConversionTable.Add(new NativeConversion(spanType, viewType, ConversionKind.Implicit, 1));
		
		return spanType;
	}
	
	public ViewType GetViewType(TypeSymbol elementType)
	{
		if (_viewTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var viewType = new ViewType(elementType);
		_viewTypes[elementType] = viewType;
		CreateViewMembers(viewType);
		SizeTable.Register(viewType, StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr));
		return viewType;
	}
	
	public void RegisterMember(TypeSymbol containingType, TypedMemberSymbol member, TypeSymbol memberType)
	{
		_members.GetOrAdd(containingType)[member.Name] = member;
		_memberTypes[member] = memberType;
	}
	
	public MemberSymbol? ResolveMember(TypeSymbol type, string name) =>
		_members.GetValueOrDefault(type)?.GetValueOrDefault(name);
	
	public int GetFieldIndex(TypeSymbol type, MemberSymbol member) =>
		_members[type].IndexOf(member.Name);
	
	public IReadOnlyList<MemberSymbol> GetMembers(TypeSymbol type) => _members[type].Values;
	
	public bool TryGetTypeOfMember(MemberSymbol member, [NotNullWhen(true)] out TypeSymbol? type)
	{
		if (member is TypedMemberSymbol m)
			return _memberTypes.TryGetValue(m, out type);
		
		type = null;
		return false;
	}
	
	public TypeSymbol GetTypeOfMember(TypedMemberSymbol member) => _memberTypes[member];
	
	public void CreateNativeMembers()
	{
		// ptr <-> usize/isize
		var ptr = NativeSymbols.VoidPtr;
		ConversionTable.Add(new NativeConversion(ptr, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, ptr, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(ptr, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, ptr, ConversionKind.Explicit, 0));
		
		// cstr <-> ptr[i8]/ptr[u8]/usize/isize
		var cstr = NativeSymbols.CStr;
		var ptrI8 = GetPointerType(NativeSymbols.Int8, PointerKind.Unsafe);
		ConversionTable.Add(new FreeConversion(cstr, ptrI8, ConversionKind.Implicit));
		ConversionTable.Add(new FreeConversion(ptrI8, cstr, ConversionKind.Explicit));
		var ptrU8 = GetPointerType(NativeSymbols.UInt8, PointerKind.Unsafe);
		ConversionTable.Add(new FreeConversion(cstr, ptrU8, ConversionKind.Implicit));
		ConversionTable.Add(new FreeConversion(ptrU8, cstr, ConversionKind.Explicit));
		ConversionTable.Add(new NativeConversion(cstr, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, cstr, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(cstr, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, cstr, ConversionKind.Explicit, 0));
		
		// TODO constructors, str.toCstr(), str.getCharLength(), etc.
		//Register(NativeSymbols.Str, new IntrinsicMemberSymbol("byteLength", NativeSymbols.UIntSize));
	}
	
	public void CreateArrayMembers(ArrayType type)
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new PropertySymbol("length")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.ArrayLength)
		};
		
		RegisterMember(type, length, lengthType);
	}
	
	public void CreateBufferMembers(BufferType type)
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new FieldSymbol("length", null, false);
		RegisterMember(type, length, lengthType);
		
		var dataType = GetPointerType(type.ElementType, PointerKind.Owning);
		var data = new FieldSymbol("data", null, false);
		RegisterMember(type, data, dataType);
	}
	
	public void CreateSpanMembers(SpanType type)
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new FieldSymbol("length", null, false);
		RegisterMember(type, length, lengthType);
		
		var dataType = GetPointerType(type.ElementType, PointerKind.Mutable);
		var data = new FieldSymbol("data", null, false);
		RegisterMember(type, data, dataType);
	}
	
	public void CreateViewMembers(ViewType type)
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new FieldSymbol("length", null, false);
		RegisterMember(type, length, lengthType);
		
		var dataType = GetPointerType(type.ElementType, PointerKind.Mutable);
		var data = new FieldSymbol("data", null, false);
		RegisterMember(type, data, dataType);
	}
}

public interface IGenericArgument;

public sealed record GenericTypeArgument(TypeSymbol Type) : IGenericArgument;
public sealed record GenericConstArgument(BigInteger Value) : IGenericArgument;