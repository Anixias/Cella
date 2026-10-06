using System.Collections.Immutable;
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
	public Action<TypeSymbol>? TypeCompleter { get; set; }
	
	private readonly Dictionary<TypeSymbol, OrderedDictionary<string, MemberSymbol>> _members = [];
	private readonly Dictionary<(TypeSymbol, BigInteger), ArrayType> _arrayTypes = [];
	private readonly Dictionary<TypeSymbol, PointerType> _pointerTypes = [];
	private readonly List<FunctionType> _functionTypes = [];
	private readonly Dictionary<TypedMemberSymbol, TypeSymbol> _memberTypes = [];
	private readonly Dictionary<TypeSymbol, TypeFacts> _facts = [];
	private readonly Dictionary<TypeSymbol, List<FunctionInfo>> _constructors = [];
	private readonly Dictionary<TypeSymbol, FunctionInfo> _destructors = [];
	private readonly Dictionary<EnumSymbol, IntegerType> _tagTypes = [];
	private readonly Dictionary<EnumCaseSymbol, BigInteger> _caseValues = [];
	
	public TypePool(ConversionTable conversionTable, OperatorRegistry operatorRegistry, SizeTable sizeTable)
	{
		ConversionTable = conversionTable;
		OperatorRegistry = operatorRegistry;
		SizeTable = sizeTable;
		SizeTable.Completer = Complete;
		
		CreateNativeMembers();
	}
	
	public void AddConstructor(TypeSymbol type, FunctionInfo info) => _constructors.GetOrAdd(type).Add(info);
	
	public IReadOnlyList<FunctionInfo> GetConstructors(TypeSymbol type)
	{
		Complete(type);
		return _constructors.TryGetValue(type, out var list) ? list : [];
	}
	
	public void SetDestructor(TypeSymbol type, FunctionInfo info) => _destructors[type] = info;
	
	public FunctionInfo? GetDestructor(TypeSymbol type)
	{
		Complete(type);
		return _destructors.TryGetValue(type, out var info) ? info : null;
	}
	
	public bool NeedsDrop(TypeSymbol type) => GetFacts(type).NeedsDrop;
	public bool IsCopy(TypeSymbol type) => GetFacts(type).IsCopy;
	public bool HasDefault(TypeSymbol type) => GetFacts(type).HasDefault;
	public bool HasDefault(FieldSymbol field) => !field.IsRequired && HasDefault(GetTypeOfMember(field));
	
	public bool PassesByPointer(TypeSymbol type, ParameterMode mode) =>
		mode == ParameterMode.ReadOnly && NeedsDrop(type);
	
	public RecordSymbol? FindDestructor(TypeSymbol type) => FindDestructor(type, []);
	
	private RecordSymbol? FindDestructor(TypeSymbol type, HashSet<TypeSymbol> visited)
	{
		if (!NeedsDrop(type) || !visited.Add(type))
			return null;
		
		if (type is RecordSymbol { HasDestructor: true } record)
			return record;
		
		return GetParts(type)
			.Select(part => FindDestructor(part, visited))
			.FirstOrDefault(static found => found is not null);
	}
	
	private TypeFacts GetFacts(TypeSymbol type)
	{
		if (_facts.TryGetValue(type, out var facts))
			return facts;
		
		_facts[type] = TypeFacts.Plain;
		facts = type switch
		{
			RecordSymbol r => Combine(r.HasDestructor, GetParts(r)) with
			{
				HasDefault = !r.HasDestructor && GetMembers(r).OfType<FieldSymbol>().All(HasDefault)
			},
			EnumSymbol e => Combine(false, GetParts(e)) with
			{
				HasDefault = FindCase(e, BigInteger.Zero) is { } zeroCase ? zeroCase.Fields.IsEmpty : e.IsExternal
			},
			ArrayType => Combine(false, GetParts(type)),
			FunctionType => TypeFacts.Plain with { HasDefault = false },
			_ => TypeFacts.Plain
		};
		
		_facts[type] = facts;
		return facts;
	}
	
	private IEnumerable<TypeSymbol> GetParts(TypeSymbol type) => type switch
	{
		RecordSymbol r => GetMembers(r).OfType<FieldSymbol>().Select(GetTypeOfMember),
		EnumSymbol e => e.Cases.SelectMany(c => GetPayloadTypes(e, c)),
		ArrayType array => [array.ElementType],
		_ => []
	};
	
	private TypeFacts Combine(bool hasDestructor, IEnumerable<TypeSymbol> parts)
	{
		var facts = parts.Select(GetFacts).ToArray();
		return new(hasDestructor || facts.Any(static f => f.NeedsDrop),
			!hasDestructor && facts.All(static f => f.IsCopy),
			facts.All(static f => f.HasDefault));
	}
	
	public static IReadOnlyDictionary<string, string> BuiltinGenericTypeArguments { get; } =
		new Dictionary<string, string>
		{
			["ptr"] = "one type argument",
			["array"] = "an element type and an optional length"
		};
	
	public TypeSymbol? ResolveBuiltinGenericType(string name, IReadOnlyList<IGenericArgument> typeArgs) => name switch
	{
		"ptr" when typeArgs.Count == 0 => NativeSymbols.VoidPtr,
		"ptr" when typeArgs is [GenericTypeArgument { Type: { } t }] => GetPointerType(t),
		"array" when typeArgs is [GenericTypeArgument { Type: { } t }, GenericConstArgument { Value: var v }] =>
			GetArrayType(t, v),
		"array" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			new ArrayType(t, BigInteger.MinusOne), // Don't use GetArrayType; we don't want to actually create it
		_ => null
	};
	
	private void CreatePointerArithmetic(PointerType ptrType)
	{
		foreach (var offsetType in new[] { NativeSymbols.IntSize, NativeSymbols.UIntSize })
		{
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpPlus, ptrType, offsetType));
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpMinus, ptrType, offsetType));
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpPlusEqual, ptrType, offsetType));
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpMinusEqual, ptrType, offsetType));
		}
		
		OperatorRegistry.Create(new PointerDifferenceImpl(ptrType));
	}
	
	public FunctionType GetFunctionType(bool isExternal, IEnumerable<TypeSymbol> parameterTypes,
		IEnumerable<ParameterMode> parameterModes, TypeSymbol returnType)
	{
		var parameters = parameterTypes.ToImmutableArray();
		var modes = parameterModes.ToImmutableArray();
		var existing = _functionTypes.Find(type => type.IsExternal == isExternal && type.ReturnType == returnType &&
		                                           type.ParameterTypes.SequenceEqual(parameters) &&
		                                           type.ParameterModes.SequenceEqual(modes));
		
		if (existing is not null)
			return existing;
		
		var functionType = new FunctionType(isExternal, parameters, modes, returnType);
		_functionTypes.Add(functionType);
		var size = isExternal ? StorageSize.Ptr : (ISize)StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr);
		SizeTable.Register(functionType, size);
		
		if (isExternal)
			ConversionTable.Add(new FreeConversion(functionType, NativeSymbols.VoidPtr, ConversionKind.Explicit));
		
		return functionType;
	}
	
	public TypeSymbol GetPassedType(TypeSymbol type, ParameterMode mode) =>
		mode == ParameterMode.Mut && type is not InvalidType ? GetPointerType(type) : type;
	
	public PointerType GetPointerType(TypeSymbol baseType)
	{
		if (_pointerTypes.TryGetValue(baseType, out var existing))
			return existing;
		
		var ptrType = new PointerType(baseType);
		_pointerTypes[baseType] = ptrType;
		SizeTable.Register(ptrType, StorageSize.Ptr);
		
		// All pointers can be implicitly converted to ptr / explicitly converted from ptr
		ConversionTable.Add(new NativeConversion(ptrType, PointerType.VoidPtr, ConversionKind.Implicit, 1));
		ConversionTable.Add(new NativeConversion(PointerType.VoidPtr, ptrType, ConversionKind.Explicit, 0));
		
		// All pointers can be explicitly converted to/from usize and isize
		ConversionTable.Add(new NativeConversion(ptrType, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, ptrType, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(ptrType, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, ptrType, ConversionKind.Explicit, 0));
		
		// Pointer arithmetic
		CreatePointerArithmetic(ptrType);
		
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
		SizeTable.Register(arrayType, () => StorageSize.Product(SizeTable.GetSize(elementType), length));
		
		// To pointer
		var arrayPtrType = GetPointerType(arrayType);
		var elementPtrType = GetPointerType(elementType);
		ConversionTable.Add(new NativeConversion(arrayPtrType, elementPtrType, ConversionKind.Implicit, 1));
		
		return arrayType;
	}
	
	public void RegisterRecord(RecordSymbol record) => SizeTable.Register(record, () =>
	{
		var fieldSizes = GetMembers(record)
			.OfType<FieldSymbol>()
			.Select(GetTypeOfMember)
			.Select(type => SizeTable.TryGetSize(type) ?? StorageSize.Const(0));
		
		return StorageSize.Sum(fieldSizes);
	});
	
	public void RegisterEnum(EnumSymbol enumType, IntegerType tagType, IReadOnlyList<BigInteger> values)
	{
		_tagTypes[enumType] = tagType;
		for (var i = 0; i < values.Count; i++)
			_caseValues[enumType.Cases[i]] = values[i];
		
		SizeTable.Register(enumType, () =>
		{
			var tagSize = SizeTable.GetSize(tagType);
			if (!enumType.HasPayload)
				return tagSize;
			
			var payloadSizes = enumType.Cases.Select(ISize (enumCase) => StorageSize.Sum(enumCase.Fields
				.Select(field => SizeTable.TryGetSize(GetPayloadType(field)) ?? StorageSize.Const(0))));
			
			return StorageSize.Sum(tagSize, StorageSize.Max(payloadSizes));
		});
		
		if (enumType.HasPayload)
			return;
		
		OperatorRegistry.Create(new NativeImpl(TokenType.OpEqualEqual, NativeSymbols.Bool, enumType, enumType));
		OperatorRegistry.Create(new NativeImpl(TokenType.OpBangEqual, NativeSymbols.Bool, enumType, enumType));
	}
	
	public void RegisterPayloadField(FieldSymbol field, TypeSymbol type) => _memberTypes[field] = type;
	
	public ImmutableArray<TypeSymbol> GetPayloadTypes(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		Complete(enumType);
		return [..enumCase.Fields.Select(GetPayloadType)];
	}
	
	private TypeSymbol GetPayloadType(FieldSymbol field) =>
		_memberTypes.GetValueOrDefault(field) ?? NativeSymbols.Invalid;
	
	public void RegisterEnumConversions(EnumSymbol enumType)
	{
		foreach (var integerType in NativeSymbols.PureIntegerTypes)
		{
			ConversionTable.Add(new EnumConversion(enumType, integerType));
			ConversionTable.Add(new EnumConversion(integerType, enumType));
		}
	}
	
	public IntegerType GetTagType(EnumSymbol enumType)
	{
		Complete(enumType);
		return _tagTypes.GetValueOrDefault(enumType, NativeSymbols.UInt8);
	}
	
	public BigInteger GetCaseValue(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		Complete(enumType);
		return _caseValues.GetValueOrDefault(enumCase, enumCase.Index);
	}
	
	public EnumCaseSymbol? FindCase(EnumSymbol enumType, BigInteger value) =>
		enumType.Cases.FirstOrDefault(enumCase => GetCaseValue(enumType, enumCase) == value);
	
	public void RegisterMember(TypeSymbol containingType, TypedMemberSymbol member, TypeSymbol memberType)
	{
		_members.GetOrAdd(containingType)[member.Name] = member;
		_memberTypes[member] = memberType;
	}
	
	public MemberSymbol? ResolveMember(TypeSymbol type, string name)
	{
		Complete(type);
		return _members.GetValueOrDefault(type)?.GetValueOrDefault(name);
	}
	
	public int GetFieldIndex(TypeSymbol type, MemberSymbol member) =>
		_members[type].IndexOf(member.Name);
	
	public IReadOnlyList<MemberSymbol> GetMembers(TypeSymbol type)
	{
		Complete(type);
		return _members.TryGetValue(type, out var members) ? members.Values : [];
	}
	
	private void Complete(TypeSymbol type)
	{
		if (type is RecordSymbol or EnumSymbol)
			TypeCompleter?.Invoke(type);
	}
	
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
		var ptrI8 = GetPointerType(NativeSymbols.Int8);
		ConversionTable.Add(new FreeConversion(cstr, ptrI8, ConversionKind.Implicit));
		ConversionTable.Add(new FreeConversion(ptrI8, cstr, ConversionKind.Explicit));
		var ptrU8 = GetPointerType(NativeSymbols.UInt8);
		ConversionTable.Add(new FreeConversion(cstr, ptrU8, ConversionKind.Implicit));
		ConversionTable.Add(new FreeConversion(ptrU8, cstr, ConversionKind.Explicit));
		ConversionTable.Add(new NativeConversion(cstr, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, cstr, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(cstr, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, cstr, ConversionKind.Explicit, 0));
		
		// TODO constructors, str.toCstr(), str.getCharLength(), etc.
		CreateStrMembers();
		
		// Operations
		CreatePointerArithmetic(NativeSymbols.VoidPtr);
	}
	
	private void CreateStrMembers()
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new PropertySymbol("byteLength")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.StrByteLength)
		};
		
		RegisterMember(NativeSymbols.Str, length, lengthType);
		
		var ptrType = GetPointerType(NativeSymbols.UInt8);
		var data = new PropertySymbol("data")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.StrData)
		};
		
		RegisterMember(NativeSymbols.Str, data, ptrType);
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
	
	private readonly record struct TypeFacts(bool NeedsDrop, bool IsCopy, bool HasDefault)
	{
		public static TypeFacts Plain => new(false, true, true);
	}
}

public interface IGenericArgument;
public sealed record GenericTypeArgument(TypeSymbol Type) : IGenericArgument;
public sealed record GenericConstArgument(BigInteger Value) : IGenericArgument;