using System.Collections.Immutable;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

internal enum CPassKind
{
	Direct,
	Coerced,
	Indirect
}

internal enum CExtension
{
	None,
	Sign,
	Zero
}

internal readonly record struct CPass(CPassKind Kind, LLVMTypeRef Type)
{
	public bool IsByValue { get; init; }
	public CExtension Extension { get; init; }
}

internal readonly record struct CSignature(ImmutableArray<CPass> Parameters, CPass Return)
{
	public LLVMTypeRef CreateFunctionType(bool isVariadic)
	{
		var context = Return.Type.Context;
		var pointerType = LLVMTypeRef.CreatePointer(context.Int8Type, 0);
		var parameterTypes = new List<LLVMTypeRef>(Parameters.Length + 1);
		if (Return.Kind == CPassKind.Indirect)
			parameterTypes.Add(pointerType);
		
		foreach (var parameter in Parameters)
			parameterTypes.Add(parameter.Kind == CPassKind.Indirect ? pointerType : parameter.Type);
		
		var returnType = Return.Kind == CPassKind.Indirect ? context.VoidType : Return.Type;
		return LLVMTypeRef.CreateFunction(returnType, parameterTypes.ToArray(), isVariadic);
	}
}

internal sealed class CAbi(LLVMTargetDataRef targetData, TargetConfig target)
{
	private const int IntegerRegisters = 6;
	private const int SseRegisters = 8;
	
	private readonly bool _isWindowsX64 = target is
		{ Architecture: TargetArchitecture.X64, System: TargetSystem.Windows };
	
	private readonly bool _isSystemV = target is
		{ Architecture: TargetArchitecture.X64, System: not TargetSystem.Windows };
	
	public bool ExtendsSmallIntegers => _isSystemV;
	
	public CSignature Classify(IEnumerable<(LLVMTypeRef Type, CExtension Extension)> parameters,
		(LLVMTypeRef Type, CExtension Extension) result)
	{
		if (!_isSystemV)
			return new([
				..parameters.Select(parameter => ClassifyValue(parameter.Type, false) with
				{
					Extension = parameter.Extension
				})
			], ClassifyValue(result.Type, true) with { Extension = result.Extension });
		
		var registers = new Registers(IntegerRegisters, SseRegisters);
		var returned = ClassifySystemV(result.Type, true, ref registers) with { Extension = result.Extension };
		if (returned.Kind == CPassKind.Indirect)
			registers.Integer--;
		
		var passed = parameters
			.Select(parameter => ClassifySystemV(parameter.Type, false, ref registers) with
			{
				Extension = parameter.Extension
			})
			.ToImmutableArray();
		
		return new(passed, returned);
	}
	
	private CPass ClassifyValue(LLVMTypeRef type, bool isReturn)
	{
		if (_isWindowsX64 && type is { Kind: LLVMTypeKind.LLVMIntegerTypeKind, IntWidth: 128 })
			return isReturn
				? new(CPassKind.Coerced, LLVMTypeRef.CreateVector(type.Context.Int64Type, 2))
				: new(CPassKind.Indirect, type);
		
		if (type.Kind is not (LLVMTypeKind.LLVMStructTypeKind or LLVMTypeKind.LLVMArrayTypeKind))
			return new(CPassKind.Direct, type);
		
		if (!_isWindowsX64)
			throw new NotSupportedException(
				$"Records cannot be passed to or returned from ext functions on '{target.TargetTriple}' yet");
		
		var size = targetData.ABISizeOfType(type);
		return size is 1 or 2 or 4 or 8
			? new(CPassKind.Coerced, type.Context.GetIntType((uint)size * 8))
			: new(CPassKind.Indirect, type);
	}
	
	private struct Registers(int integer, int sse)
	{
		public int Integer = integer;
		public int Sse = sse;
	}
	
	private enum EightbyteClass
	{
		None,
		Integer,
		Sse
	}
	
	private struct Eightbyte
	{
		public EightbyteClass Class;
		public bool HasDouble;
		public bool HasLowFloat;
		public bool HasHighFloat;
	}
	
	private CPass ClassifySystemV(LLVMTypeRef type, bool isReturn, ref Registers registers)
	{
		if (type.Kind is not (LLVMTypeKind.LLVMStructTypeKind or LLVMTypeKind.LLVMArrayTypeKind))
		{
			if (!isReturn)
				Consume(type, ref registers);
			
			return new(CPassKind.Direct, type);
		}
		
		var eightbytes = FindEightbytes(type);
		if (eightbytes is null)
			return new(CPassKind.Indirect, type) { IsByValue = !isReturn };
		
		var integers = eightbytes.Count(static eightbyte => eightbyte.Class != EightbyteClass.Sse);
		var sses = eightbytes.Length - integers;
		if (!isReturn)
		{
			if (integers > registers.Integer || sses > registers.Sse)
				return new(CPassKind.Indirect, type) { IsByValue = true };
			
			registers.Integer -= integers;
			registers.Sse -= sses;
		}
		
		var size = targetData.ABISizeOfType(type);
		var parts = eightbytes
			.Select((eightbyte, i) => GetEightbyteType(type.Context, eightbyte,
				i == eightbytes.Length - 1 ? (uint)(size - (ulong)i * 8) : 8))
			.ToArray();
		
		return new(CPassKind.Coerced, parts is [var single] ? single : type.Context.GetStructType(parts, false));
	}
	
	private static void Consume(LLVMTypeRef type, ref Registers registers)
	{
		switch (type.Kind)
		{
			case LLVMTypeKind.LLVMFloatTypeKind or LLVMTypeKind.LLVMDoubleTypeKind:
				registers.Sse--;
				break;
			
			case LLVMTypeKind.LLVMIntegerTypeKind when type.IntWidth == 128:
				registers.Integer -= 2;
				break;
			
			case LLVMTypeKind.LLVMVoidTypeKind:
				break;
			
			default:
				registers.Integer--;
				break;
		}
	}
	
	private Eightbyte[]? FindEightbytes(LLVMTypeRef type)
	{
		var size = targetData.ABISizeOfType(type);
		if (size is 0 or > 16)
			return null;
		
		var eightbytes = new Eightbyte[(size + 7) / 8];
		return Walk(type, 0, eightbytes) ? eightbytes : null;
	}
	
	private bool Walk(LLVMTypeRef type, ulong offset, Eightbyte[] eightbytes)
	{
		if (offset % targetData.ABIAlignmentOfType(type) != 0)
			return false;
		
		switch (type.Kind)
		{
			case LLVMTypeKind.LLVMStructTypeKind:
			{
				for (var i = 0u; i < type.StructElementTypesCount; i++)
				{
					if (!Walk(type.StructGetTypeAtIndex(i), offset + targetData.OffsetOfElement(type, i), eightbytes))
						return false;
				}
				
				return true;
			}
			
			case LLVMTypeKind.LLVMArrayTypeKind:
			{
				var element = type.ElementType;
				var elementSize = targetData.ABISizeOfType(element);
				for (var i = 0ul; i < type.ArrayLength; i++)
				{
					if (!Walk(element, offset + i * elementSize, eightbytes))
						return false;
				}
				
				return true;
			}
			
			case LLVMTypeKind.LLVMFloatTypeKind:
			{
				ref var eightbyte = ref eightbytes[offset / 8];
				Merge(ref eightbyte, EightbyteClass.Sse);
				if (offset % 8 == 0)
					eightbyte.HasLowFloat = true;
				else
					eightbyte.HasHighFloat = true;
				
				return true;
			}
			
			case LLVMTypeKind.LLVMDoubleTypeKind:
			{
				ref var eightbyte = ref eightbytes[offset / 8];
				Merge(ref eightbyte, EightbyteClass.Sse);
				eightbyte.HasDouble = true;
				return true;
			}
			
			default:
			{
				var end = offset + Math.Max(targetData.ABISizeOfType(type), 1);
				for (var position = offset; position < end; position += 8)
					Merge(ref eightbytes[position / 8], EightbyteClass.Integer);
				
				return true;
			}
		}
	}
	
	private static void Merge(ref Eightbyte eightbyte, EightbyteClass merged) =>
		eightbyte.Class = eightbyte.Class == EightbyteClass.Integer || merged == EightbyteClass.Integer
			? EightbyteClass.Integer
			: merged;
	
	private static LLVMTypeRef GetEightbyteType(LLVMContextRef context, Eightbyte eightbyte, uint bytes)
	{
		if (eightbyte.Class is EightbyteClass.Integer or EightbyteClass.None)
			return context.GetIntType(bytes * 8);
		
		if (eightbyte.HasDouble)
			return context.DoubleType;
		
		return eightbyte is { HasLowFloat: true, HasHighFloat: false }
			? context.FloatType
			: LLVMTypeRef.CreateVector(context.FloatType, 2);
	}
}