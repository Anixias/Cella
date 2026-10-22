using System.Buffers.Binary;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private LLVMValueRef EmitStaticConstant(Constant constant) => constant switch
	{
		IntegerConstant c => EmitIntegerConstant(c),
		FloatConstant { Bits: { } bits } c => EmitFloatBits(MapTypeSymbol(c.Type), bits),
		FloatConstant c => LLVMValueRef.CreateConstReal(MapTypeSymbol(c.Type), c.Value),
		BoolConstant c => c.Value ? _true : _false,
		NullConstant c => LLVMValueRef.CreateConstNull(MapTypeSymbol(c.Type)),
		StringConstant c => EmitConstant(new ConstantValue(c.Type, c.Value)),
		RecordConstant c => EmitStructConstant(MapTypeSymbol(c.Type), [..c.Fields.Select(EmitStaticConstant)]),
		UnionConstant c => EmitUnionConstant(c),
		ArrayConstant c => EmitArrayConstant(MapTypeSymbol(c.ArrayType.ElementType),
			[..c.Elements.Select(EmitStaticConstant)]),
		EnumConstant c => EmitEnumConstant(c),
		EnumTagConstant c => EmitEnumTagConstant(c),
		FunctionConstant c => EmitFunctionReference(c.Function, c.Type),
		ZeroConstant c => LLVMValueRef.CreateConstNull(MapTypeSymbol(c.Type)),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitFloatBits(LLVMTypeRef type, ulong bits) => LLVM.ConstBitCast(
		LLVMValueRef.CreateConstInt(
			type.Kind == LLVMTypeKind.LLVMFloatTypeKind ? _context.Int32Type : _context.Int64Type,
			bits), type);
	
	private LLVMValueRef EmitUnionConstant(UnionConstant constant)
	{
		var type = MapTypeSymbol(constant.Type);
		if (type.StructElementTypesCount == 0)
			return LLVMValueRef.CreateConstNull(type);
		
		var storage = type.StructGetTypeAtIndex(0);
		var word = storage.ElementType;
		var wordBytes = (int)word.IntWidth / 8;
		var bytes = new byte[wordBytes * storage.ArrayLength];
		constant.Bytes.AsSpan(0, Math.Min(bytes.Length, constant.Bytes.Length)).CopyTo(bytes);
		var words = new LLVMValueRef[storage.ArrayLength];
		for (var i = 0; i < words.Length; i++)
		{
			var chunk = new ulong[(wordBytes + 7) / 8];
			for (var j = 0; j < wordBytes; j++)
				chunk[j / 8] |= (ulong)bytes[i * wordBytes + j] << (j % 8 * 8);
			
			words[i] = LLVMValueRef.CreateConstIntOfArbitraryPrecision(word, chunk);
		}
		
		return LLVMValueRef.CreateConstNamedStruct(type, [LLVMValueRef.CreateConstArray(word, words)]);
	}
	
	private LLVMValueRef EmitStructConstant(LLVMTypeRef type, LLVMValueRef[] fields) =>
		fields.Select((field, i) => field.TypeOf == type.StructGetTypeAtIndex((uint)i)).All(static same => same)
			? LLVMValueRef.CreateConstNamedStruct(type, fields)
			: _context.GetConstStruct(fields, false);
	
	private LLVMValueRef EmitArrayConstant(LLVMTypeRef elementType, LLVMValueRef[] elements) =>
		elements.All(element => element.TypeOf == elementType)
			? LLVMValueRef.CreateConstArray(elementType, elements)
			: _context.GetConstStruct(elements, false);
	
	private LLVMValueRef EmitEnumConstant(EnumConstant constant)
	{
		if (Substitute(constant.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return constant.Payload is [var value]
				? EmitStaticConstant(value)
				: EmitMatchConstant(matchEnum, _typePool.GetMatchValues(matchEnum, constant.Case)[0]);
		
		var enumType = MapTypeSymbol(constant.Type);
		var tag = EmitCaseTag((EnumSymbol)constant.Type, constant.Case);
		if (constant.Payload.IsEmpty)
			return EmitTagOnly(enumType, tag);
		
		var area = enumType.StructGetTypeAtIndex(1);
		var payload = _context.GetConstStruct([..constant.Payload.Select(EmitStaticConstant)], false);
		var alignment = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreateArray(area.ElementType, 0));
		var tail = _targetData.ABISizeOfType(area) - _targetData.ABISizeOfType(payload.TypeOf);
		if (tail == 0)
			return _context.GetConstStruct([tag, alignment, payload], false);
		
		var padding = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreateArray(_context.Int8Type, (uint)tail));
		return _context.GetConstStruct([tag, alignment, payload, padding], false);
	}
	
	private LLVMValueRef EmitEnumTagConstant(EnumTagConstant constant)
	{
		if (Substitute(constant.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return EmitMatchConstant(matchEnum, constant.Tag);
		
		var enumType = (EnumSymbol)constant.Type;
		var tag = EmitIntegerConstant(new IntegerConstant(_typePool.GetTagType(enumType), constant.Tag));
		return EmitTagOnly(MapTypeSymbol(enumType), tag);
	}
	
	private static LLVMValueRef EmitTagOnly(LLVMTypeRef enumType, LLVMValueRef tag) =>
		enumType.StructElementTypesCount == 1
			? LLVMValueRef.CreateConstNamedStruct(enumType, [tag])
			: LLVMValueRef.CreateConstNamedStruct(enumType,
				[tag, LLVMValueRef.CreateConstNull(enumType.StructGetTypeAtIndex(1))]);
	
	private LLVMValueRef EmitIntegerConstant(IntegerConstant constant) =>
		EmitIntegerPattern(constant.Value, MapTypeSymbol(constant.Type));
	
	private static LLVMValueRef EmitIntegerPattern(BigInteger value, LLVMTypeRef type)
	{
		var bits = (int)type.IntWidth;
		var pattern = value.Sign < 0 ? value + (BigInteger.One << bits) : value;
		var words = new ulong[GetWordCount(bits)];
		for (var i = 0; i < words.Length; i++)
			words[i] = (ulong)((pattern >> (BitsPerWord * i)) & ulong.MaxValue);
		
		return LLVMValueRef.CreateConstIntOfArbitraryPrecision(type, words);
	}
	
	private LLVMValueRef GetOrCreateStringGlobal(byte[] bytes)
	{
		if (current.Strings.TryGetValue(bytes, out var existing))
			return existing;
		
		var byteType = MapTypeSymbol(NativeSymbols.UInt8);
		var byteValues = bytes.Select(b => LLVMValueRef.CreateConstInt(byteType, b));
		var arrayValue = LLVMValueRef.CreateConstArray(byteType, [..byteValues]);
		
		var global = current.Module.AddGlobal(arrayValue.TypeOf, string.Empty);
		global.Initializer = arrayValue;
		global.IsGlobalConstant = true;
		global.Linkage = LLVMLinkage.LLVMLinkerPrivateLinkage;
		global.HasUnnamedAddr = true;
		
		var ptr = LLVMValueRef.CreateConstInBoundsGEP2(byteType, global,
			[LLVMValueRef.CreateConstInt(_context.Int32Type, 0)]);
		
		current.Strings[bytes] = ptr;
		return ptr;
	}
	
	private LLVMValueRef EmitSizeConstant(BigInteger value, bool isUnsigned)
	{
		var usizeType = MapTypeSymbol(isUnsigned ? NativeSymbols.UIntSize : NativeSymbols.IntSize);
		var ptrBits = (int)(_pointerSize * 8);
		var wordCount = GetWordCount(ptrBits);
		Span<ulong> words = stackalloc ulong[wordCount];
		BigIntegerToWords(value, ptrBits, isUnsigned, words);
		return LLVMValueRef.CreateConstIntOfArbitraryPrecision(usizeType, words);
	}
	
	private static void BigIntegerToWords(BigInteger value, int maxBitCount, bool isUnsigned, Span<ulong> words)
	{
		switch (maxBitCount)
		{
			case < 0:
				throw new ArgumentException($"{nameof(maxBitCount)} must be non‑negative.", nameof(maxBitCount));
			
			case 0:
				return;
		}
		
		var maxByteSize = (value.GetByteCount() + 7) & ~7;
		Span<byte> bytes = stackalloc byte[maxByteSize];
		value.TryWriteBytes(bytes, out var bytesWritten, isUnsigned);
		
		for (var i = 0; i < words.Length; i++)
		{
			var offset = i * 8;
			var word = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
			
			// The last word may contain padding bytes, need to sign-extend if signed
			if (!isUnsigned && i == words.Length - 1)
			{
				var lastWordBytes = bytesWritten & 7;
				if (lastWordBytes == 0)
					lastWordBytes = 8;
				
				if (lastWordBytes < 8)
				{
					// If highest bit is set, sign-extend padding bytes
					var msb = bytes[offset + lastWordBytes - 1];
					if ((msb & 0x80) != 0)
						word |= ulong.MaxValue << (lastWordBytes * 8);
				}
			}
			
			words[i] = word;
		}
	}
	
	private const int BitsPerWord = sizeof(ulong) * 8;
	
	/// <summary>
	/// Returns the number of ulong words required to hold <see cref="maxBitCount"/> bits.
	/// </summary>
	private static int GetWordCount(int maxBitCount) => (maxBitCount + BitsPerWord - 1) / BitsPerWord;
}