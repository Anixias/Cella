using LLVMSharp.Interop;

namespace Cella.Core.CodeGen.Extensions;

public static unsafe class Extensions
{
	extension(LLVMTargetDataRef targetData)
	{
		public uint PointerSize() => LLVM.PointerSize((LLVMOpaqueTargetData*)targetData.Handle);
		
		public void Dispose()
		{
			LLVM.DisposeTargetData((LLVMOpaqueTargetData*)targetData.Handle);
		}
	}
	
	extension(LLVMTargetMachineRef targetMachine)
	{
		public void Dispose()
		{
			LLVM.DisposeTargetMachine((LLVMOpaqueTargetMachine*)targetMachine.Handle);
		}
	}
	
	extension(LLVMTypeRef type)
	{
		public static LLVMTypeRef Int128 => LLVM.Int128Type();
	}
	
	extension(LLVMBuilderRef builder)
	{
		public LLVMValueRef BuildMemCpy(LLVMValueRef destination, uint destinationAlignment, LLVMValueRef source,
			uint sourceAlignment, LLVMValueRef size) =>
			LLVM.BuildMemCpy((LLVMOpaqueBuilder*)builder.Handle, (LLVMOpaqueValue*)destination.Handle,
				destinationAlignment, (LLVMOpaqueValue*)source.Handle, sourceAlignment, (LLVMOpaqueValue*)size.Handle);
		
		public LLVMValueRef BuildMemSet(LLVMValueRef pointer, LLVMValueRef value, LLVMValueRef length,
			uint alignment) =>
			LLVM.BuildMemSet((LLVMOpaqueBuilder*)builder.Handle, (LLVMOpaqueValue*)pointer.Handle,
				(LLVMOpaqueValue*)value.Handle, (LLVMOpaqueValue*)length.Handle, alignment);
	}
}