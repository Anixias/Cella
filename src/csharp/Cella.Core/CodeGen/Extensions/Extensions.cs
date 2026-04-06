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
}