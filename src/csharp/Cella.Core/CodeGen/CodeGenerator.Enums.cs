using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private LLVMValueRef EmitEnumValue(EnumValue v, LLVMBuilderRef builder)
	{
		if (Substitute(v.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return EmitMatchValue(matchEnum, v, builder);
		
		var enumType = MapTypeSymbol(v.Type);
		var tag = EmitCaseTag((EnumSymbol)v.Type, v.Case);
		if (v.Payload.IsEmpty)
			return EmitTagged((EnumSymbol)v.Type, tag, builder);
		
		var slot = SpillValue(builder, v.Type, EmitZeroed(builder, v.Type, v.Case.Name), v.Case.Name);
		builder.BuildStore(tag, builder.BuildStructGEP2(enumType, slot, 0, "tag.addr"));
		var payloadType = GetPayloadType(v.Case);
		var payload = builder.BuildStructGEP2(enumType, slot, 1, "payload");
		for (var i = 0; i < v.Payload.Length; i++)
			StoreValue(builder, v.Payload[i].Type, EmitValue(v.Payload[i], builder),
				builder.BuildStructGEP2(payloadType, payload, (uint)i, v.Case.Fields[i].Name));
		
		return IsLarge(v.Type) ? slot : builder.BuildLoad2(enumType, slot, v.Case.Name);
	}
	
	private LLVMValueRef EmitTagged(EnumSymbol type, LLVMValueRef tag, LLVMBuilderRef builder)
	{
		if (!IsLarge(type))
			return builder.BuildInsertValue(LLVMValueRef.CreateConstNull(MapTypeSymbol(type)), tag, 0, type.Name);
		
		var slot = EmitZeroed(builder, type, type.Name);
		builder.BuildStore(tag, builder.BuildStructGEP2(MapTypeSymbol(type), slot, 0, "tag.addr"));
		return slot;
	}
	
	private LLVMValueRef EmitMatchValue(EnumSymbol enumType, EnumValue v, LLVMBuilderRef builder)
	{
		var enumCase = enumType.Cases[v.Case.Index];
		if (v.Payload.IsEmpty)
			return EmitMatchConstant(enumType, _typePool.GetMatchValues(enumType, enumCase)[0]);
		
		var value = EmitValue(v.Payload[0], builder);
		var isElse = enumCase.Node.Else is not null;
		var listed = EmitIsListed(enumType, value, isElse ? enumType.Cases : [enumCase], builder);
		PanicIf(isElse ? listed : builder.BuildNot(listed), $"'{enumType.Name}.{enumCase.Name}' can't hold this value",
			v.SourceLocation, builder);
		
		return value;
	}
	
	private LLVMValueRef EmitIsListed(EnumSymbol enumType, LLVMValueRef value, IEnumerable<EnumCaseSymbol> cases,
		LLVMBuilderRef builder)
	{
		var result = _false;
		foreach (var pattern in cases.SelectMany(enumCase => _typePool.GetMatchValues(enumType, enumCase)))
		{
			var test = EmitMatchTest(enumType, value, pattern, builder);
			result = result.Handle == _false.Handle ? test : builder.BuildOr(result, test);
		}
		
		return result;
	}
	
	private LLVMValueRef EmitMatchTag(EnumSymbol enumType, LLVMValueRef value, LLVMBuilderRef builder)
	{
		var tagType = _typePool.GetTagType(enumType);
		var fallback = TypePool.GetElseCase(enumType) ?? enumType.Cases[^1];
		var tag = EmitIntegerConstant(new IntegerConstant(tagType, fallback.Index));
		foreach (var enumCase in enumType.Cases)
		{
			var caseTag = EmitIntegerConstant(new IntegerConstant(tagType, enumCase.Index));
			foreach (var pattern in _typePool.GetMatchValues(enumType, enumCase))
				tag = builder.BuildSelect(EmitMatchTest(enumType, value, pattern, builder), caseTag, tag, "tag");
		}
		
		return tag;
	}
	
	private LLVMValueRef EmitMatchTest(EnumSymbol enumType, LLVMValueRef value, BigInteger pattern,
		LLVMBuilderRef builder)
	{
		if (_typePool.GetMatchedType(enumType) is FunctionType { IsExternal: false })
			value = builder.BuildExtractValue(value, 0, "code");
		
		var expected = value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
			? LLVMValueRef.CreateConstNull(value.TypeOf)
			: EmitMatchConstant(enumType, pattern);
		
		return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value, expected, "listed");
	}
	
	private LLVMValueRef EmitMatchConstant(EnumSymbol enumType, BigInteger pattern)
	{
		var matchedType = _typePool.GetMatchedType(enumType);
		return matchedType switch
		{
			IntegerStorageType => EmitIntegerConstant(new IntegerConstant(matchedType, pattern)),
			_ when matchedType == NativeSymbols.Bool => pattern.IsZero ? _false : _true,
			_ => LLVMValueRef.CreateConstNull(MapTypeSymbol(matchedType))
		};
	}
	
	private LLVMValueRef EmitCaseTag(EnumSymbol enumType, EnumCaseSymbol enumCase) => EmitIntegerConstant(
		new IntegerConstant(_typePool.GetTagType(enumType), _typePool.GetCaseValue(enumType, enumCase)));
	
	private LLVMValueRef EmitEnumConversion(EnumConversion c, ConversionValue v, LLVMBuilderRef builder)
	{
		var source = EmitValue(v.Source, builder);
		if (c.From is EnumSymbol from)
		{
			var tag = ExtractField(builder, from, source, 0, _typePool.GetTagType(from), "tag");
			return ConvertInteger(tag, MapTypeSymbol(c.To), _typePool.GetTagType(from).IsSigned,
				((IntegerStorageType)c.To).IsSigned, v.SourceLocation, builder);
		}
		
		var to = (EnumSymbol)c.To;
		var sourceType = (IntegerStorageType)c.From;
		if (!to.IsExternal)
			PanicIf(builder.BuildNot(IsCaseValue(to, source, sourceType, builder)),
				$"'{to.Name}' has no case with this value", v.SourceLocation, builder);
		
		var tagType = _typePool.GetTagType(to);
		var resized = to.IsExternal
			? ConvertInteger(source, MapTypeSymbol(tagType), sourceType.IsSigned, tagType.IsSigned, v.SourceLocation,
				builder)
			: ResizeInteger(source, MapTypeSymbol(tagType), sourceType.IsSigned, builder);
		
		return EmitTagged(to, resized, builder);
	}
	
	private LLVMValueRef IsCaseValue(EnumSymbol enumType, LLVMValueRef value, IntegerStorageType type,
		LLVMBuilderRef builder)
	{
		var bits = (int)value.TypeOf.IntWidth;
		var minimum = type.IsSigned ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
		var maximum = type.IsSigned ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
		var values = enumType.Cases
			.Select(enumCase => _typePool.GetCaseValue(enumType, enumCase))
			.Where(caseValue => caseValue >= minimum && caseValue <= maximum)
			.Distinct()
			.Order()
			.ToList();
		
		var result = _false;
		var start = 0;
		while (start < values.Count)
		{
			var end = start;
			while (end + 1 < values.Count && values[end + 1] == values[end] + 1)
				end++;
			
			var low = EmitIntegerConstant(new IntegerConstant(type, values[start]));
			var span = EmitIntegerConstant(new IntegerConstant(type, values[end] - values[start]));
			var offset = builder.BuildSub(value, low);
			var inRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, offset, span);
			result = result.Handle == _false.Handle ? inRange : builder.BuildOr(result, inRange);
			start = end + 1;
		}
		
		return result;
	}
	
	private LLVMValueRef EmitEnumTag(EnumTagValue v, LLVMBuilderRef builder)
	{
		if (Substitute(v.Target.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return EmitMatchTag(matchEnum, EmitValue(v.Target, builder), builder);
		
		if (!IsAddressable(v.Target))
			return ExtractField(builder, v.Target.Type, EmitValue(v.Target, builder), 0, v.Type, "tag");
		
		var enumType = MapTypeSymbol(v.Target.Type);
		var address = builder.BuildStructGEP2(enumType, EmitAddress(v.Target, builder), 0, "tag.addr");
		return builder.BuildLoad2(MapTypeSymbol(v.Type), address, "tag");
	}
	
	private LLVMValueRef EmitEnumPayload(EnumPayloadValue v, LLVMBuilderRef builder)
	{
		if (Substitute(v.Target.Type) is EnumSymbol { IsMatch: true })
			return EmitValue(v.Target, builder);
		
		var name = v.Case.Fields[v.Index].Name;
		return LoadValue(builder, v.Type, EmitEnumPayloadAddress(v, builder), name);
	}
	
	private LLVMValueRef EmitEnumPayloadAddress(EnumPayloadValue v, LLVMBuilderRef builder)
	{
		var enumType = MapTypeSymbol(v.Target.Type);
		LLVMValueRef address;
		if (IsAddressable(v.Target))
		{
			address = EmitAddress(v.Target, builder);
		}
		else
		{
			address = SpillValue(builder, v.Target.Type, EmitValue(v.Target, builder), "scrutinee");
		}
		
		if (Substitute(v.Target.Type) is EnumSymbol { IsMatch: true })
			return address;
		
		var name = v.Case.Fields[v.Index].Name;
		var payload = builder.BuildStructGEP2(enumType, address, 1, "payload");
		return builder.BuildStructGEP2(GetPayloadType(v.Case), payload, (uint)v.Index, name + ".addr");
	}
}