using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private LLVMValueRef EmitConversion(ConversionValue v, LLVMBuilderRef builder) => v.Conversion switch
	{
		IdentityConversion => EmitValue(v.Source, builder),
		IntegerConversion { To: var to } c when Substitute(to) == NativeSymbols.Char =>
			ConvertToChar(EmitValue(v.Source, builder), c.FromSigned, v.SourceLocation, builder),
		FreeConversion { From: IntegerType, To: var to } when Substitute(to) == NativeSymbols.Char =>
			ConvertToChar(EmitValue(v.Source, builder), false, v.SourceLocation, builder),
		IntegerConversion c => ConvertInteger(EmitValue(v.Source, builder), MapTypeSymbol(c.To), c.FromSigned,
			c.ToSigned, v.SourceLocation, builder),
		FloatConversion c => EmitFloatConversion(c, EmitValue(v.Source, builder), v.SourceLocation, builder),
		NativeConversion c => EmitNativeConversion(c, v, builder),
		EnumConversion c => EmitEnumConversion(c, v, builder),
		FreeConversion or MatchConversion => EmitValue(v.Source, builder),
		FunctionConversion c => EmitValue(new CallValue(c.Function, [v.Source], v.SourceLocation), builder),
		DynConversion c => EmitDynConversion(c, v, builder),
		DynUpcastConversion c => EmitDynUpcast(c, v, builder),
		DynLayoutConversion c => EmitDynLayout(c, v, builder),
		DynTestConversion c => EmitDynTest(c, v, builder),
		DynCastConversion => builder.BuildExtractValue(EmitValue(v.Source, builder), 0, "object"),
		CallableConversion c => EmitCallableConversion(c, v, builder),
		ExternalFunctionConversion c => BuildFunctionValue(MapTypeSymbol(c.To),
			GetExternalCallThunk((FunctionType)Substitute(c.From)), EmitValue(v.Source, builder), builder),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitCallableConversion(CallableConversion conversion, ConversionValue v,
		LLVMBuilderRef builder)
	{
		var address = EmitValue(v.Source, builder);
		var type = MapTypeSymbol(conversion.To);
		var target = (FunctionType)Substitute(conversion.To);
		switch (Substitute(conversion.From))
		{
			case ClosureType closure when TakesCopies(closure.Signature, target):
				return BuildFunctionValue(type, GetClosureCallThunk(target, closure), address, builder);
			
			case ClosureType closure:
				return BuildFunctionValue(type, GetFunctionValue(closure.Function), address, builder);
			
			case FunctionType stored when TakesCopies(stored, target):
				return BuildFunctionValue(type, GetStoredCallThunk(target, stored), address, builder);
			
			case FunctionType { IsExternal: true } external:
				return BuildFunctionValue(type, GetExternalCallThunk(external),
					builder.BuildLoad2(OpaquePointer, address, "function"), builder);
			
			case FunctionType:
				return builder.BuildLoad2(type, address, "function");
			
			case DynType { Function: not null }:
				var table = builder.BuildExtractValue(address, 1, "table");
				var entry = builder.BuildStructGEP2(FunctionDynTableType, table, (uint)DynTableHeader.Length, "entry");
				return BuildFunctionValue(type, builder.BuildLoad2(OpaquePointer, entry, "code"),
					builder.BuildExtractValue(address, 0, "object"), builder);
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitNativeConversion(NativeConversion c, ConversionValue v, LLVMBuilderRef builder)
	{
		var source = EmitValue(v.Source, builder);
		
		// Pointers
		switch (c)
		{
			case { From: PointerType, To: PointerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildBitCast(source, destType, "ptrcast");
			}
			
			case { From: PointerType, To: IntegerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildPtrToInt(source, destType, "ptrtoint");
			}
			
			case { From: IntegerType, To: PointerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildIntToPtr(source, destType, "inttoptr");
			}
			
			case { From: PrimitiveType { Kind: PrimitiveTypeKind.CStr }, To: IntegerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildPtrToInt(source, destType, "cstrtoint");
			}
			
			case { From: IntegerType, To: PrimitiveType { Kind: PrimitiveTypeKind.CStr } }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildIntToPtr(source, destType, "inttocstr");
			}
			
			case { From: PrimitiveType { Kind: PrimitiveTypeKind.Bool }, To: IntegerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildZExt(source, destType, "booltoint");
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private bool IsAddressable(Value value) => value switch
	{
		VariableValue => true,
		GlobalValue => true,
		IndexerValue { Target.Type: StringType } => true,
		IndexerValue v => IsAddressable(v.Target),
		AccessValue v => IsAddressable(v.Target),
		UnaryOpValue { Op: UnaryOperation.Dereference } => true,
		_ => false
	};
	
	private LLVMValueRef ConvertToChar(LLVMValueRef source, bool fromSigned, SourceLocation location,
		LLVMBuilderRef builder)
	{
		var wide = source.TypeOf.IntWidth < 64
			? fromSigned
				? builder.BuildSExt(source, _context.Int64Type, "wide")
				: builder.BuildZExt(source, _context.Int64Type, "wide")
			: source;
		
		if (_config.OverflowChecks)
			PanicIf(builder.BuildNot(IsScalarValue(wide, builder), "invalid"), "conversion overflow", location,
				builder);
		
		return ResizeInteger(source, _context.Int32Type, fromSigned, builder);
	}
	
	private LLVMValueRef ConvertInteger(LLVMValueRef source, LLVMTypeRef destType, bool fromSigned, bool toSigned,
		SourceLocation location, LLVMBuilderRef builder)
	{
		var result = ResizeInteger(source, destType, fromSigned, builder);
		var (fromBits, toBits) = (source.TypeOf.IntWidth, destType.IntWidth);
		if (!_config.OverflowChecks || (fromSigned == toSigned ? toBits >= fromBits : !fromSigned && toBits > fromBits))
			return result;
		
		var back = ResizeInteger(result, source.TypeOf, toSigned, builder);
		var lost = builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, back, source, "lost");
		if (fromSigned != toSigned)
		{
			var signed = fromSigned ? source : result;
			var zero = LLVMValueRef.CreateConstNull(signed.TypeOf);
			var negative = builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, signed, zero, "negative");
			lost = builder.BuildOr(lost, negative, "lost");
		}
		
		PanicIf(lost, "conversion overflow", location, builder);
		return result;
	}
	
	private static LLVMValueRef ResizeInteger(LLVMValueRef source, LLVMTypeRef destType, bool isSigned,
		LLVMBuilderRef builder)
	{
		var srcType = source.TypeOf;
		
		if (destType.IntWidth == srcType.IntWidth)
			return source;
		
		if (destType.IntWidth < srcType.IntWidth)
			return builder.BuildTrunc(source, destType);
		
		return isSigned
			? builder.BuildSExt(source, destType)
			: builder.BuildZExt(source, destType);
	}
	
	private LLVMValueRef EmitFloatConversion(FloatConversion c, LLVMValueRef source, SourceLocation location,
		LLVMBuilderRef builder)
	{
		var destType = MapTypeSymbol(c.To);
		return (c.From, c.To) switch
		{
			(FloatType, FloatType) when CountBits(c.To) > CountBits(c.From) => builder.BuildFPExt(source, destType),
			(FloatType, FloatType) => builder.BuildFPTrunc(source, destType),
			(IntegerType { IsSigned: true }, _) => builder.BuildSIToFP(source, destType),
			(IntegerType, _) => builder.BuildUIToFP(source, destType),
			(_, IntegerType to) => EmitFloatToInteger(source, destType, to.IsSigned, location, builder),
			_ => throw new InvalidOperationException()
		};
	}
	
	private LLVMValueRef EmitFloatToInteger(LLVMValueRef source, LLVMTypeRef destType, bool isSigned,
		SourceLocation location, LLVMBuilderRef builder)
	{
		if (_config.OverflowChecks)
			PanicIf(builder.BuildNot(FitsInteger(source, destType.IntWidth, isSigned, builder)), "conversion overflow",
				location, builder);
		
		return BuildSaturatingFloatToInt(source, destType, isSigned, builder);
	}
	
	private static LLVMValueRef FitsInteger(LLVMValueRef source, uint bits, bool isSigned, LLVMBuilderRef builder)
	{
		var upper = BigInteger.One << (int)(isSigned ? bits - 1 : bits);
		var lower = isSigned ? -upper : BigInteger.Zero;
		var below = lower - 1;
		var exact = source.TypeOf.Kind == LLVMTypeKind.LLVMFloatTypeKind
			? new BigInteger((float)below) == below
			: new BigInteger((double)below) == below;
		
		var predicate = exact ? LLVMRealPredicate.LLVMRealOGT : LLVMRealPredicate.LLVMRealOGE;
		var aboveLower = builder.BuildFCmp(predicate, source,
			LLVMValueRef.CreateConstReal(source.TypeOf, (double)(exact ? below : lower)), "above");
		
		var belowUpper = builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, source,
			LLVMValueRef.CreateConstReal(source.TypeOf, (double)upper), "below");
		
		return builder.BuildAnd(aboveLower, belowUpper, "fits");
	}
	
	private LLVMValueRef BuildSaturatingFloatToInt(LLVMValueRef source, LLVMTypeRef destType, bool isSigned,
		LLVMBuilderRef builder)
	{
		var sourceName = source.TypeOf.Kind == LLVMTypeKind.LLVMFloatTypeKind ? "f32" : "f64";
		var name = $"llvm.{(isSigned ? "fptosi" : "fptoui")}.sat.i{destType.IntWidth}.{sourceName}";
		var functionType = LLVMTypeRef.CreateFunction(destType, [source.TypeOf]);
		return builder.BuildCall2(functionType, GetIntrinsic(name, functionType), [source]);
	}
	
	private LLVMValueRef GetIntrinsic(string name, LLVMTypeRef functionType)
	{
		var function = current.Module.GetNamedFunction(name);
		return function.Handle == IntPtr.Zero ? current.Module.AddFunction(name, functionType) : function;
	}
	
	private uint CountBits(TypeSymbol type) =>
		(uint)_typePool.SizeTable.GetSize(Substitute(type)).CountBits(_pointerSize * 8);
}