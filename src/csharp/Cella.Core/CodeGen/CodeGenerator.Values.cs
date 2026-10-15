using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private LLVMValueRef EmitFStr(FStrValue v, LLVMBuilderRef builder)
	{
		var texts = v.Text is { } text ? EmitValue(text, builder) : CreateTextsGlobal(v.Texts);
		var values = v.Values is { } array ? EmitValue(array, builder) : LLVMValueRef.CreateConstNull(OpaquePointer);
		var result = builder.BuildInsertValue(MapTypeSymbol(v.Type).Undef, EmitSizeConstant(v.Holes, true), 0, "fstr");
		result = builder.BuildInsertValue(result, texts, 1, "fstr");
		return builder.BuildInsertValue(result, values, 2, "fstr");
	}
	
	private LLVMValueRef CreateTextsGlobal(ImmutableArray<string> texts)
	{
		var lengthType = MapTypeSymbol(NativeSymbols.UIntSize);
		var elements = texts.Select(text => Encoding.UTF8.GetBytes(text)).Select(bytes =>
			_context.GetConstStruct(
				[LLVMValueRef.CreateConstInt(lengthType, (ulong)bytes.Length), GetOrCreateStringGlobal(bytes)], false));
		
		var array = LLVMValueRef.CreateConstArray(MapTypeSymbol(NativeSymbols.Str), [..elements]);
		var global = current.Module.AddGlobal(array.TypeOf, "fstr.texts");
		global.Initializer = array;
		global.IsGlobalConstant = true;
		global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
		global.HasUnnamedAddr = true;
		return global;
	}
	
	private LLVMValueRef EmitFStrPart(FStrPartValue v, LLVMBuilderRef builder)
	{
		var template = EmitValue(v.Target, builder);
		var index = EmitValue(v.Index, builder);
		if (checks.Bounds)
		{
			var holes = builder.BuildExtractValue(template, 0, "holes");
			var predicate = v.Part == FStrPart.Piece ? LLVMIntPredicate.LLVMIntUGT : LLVMIntPredicate.LLVMIntUGE;
			PanicIf(builder.BuildICmp(predicate, index, holes), "index out of bounds", v.SourceLocation, builder);
		}
		
		if (v.Part == FStrPart.Value)
		{
			var values = builder.BuildExtractValue(template, 2, "values");
			var valueAddress = builder.BuildInBoundsGEP2(MapTypeSymbol(v.Type), values, new[] { index }, "value.addr");
			return LoadValue(builder, v.Type, valueAddress, "value");
		}
		
		var two = LLVMValueRef.CreateConstInt(index.TypeOf, 2);
		var position = builder.BuildMul(index, two, "position");
		if (v.Part == FStrPart.Spec)
			position = builder.BuildAdd(position, LLVMValueRef.CreateConstInt(index.TypeOf, 1), "position");
		
		var strType = MapTypeSymbol(NativeSymbols.Str);
		var texts = builder.BuildExtractValue(template, 1, "texts");
		var textAddress = builder.BuildInBoundsGEP2(strType, texts, new[] { position }, "text.addr");
		return builder.BuildLoad2(strType, textAddress, "text");
	}
	
	private LLVMValueRef EmitSizeOf(SizeOfValue v)
	{
		var bits = _typePool.SizeTable.GetSize(Substitute(v.Target)).CountBits(_pointerSize * 8);
		return EmitSizeConstant((bits + 7) / 8, true);
	}
	
	private LLVMValueRef EmitValueParameter(ValueParameterValue v) => Substitute(v.Parameter) is ValueArgumentType value
		? EmitIntegerConstant(new IntegerConstant(v.Type, value.Value))
		: throw new InvalidOperationException($"Value parameter '{v.Parameter.Name}' has no value");
	
	private LLVMValueRef EmitAlignOf(AlignOfValue v)
	{
		var bits = _typePool.SizeTable.GetSize(Substitute(v.Target)).CountAlignmentBits(_pointerSize * 8);
		return EmitSizeConstant(new BigInteger((bits + 7) / 8), true);
	}
	
	private LLVMValueRef EmitNew(NewValue v, LLVMBuilderRef builder)
	{
		var type = Substitute(v.Type);
		if (_typePool.FindNewConstructor(type) is not { } constructor)
			return EmitZeroed(builder, type, "new");
		
		var slot = SpillValue(builder, type, EmitZeroed(builder, type, "new"), "new");
		var info = SubstituteFunction(constructor);
		GetFunctionValue(info);
		var function = current.Functions[info];
		var cleanups = new List<Action>();
		var args = EmitOmittedArguments(info.Signature, 1, [], cleanups, builder);
		builder.BuildCall2(function.FunctionType, function.FunctionValue, [slot, ..args]);
		foreach (var cleanup in cleanups)
			cleanup();
		
		return IsLarge(type) ? slot : builder.BuildLoad2(MapTypeSymbol(type), slot, "new");
	}
	
	private List<LLVMValueRef> EmitOmittedArguments(FunctionSignature signature, int given,
		IReadOnlyList<LLVMValueRef> packed, List<Action> cleanups, LLVMBuilderRef builder)
	{
		var args = new List<LLVMValueRef>();
		var firstDefault = signature.FixedCount - signature.Defaults.Length;
		for (var i = given; i < signature.FixedCount; i++)
		{
			var type = Substitute(signature.GetDeclaredType(i));
			var slot = SpillValue(builder, type, EmitStaticConstant(signature.Defaults[i - firstDefault]), "default");
			args.Add(PassOmittedArgument(signature, i, slot, cleanups, builder));
		}
		
		if (signature.HasVariadicParameter)
			args.Add(PassOmittedArgument(signature, signature.FixedCount,
				EmitRun(Substitute(signature.GetDeclaredType(signature.FixedCount)), packed, builder), cleanups,
				builder));
		
		return args;
	}
	
	private LLVMValueRef PassOmittedArgument(FunctionSignature signature, int index, LLVMValueRef slot,
		List<Action> cleanups, LLVMBuilderRef builder)
	{
		var type = Substitute(signature.GetDeclaredType(index));
		var mode = signature.GetMode(index);
		if (mode != ParameterMode.Own)
			cleanups.Add(() => EmitDropCall(type, slot, builder));
		
		return TypePool.PassesByPointer(mode) ? slot : LoadValue(builder, type, slot, "argument");
	}
	
	private LLVMValueRef EmitRun(TypeSymbol type, IReadOnlyList<LLVMValueRef> elements, LLVMBuilderRef builder)
	{
		var slot = SpillValue(builder, type, EmitZeroed(builder, type, "run"), "run");
		var element = _typePool.FindVariadicElement(type)!;
		var data = LLVMValueRef.CreateConstNull(OpaquePointer);
		if (elements.Count > 0)
		{
			var elementType = MapTypeSymbol(element);
			var arrayType = LLVMTypeRef.CreateArray(elementType, (uint)elements.Count);
			data = BuildEntryAlloca(builder, arrayType, "values");
			for (var i = 0; i < elements.Count; i++)
			{
				var index = LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)i);
				StoreValue(builder, element, elements[i],
					builder.BuildGEP2(arrayType, data, new[] { Int32Zero, index }, "value"));
			}
		}
		
		BuildRun(type, _typePool.FindRunWitness(type, element), slot, data,
			EmitSizeConstant(new BigInteger(elements.Count), false), builder);
		
		return slot;
	}
	
	private void BuildRun(TypeSymbol type, Witness? witness, LLVMValueRef slot, LLVMValueRef data, LLVMValueRef count,
		LLVMBuilderRef builder)
	{
		switch (witness)
		{
			case FunctionWitness { Info: var constructor }:
			{
				var info = SubstituteFunction(constructor);
				GetFunctionValue(info);
				var function = current.Functions[info];
				builder.BuildCall2(function.FunctionType, function.FunctionValue, [slot, data, count]);
				break;
			}
			
			case MemberwiseWitness:
			{
				var mapped = MapTypeSymbol(type);
				builder.BuildStore(data, builder.BuildStructGEP2(mapped, slot, 0, "data"));
				builder.BuildStore(count, builder.BuildStructGEP2(mapped, slot, 1, "length"));
				break;
			}
		}
	}
	
	private LLVMValueRef EmitIndexer(IndexerValue v, LLVMBuilderRef builder)
	{
		var (elemPtr, _) = EmitIndexerAddress(v, builder);
		return LoadValue(builder, v.Type, elemPtr, "elem");
	}
	
	private (LLVMValueRef Ptr, LLVMTypeRef ElemType) EmitIndexerAddress(IndexerValue v, LLVMBuilderRef builder)
	{
		var elemType = MapTypeSymbol(v.Type);
		
		switch (Substitute(v.Target.Type))
		{
			case ArrayType a:
			{
				var arrayType = MapTypeSymbol(a);
				LLVMValueRef arrayPtr;
				if (IsAddressable(v.Target))
				{
					arrayPtr = EmitAddress(v.Target, builder);
				}
				else
				{
					arrayPtr = SpillValue(builder, a, EmitValue(v.Target, builder), "array");
				}
				
				var index = EmitValue(v.Index, builder);
				var zero = LLVMValueRef.CreateConstInt(_context.Int32Type, 0);
				if (!checks.Bounds)
					return (builder.BuildGEP2(arrayType, arrayPtr, new[] { zero, index }, "elemptr"), elemType);
				
				var length = LLVMValueRef.CreateConstInt(index.TypeOf, (ulong)a.Length);
				PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, index, length), "index out of bounds",
					v.SourceLocation, builder);
				
				return (builder.BuildInBoundsGEP2(arrayType, arrayPtr, new[] { zero, index }, "elemptr"), elemType);
			}
			
			case StringType when v.Target.Type == NativeSymbols.Str:
			{
				var (length, data) = EmitStrParts(v.Target, builder);
				var index = EmitValue(v.Index, builder);
				if (!checks.Bounds)
					return (builder.BuildGEP2(_context.Int8Type, data, new[] { index }, "byteptr"), elemType);
				
				PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, index, length), "index out of bounds",
					v.SourceLocation, builder);
				
				return (builder.BuildInBoundsGEP2(_context.Int8Type, data, new[] { index }, "byteptr"), elemType);
			}
			
			case StringType:
			{
				var data = EmitValue(v.Target, builder);
				var index = EmitValue(v.Index, builder);
				return (builder.BuildGEP2(_context.Int8Type, data, new[] { index }, "byteptr"), elemType);
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private (LLVMValueRef Length, LLVMValueRef Data) EmitStrParts(Value str, LLVMBuilderRef builder)
	{
		if (!IsAddressable(str))
		{
			var value = EmitValue(str, builder);
			return (builder.BuildExtractValue(value, 0, "length"), builder.BuildExtractValue(value, 1, "data"));
		}
		
		var strType = MapTypeSymbol(NativeSymbols.Str);
		var address = EmitAddress(str, builder);
		var lengthAddress = builder.BuildStructGEP2(strType, address, 0, "length.addr");
		var dataAddress = builder.BuildStructGEP2(strType, address, 1, "data.addr");
		return (builder.BuildLoad2(strType.StructGetTypeAtIndex(0), lengthAddress, "length"),
			builder.BuildLoad2(strType.StructGetTypeAtIndex(1), dataAddress, "data"));
	}
	
	private LLVMValueRef EmitAccessAddress(AccessValue v, LLVMBuilderRef builder)
	{
		var targetPtr = EmitAddress(v.Target, builder);
		if (Substitute(v.Target.Type) is RecordSymbol { IsUnion: true })
			return targetPtr;
		
		var targetType = MapTypeSymbol(v.Target.Type);
		var fieldIndex = (uint)_typePool.GetFieldIndex(Substitute(v.Target.Type), v.Member);
		return builder.BuildStructGEP2(targetType, targetPtr, fieldIndex, v.Member.Name + ".addr");
	}
	
	private bool ReadsThroughUnion(Value value) => value switch
	{
		AccessValue access => Substitute(access.Target.Type) is RecordSymbol { IsUnion: true } ||
		                      ReadsThroughUnion(access.Target),
		IndexerValue indexer => ReadsThroughUnion(indexer.Target),
		_ => false
	};
	
	private LLVMValueRef? LoadUnionScalar(TypeSymbol type, LLVMValueRef address, AccessValue v, LLVMBuilderRef builder)
	{
		type = Substitute(type);
		if (type == NativeSymbols.Bool)
		{
			var stored = builder.BuildLoad2(_context.Int8Type, address, v.Member.Name);
			return builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, stored,
				LLVMValueRef.CreateConstNull(_context.Int8Type),
				v.Member.Name);
		}
		
		if (type != NativeSymbols.Char)
			return null;
		
		var value = builder.BuildLoad2(_context.Int32Type, address, v.Member.Name);
		if (checks.Overflow)
			PanicIf(builder.BuildNot(IsScalarValue(value, builder), "invalid"), "invalid char", v.SourceLocation,
				builder);
		
		return value;
	}
	
	private LLVMValueRef EmitAccessValue(AccessValue v, LLVMBuilderRef builder)
	{
		// Intrinsic properties
		switch (v.Member)
		{
			case PropertySymbol { Getter: NativeAccessor getter }:
				switch (getter.Intrinsic)
				{
					case NativeMemberIntrinsic.ArrayLength when Substitute(v.Target.Type) is ArrayType arrayType:
						return EmitSizeConstant(arrayType.Length, true);
				}
				
				break;
		}
		
		// If target has an address, GEP + load only the field
		if (IsAddressable(v.Target))
		{
			var fieldAddress = EmitAccessAddress(v, builder);
			var fieldType = _typePool.GetTypeOfMember((TypedMemberSymbol)v.Member);
			return ReadsThroughUnion(v) && LoadUnionScalar(fieldType, fieldAddress, v, builder) is { } scalar
				? scalar
				: LoadValue(builder, fieldType, fieldAddress, v.Member.Name);
		}
		
		// TODO Fields could have been reordered to pack them
		// TODO Also, GetFieldIndex is O(n), would probably want to cache the final indices in another dictionary
		var target = EmitValue(v.Target, builder);
		if (Substitute(v.Target.Type) is RecordSymbol { IsUnion: true } union)
		{
			var stored = SpillValue(builder, union, target, "union");
			return LoadUnionScalar(v.Type, stored, v, builder) ?? LoadValue(builder, v.Type, stored, v.Member.Name);
		}
		
		var fieldIndex = (uint)_typePool.GetFieldIndex(Substitute(v.Target.Type), v.Member);
		return ExtractField(builder, v.Target.Type, target, fieldIndex, v.Type, v.Member.Name);
	}
	
	private LLVMValueRef EmitArrayValue(ArrayValue value, LLVMBuilderRef builder)
	{
		var arrayType = MapTypeSymbol(value.ArrayType);
		if (value.IsConstant)
		{
			var constant = EmitArrayConstant(value, builder);
			if (!IsLarge(value.ArrayType))
				return constant;
			
			var global = current.Module.AddGlobal(arrayType, "array");
			global.Initializer = constant;
			global.IsGlobalConstant = true;
			global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
			global.HasUnnamedAddr = true;
			return LoadValue(builder, value.ArrayType, global, "array");
		}
		
		if (IsLarge(value.ArrayType))
		{
			var slot = BuildEntryAlloca(builder, arrayType, "array");
			var elementType = value.ArrayType.ElementType;
			for (var i = 0; i < value.Elements.Length; i++)
			{
				var index = LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)i);
				StoreValue(builder, elementType, EmitValue(value.Elements[i], builder),
					builder.BuildGEP2(arrayType, slot, new[] { Int32Zero, index }, "element"));
			}
			
			return slot;
		}
		
		var agg = arrayType.Undef;
		for (uint i = 0; i < value.Elements.Length; i++)
			agg = builder.BuildInsertValue(agg, EmitValue(value.Elements[(int)i], builder), i, $"arr{i}");
		
		return agg;
	}
	
	private LLVMValueRef EmitArrayConstant(ArrayValue value, LLVMBuilderRef builder) =>
		LLVMValueRef.CreateConstArray(MapTypeSymbol(value.ArrayType.ElementType),
		[
			..value.Elements.Select(element => element switch
			{
				ArrayValue { IsConstant: true } array => EmitArrayConstant(array, builder),
				ConstantValue { Value: null } => LLVMValueRef.CreateConstNull(MapTypeSymbol(element.Type)),
				_ => EmitValue(element, builder)
			})
		]);
	
	private LLVMValueRef EmitAssignValue(AssignValue v, LLVMBuilderRef builder)
	{
		var right = EmitValue(v.Right, builder);
		StoreValue(builder, v.Left.Type, right, EmitAddress(v.Left, builder));
		return right;
	}
	
	private LLVMValueRef EmitAddress(Value value, LLVMBuilderRef builder) => value switch
	{
		VariableValue v => current.Variables[v.Variable],
		GlobalValue v => EmitGlobalAddress(v.Global, builder),
		IndexerValue v => EmitIndexerAddress(v, builder).Ptr,
		AccessValue v => EmitAccessAddress(v, builder),
		UnaryOpValue { Op: UnaryOperation.Dereference } v => EmitValue(v.Operand, builder),
		EnumPayloadValue v => EmitEnumPayloadAddress(v, builder),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitConstant(ConstantValue constant)
	{
		var type = MapTypeSymbol(constant.Type);
		
		if (constant.Value is not { } value)
			return LLVMValueRef.CreateConstNull(type);
		
		if (constant.Type is PrimitiveType primitiveType)
		{
			switch (primitiveType.Kind)
			{
				case PrimitiveTypeKind.Int8:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(sbyte)value), true);
				
				case PrimitiveTypeKind.Int16:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(short)value), true);
				
				case PrimitiveTypeKind.Int32:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(int)value), true);
				
				case PrimitiveTypeKind.Int64:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(long)value), true);
				
				case PrimitiveTypeKind.Int128:
				{
					// Little Endian
					var i128 = (Int128)value;
					Span<ulong> words = stackalloc ulong[2];
					words[0] = unchecked((ulong)i128);
					words[1] = unchecked((ulong)(i128 >> 64));
					return LLVMValueRef.CreateConstIntOfArbitraryPrecision(type, words);
				}
				
				case PrimitiveTypeKind.IntSize:
					return EmitSizeConstant((BigInteger)value, false);
				
				case PrimitiveTypeKind.UInt8:
					return LLVMValueRef.CreateConstInt(type, (byte)value);
				
				case PrimitiveTypeKind.UInt16:
					return LLVMValueRef.CreateConstInt(type, (ushort)value);
				
				case PrimitiveTypeKind.UInt32:
					return LLVMValueRef.CreateConstInt(type, (uint)value);
				
				case PrimitiveTypeKind.UInt64:
					return LLVMValueRef.CreateConstInt(type, (ulong)value);
				
				case PrimitiveTypeKind.UInt128:
				{
					// Little Endian
					var u128 = (UInt128)value;
					Span<ulong> words = stackalloc ulong[2];
					words[0] = (ulong)u128;
					words[1] = (ulong)(u128 >> 64);
					return LLVMValueRef.CreateConstIntOfArbitraryPrecision(type, words);
				}
				
				case PrimitiveTypeKind.UIntSize:
					return EmitSizeConstant((BigInteger)value, true);
				
				case PrimitiveTypeKind.Char:
					return LLVMValueRef.CreateConstInt(type, (uint)value);
				
				case PrimitiveTypeKind.Float32:
				case PrimitiveTypeKind.Float64:
					return LLVMValueRef.CreateConstReal(type, (double)value);
				
				case PrimitiveTypeKind.Str:
				{
					var lengthType = MapTypeSymbol(NativeSymbols.UIntSize);
					var str = (StrValue)value;
					var ptr = GetOrCreateStringGlobal(str.Bytes);
					
					var length = LLVMValueRef.CreateConstInt(lengthType, str.Length);
					return _context.GetConstStruct([length, ptr], false);
				}
				
				case PrimitiveTypeKind.CStr:
					return GetOrCreateStringGlobal((byte[])value);
				
				case PrimitiveTypeKind.Bool:
					return (bool)value ? _true : _false;
			}
		}
		
		throw new InvalidOperationException();
	}
}