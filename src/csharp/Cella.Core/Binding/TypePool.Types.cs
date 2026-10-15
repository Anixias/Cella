using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public static IReadOnlyDictionary<string, string> BuiltinGenericTypeArguments { get; } =
		new Dictionary<string, string>
		{
			["ptr"] = "one generic argument",
			["array"] = "2 generic arguments",
			["fstr"] = "one trait argument"
		};
	
	public static ImmutableArray<TypeParameterSymbol> ArrayParameters { get; } =
	[
		new("T", []),
		new("N", []) { IsValue = true, ValueType = NativeSymbols.UIntSize }
	];
	
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
	
	public ClosureType GetClosureType(FunctionInfo function, ImmutableArray<TypeSymbol> captureTypes,
		FunctionType signature, string definitionName)
	{
		var existing = _closureTypes.Find(type => type.Function == function && type.Signature == signature &&
		                                          type.CaptureTypes.SequenceEqual(captureTypes));
		
		if (existing is not null)
			return existing;
		
		var closure = new ClosureType(function, captureTypes, signature, definitionName);
		_closureTypes.Add(closure);
		SizeTable.Register(closure, () => StorageSize.Sum(captureTypes
			.Select(type => SizeTable.TryGetSize(type) ?? StorageSize.Const(0))));
		
		return closure;
	}
	
	public FunctionType GetFunctionType(bool isExternal, IEnumerable<TypeSymbol> parameterTypes,
		IEnumerable<ParameterMode> parameterModes, TypeSymbol returnType, bool isRef = false, bool isVariadic = false,
		bool hasVariadicParameter = false)
	{
		var parameters = parameterTypes.ToImmutableArray();
		var modes = parameterModes.ToImmutableArray();
		var existing = _functionTypes.Find(type => type.IsExternal == isExternal && type.IsRef == isRef &&
		                                           type.IsVariadic == isVariadic &&
		                                           type.HasVariadicParameter == hasVariadicParameter &&
		                                           type.ReturnType == returnType &&
		                                           type.ParameterTypes.SequenceEqual(parameters) &&
		                                           type.ParameterModes.SequenceEqual(modes));
		
		if (existing is not null)
			return existing;
		
		var functionType = new FunctionType(isExternal, isRef, parameters, modes, returnType, isVariadic,
			hasVariadicParameter);
		
		_functionTypes.Add(functionType);
		var size = isExternal ? StorageSize.Ptr : (ISize)StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr);
		SizeTable.Register(functionType, size);
		
		if (isExternal)
			ConversionTable.Add(new FreeConversion(functionType, NativeSymbols.VoidPtr, ConversionKind.Explicit));
		
		if (isRef)
			ConversionTable.Add(new FreeConversion(
				GetFunctionType(false, parameters, modes, returnType, false, isVariadic, hasVariadicParameter),
				functionType,
				ConversionKind.Implicit));
		
		return functionType;
	}
	
	public TypeSymbol GetPassedType(TypeSymbol type, ParameterMode mode) => (mode, type) switch
	{
		(ParameterMode.Mut, not InvalidType) => GetPointerType(type),
		(ParameterMode.ReadOnly or ParameterMode.Imm, FunctionType { IsExternal: false, IsRef: false } function) =>
			GetRefFunctionType(function),
		_ => type
	};
	
	public PointerType GetPointerType(TypeSymbol baseType)
	{
		if (_pointerTypes.TryGetValue(baseType, out var existing))
			return existing;
		
		var ptrType = new PointerType(baseType);
		_pointerTypes[baseType] = ptrType;
		if (baseType is DynType)
		{
			SizeTable.Register(ptrType, FatPointerSize);
			ConversionTable.Add(new DynCastConversion(ptrType, PointerType.VoidPtr, ConversionKind.Implicit, 1));
			return ptrType;
		}
		
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
	
	public BorrowType GetBorrowType(TypeSymbol target, bool isMutable)
	{
		if (_borrowTypes.TryGetValue((target, isMutable), out var existing))
			return existing;
		
		var borrowType = new BorrowType(target, isMutable);
		_borrowTypes[(target, isMutable)] = borrowType;
		SizeTable.Register(borrowType, target is DynType ? FatPointerSize : StorageSize.Ptr);
		if (isMutable)
			ConversionTable.Add(new FreeConversion(borrowType, GetBorrowType(target, false), ConversionKind.Implicit));
		
		return borrowType;
	}
	
	private static ISize FatPointerSize => StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr);
	
	public static bool IsFatPointer(TypeSymbol type) =>
		type is PointerType { BaseType: DynType } or BorrowType { Target: DynType };
	
	public DynType GetDynType(TraitType trait)
	{
		if (_dynTypes.TryGetValue(trait, out var existing))
			return existing;
		
		var dynType = new DynType(trait);
		_dynTypes[trait] = dynType;
		return dynType;
	}
	
	public DynType GetDynType(FunctionType function)
	{
		if (_functionDynTypes.TryGetValue(function, out var existing))
			return existing;
		
		var dynType = new DynType(function);
		_functionDynTypes[function] = dynType;
		return dynType;
	}
	
	public DynType GetDynType(TypeParameterSymbol parameter)
	{
		if (_parameterDynTypes.TryGetValue(parameter, out var existing))
			return existing;
		
		var dynType = new DynType(parameter);
		_parameterDynTypes[parameter] = dynType;
		return dynType;
	}
	
	public FStrType GetFStrType(DynType value)
	{
		if (_fstrTypes.TryGetValue(value, out var existing))
			return existing;
		
		var fstrType = new FStrType(value);
		_fstrTypes[value] = fstrType;
		SizeTable.Register(fstrType, StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr, StorageSize.Ptr));
		var holes = new PropertySymbol("holes")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.FStrHoles)
		};
		
		RegisterMember(fstrType, holes, NativeSymbols.UIntSize);
		return fstrType;
	}
	
	public TraitType GetTraitType(TraitSymbol trait, ImmutableArray<TypeSymbol> arguments)
	{
		var instances = _traitTypes.GetOrAdd(trait);
		if (instances.Find(instance => instance.Arguments.SequenceEqual(arguments)) is { } existing)
			return existing;
		
		var traitType = new TraitType(trait, arguments);
		instances.Add(traitType);
		return traitType;
	}
	
	public TraitType SubstituteTrait(TraitType trait, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		map.Count == 0 || trait.Arguments.IsEmpty
			? trait
			: GetTraitType(trait.Trait, [..trait.Arguments.Select(argument => Substitute(argument, map))]);
	
	public ImmutableArray<FunctionSymbol>? FindDynMembers(TraitSymbol trait) =>
		_dynMembers.TryGetValue(trait, out var members) ? members : null;
	
	public ImmutableArray<FunctionInfo> GetDynMemberInfos(TraitSymbol trait) => _dynMemberInfos[trait];
	
	public void SetDynMembers(TraitSymbol trait, ImmutableArray<FunctionInfo> members)
	{
		_dynMembers[trait] = [..members.Select(static member => member.Symbol)];
		_dynMemberInfos[trait] = members;
	}
	
	public int GetDynSlot(TraitSymbol trait, FunctionSymbol member) => _dynMembers[trait].IndexOf(member);
	
	public static DynType? FindValueDyn(TypeSymbol type) => type switch
	{
		DynType dyn => dyn,
		ArrayType array => FindValueDyn(array.ElementType),
		FunctionType function => FindValueDyn(function.ReturnType) ?? function.ParameterTypes
			.Where((_, i) => function.ParameterModes[i] == ParameterMode.Own)
			.Select(FindValueDyn)
			.FirstOrDefault(static dyn => dyn is not null),
		_ => null
	};
	
	public ArrayType GetArrayType(TypeSymbol elementType, BigInteger length)
	{
		var key = (elementType, length);
		if (_arrayTypes.TryGetValue(key, out var existing))
			return existing;
		
		var arrayType = new ArrayType(elementType, length);
		_arrayTypes[key] = arrayType;
		CreateArrayMembers(arrayType);
		SizeTable.Register(arrayType,
			() => StorageSize.Product(SizeTable.TryGetSize(elementType) ?? StorageSize.Const(0), length));
		
		// To pointer
		var arrayPtrType = GetPointerType(arrayType);
		var elementPtrType = GetPointerType(elementType);
		ConversionTable.Add(new NativeConversion(arrayPtrType, elementPtrType, ConversionKind.Implicit, 1));
		
		return arrayType;
	}
	
	public TypeSymbol GetArrayType(TypeSymbol elementType, TypeSymbol length) => length switch
	{
		ValueArgumentType { Value.Sign: >= 0 } value => GetArrayType(elementType, value.Value),
		TypeParameterSymbol { IsValue: true } parameter => GetParametricArrayType(elementType, parameter),
		_ => NativeSymbols.Invalid
	};
	
	public ArrayType GetParametricArrayType(TypeSymbol elementType, TypeParameterSymbol length)
	{
		if (_parametricArrayTypes.TryGetValue((elementType, length), out var existing))
			return existing;
		
		var arrayType = new ArrayType(elementType, BigInteger.MinusOne, length);
		_parametricArrayTypes[(elementType, length)] = arrayType;
		CreateArrayMembers(arrayType);
		ConversionTable.Add(new NativeConversion(GetPointerType(arrayType), GetPointerType(elementType),
			ConversionKind.Implicit, 1));
		
		return arrayType;
	}
	
	public ValueArgumentType GetValueArgument(BigInteger value)
	{
		if (!_valueArguments.TryGetValue(value, out var argument))
			_valueArguments[value] = argument = new ValueArgumentType(value);
		
		return argument;
	}
	
	public bool IsTooLarge(TypeSymbol type, uint pointerBits) => !ContainsTypeParameters(type) &&
	                                                             (type is ArrayType { Length: var length } &&
	                                                              length > ulong.MaxValue ||
	                                                              SizeTable.TryGetSize(type) is { } size &&
	                                                              (size.CountBits(pointerBits) + 7) / 8 >=
	                                                              BigInteger.One << ((int)pointerBits - 1));
	
	public TypeSymbol GetLengthArgument(ArrayType array) =>
		array.LengthParameter ?? (TypeSymbol)GetValueArgument(array.Length);
}
