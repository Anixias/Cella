using LLVMSharp.Interop;

namespace Cella.Core.CodeGen.Extensions;

public static class Extensions
{
	extension(LLVMTargetDataRef targetData)
	{
		public unsafe void Dispose()
		{
			LLVM.DisposeTargetData((LLVMOpaqueTargetData*)targetData.Handle);
		}
	}
	
	extension(LLVMTargetMachineRef targetMachine)
	{
		public unsafe void Dispose()
		{
			LLVM.DisposeTargetMachine((LLVMOpaqueTargetMachine*)targetMachine.Handle);
		}
	}
	
	extension(LLVMTypeRef type)
	{
		public static unsafe LLVMTypeRef Int128 => LLVM.Int128Type();
	}
}