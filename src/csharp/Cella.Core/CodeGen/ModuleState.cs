using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

internal sealed class ModuleState(LoweredModule lowered, LLVMModuleRef module, LLVMDIBuilderRef diBuilder)
{
	public LoweredModule Lowered { get; } = lowered;
	public LLVMModuleRef Module { get; } = module;
	public LLVMDIBuilderRef DiBuilder { get; } = diBuilder;
	public Dictionary<TypeSymbol, LLVMTypeRef> Types { get; } = [];
	public Dictionary<FunctionInfo, LLVMFunctionInfo> Functions { get; } = [];
	public Dictionary<TypeSymbol, LLVMValueRef> DropGlue { get; } = [];
	public Dictionary<VariableInfo, LLVMValueRef> Variables { get; } = [];
	public Dictionary<GlobalSymbol, LLVMValueRef> Globals { get; } = [];
	public Dictionary<byte[], LLVMValueRef> Strings { get; } = new(ByteArrayComparer.Instance);
	public HashSet<string> ExternalLibraries { get; } = [];
	public LLVMValueRef PanicFunction { get; set; }
	public LLVMFunctionInfo? EntryPoint { get; set; }
}