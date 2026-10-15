using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private LLVMValueRef EmitAtomic(AtomicValue v, LLVMBuilderRef builder)
	{
		var ordering = MapOrdering(v.Ordering);
		if (v.Pointer is not { } pointer)
			return builder.BuildFence(ordering, false);
		
		var valueType = ((PointerType)Substitute(pointer.Type)).BaseType;
		var atomicType = GetAtomicType(valueType);
		var address = EmitValue(pointer, builder);
		switch (v.Access)
		{
			case AtomicAccess.Load:
			{
				var load = builder.BuildLoad2(atomicType, address, "atomic");
				load.Alignment = (uint)_targetData.ABISizeOfType(atomicType);
				LLVM.SetOrdering(load, ordering);
				return FromAtomic(load, valueType, builder);
			}
			
			case AtomicAccess.Store:
			{
				var value = EmitValue(v.Operand!, builder);
				var store = builder.BuildStore(ToAtomic(value, valueType, builder), address);
				store.Alignment = (uint)_targetData.ABISizeOfType(atomicType);
				LLVM.SetOrdering(store, ordering);
				return value;
			}
			
			case AtomicAccess.CompareSwap:
			{
				var expected = ToComparable(ToAtomic(EmitValue(v.Expected!, builder), valueType, builder), builder);
				var desired = ToComparable(ToAtomic(EmitValue(v.Operand!, builder), valueType, builder), builder);
				LLVMValueRef exchange = LLVM.BuildAtomicCmpXchg(builder, address, expected, desired, ordering,
					MapFailureOrdering(v.Ordering), 0);
				
				return builder.BuildExtractValue(exchange, 1, "swapped");
			}
		}
		
		var operand = ToAtomic(EmitValue(v.Operand!, builder), valueType, builder);
		var isFloat = valueType is FloatType;
		var operation = v.Operation switch
		{
			BinaryOperation.Addition when isFloat => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpFAdd,
			BinaryOperation.Addition => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd,
			BinaryOperation.Subtraction when isFloat => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpFSub,
			BinaryOperation.Subtraction => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpSub,
			BinaryOperation.BitwiseAnd => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAnd,
			BinaryOperation.BitwiseOr => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpOr,
			_ => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpXor
		};
		
		var old = builder.BuildAtomicRMW(operation, address, operand, ordering, false);
		var result = v.Operation switch
		{
			BinaryOperation.Addition when isFloat => builder.BuildFAdd(old, operand, "new"),
			BinaryOperation.Addition => builder.BuildAdd(old, operand, "new"),
			BinaryOperation.Subtraction when isFloat => builder.BuildFSub(old, operand, "new"),
			BinaryOperation.Subtraction => builder.BuildSub(old, operand, "new"),
			BinaryOperation.BitwiseAnd => builder.BuildAnd(old, operand, "new"),
			BinaryOperation.BitwiseOr => builder.BuildOr(old, operand, "new"),
			_ => builder.BuildXor(old, operand, "new")
		};
		
		return FromAtomic(result, valueType, builder);
	}
	
	private LLVMTypeRef GetAtomicType(TypeSymbol type) => type switch
	{
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => _context.Int8Type,
		EnumSymbol { IsMatch: true } enumType => GetAtomicType(_typePool.GetMatchedType(enumType)),
		EnumSymbol enumType => MapTypeSymbol(_typePool.GetTagType(enumType)),
		_ => MapTypeSymbol(type)
	};
	
	private LLVMValueRef ToAtomic(LLVMValueRef value, TypeSymbol type, LLVMBuilderRef builder) => type switch
	{
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => builder.BuildZExt(value, _context.Int8Type, "atomic"),
		EnumSymbol { IsMatch: true } enumType => ToAtomic(value, _typePool.GetMatchedType(enumType), builder),
		EnumSymbol enumType => ExtractField(builder, enumType, value, 0, _typePool.GetTagType(enumType), "tag"),
		_ => value
	};
	
	private LLVMValueRef FromAtomic(LLVMValueRef value, TypeSymbol type, LLVMBuilderRef builder) => type switch
	{
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => builder.BuildTrunc(value, _context.Int1Type, "value"),
		EnumSymbol { IsMatch: true } enumType => FromAtomic(value, _typePool.GetMatchedType(enumType), builder),
		EnumSymbol enumType => EmitTagged(enumType, value, builder),
		_ => value
	};
	
	private LLVMValueRef ToComparable(LLVMValueRef value, LLVMBuilderRef builder) => value.TypeOf.Kind switch
	{
		LLVMTypeKind.LLVMFloatTypeKind => builder.BuildBitCast(value, _context.Int32Type, "bits"),
		LLVMTypeKind.LLVMDoubleTypeKind => builder.BuildBitCast(value, _context.Int64Type, "bits"),
		_ => value
	};
	
	private static LLVMAtomicOrdering MapOrdering(AtomicOrdering ordering) => ordering switch
	{
		AtomicOrdering.Relaxed => LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
		AtomicOrdering.Acquire => LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
		AtomicOrdering.Release => LLVMAtomicOrdering.LLVMAtomicOrderingRelease,
		AtomicOrdering.AcquireRelease => LLVMAtomicOrdering.LLVMAtomicOrderingAcquireRelease,
		_ => LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent
	};
	
	private static LLVMAtomicOrdering MapFailureOrdering(AtomicOrdering ordering) => ordering switch
	{
		AtomicOrdering.Relaxed or AtomicOrdering.Release => LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
		AtomicOrdering.Acquire or AtomicOrdering.AcquireRelease => LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
		_ => LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent
	};
}
