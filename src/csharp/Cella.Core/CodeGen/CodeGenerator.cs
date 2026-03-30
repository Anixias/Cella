using Cella.Core.CodeGen.Extensions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

// Create type definitions/forward declarations
// Then, create function definitions/forward declarations
public sealed unsafe class CodeGenerator : IDisposable
{
	private static bool isInitialized;
	
	private static void Init()
	{
		if (isInitialized)
			return;
		
		isInitialized = true;
		LLVM.LinkInMCJIT();
		LLVM.InitializeAllTargetInfos();
		LLVM.InitializeAllTargets();
		LLVM.InitializeAllTargetMCs();
		LLVM.InitializeAllAsmParsers();
		LLVM.InitializeAllAsmPrinters();
	}
	
	public string TargetTriple { get; }
	
	private readonly CodeGenConfig _config;
	private readonly string _dataLayoutStr;
	private readonly LLVMTargetMachineRef _targetMachine;
	private readonly Dictionary<TypeSymbol, LLVMTypeRef> _typeMap = [];
	
	public CodeGenerator(CodeGenConfig config)
	{
		Init();
		_config = config;
		(_dataLayoutStr, TargetTriple, _targetMachine) = GetDataLayout(config.TargetConfig);
		MapNativeSymbols();
	}
	
	private void MapNativeSymbols()
	{
		_typeMap[NativeSymbols.Int32] = LLVMTypeRef.Int32;
		_typeMap[NativeSymbols.Int64] = LLVMTypeRef.Int64;
		_typeMap[NativeSymbols.Int128] = LLVMTypeRef.Int128;
	}
	
	public string? Generate(LoweredModule module)
	{
		using var llvmModule = LLVMModuleRef.CreateWithName(module.Symbol.Name);
		var llvmDiBuilder = llvmModule.CreateDIBuilder();
		try
		{
			string message;
			
			// Build code
			BuildModule(llvmModule, llvmDiBuilder, module);
			
			if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMAbortProcessAction, out message))
			{
				// TEMP
				Console.WriteLine(message);
				return null;
			}
			
			llvmDiBuilder.DIBuilderFinalize();
			
			llvmModule.Target = TargetTriple;
			llvmModule.DataLayout = _dataLayoutStr;
			
			if (!Directory.Exists(_config.OutputConfig.Directory))
				Directory.CreateDirectory(_config.OutputConfig.Directory);
			
			if (_config.OutputConfig.EmitIR)
			{
				var irFilePath = Path.Combine(_config.OutputConfig.Directory, $"{module.Symbol.Name}.ll");
				if (!llvmModule.TryPrintToFile(irFilePath, out message))
				{
					// TEMP
					Console.WriteLine(message);
					return null;
				}
			}
			
			if (_config.OutputConfig.EmitAssembly)
			{
				var assemblyFilePath = Path.Combine(_config.OutputConfig.Directory, $"{module.Symbol.Name}.s");
				if (!_targetMachine.TryEmitToFile(llvmModule, assemblyFilePath, LLVMCodeGenFileType.LLVMAssemblyFile,
					    out message))
				{
					// TEMP
					Console.WriteLine(message);
					return null;
				}
			}
			
			var objectFilePath = Path.Combine(_config.OutputConfig.Directory, $"{module.Symbol.Name}.o");
			if (_targetMachine.TryEmitToFile(llvmModule, objectFilePath, LLVMCodeGenFileType.LLVMObjectFile,
				    out message))
				return objectFilePath;
			
			// TEMP
			Console.WriteLine(message);
			return null;
			
		}
		finally
		{
			LLVM.DisposeDIBuilder((LLVMOpaqueDIBuilder*)llvmDiBuilder.Handle);
		}
	}
	
	private LLVMTypeRef MapTypeSymbol(TypeSymbol? symbol) => symbol is null
		? LLVMTypeRef.Void
		: _typeMap.GetValueOrDefault(symbol, LLVMTypeRef.Void);
	
	private void BuildModule(LLVMModuleRef llvmModule, LLVMDIBuilderRef llvmDiBuilder, LoweredModule module)
	{
		// TODO Build types
		
		foreach (var function in module.Functions)
			BuildFunction(llvmModule, llvmDiBuilder, function);
	}
	
	private void BuildFunction(LLVMModuleRef llvmModule, LLVMDIBuilderRef llvmDiBuilder, LoweredFunction function)
	{
		var returnType = MapTypeSymbol(function.Symbol.ReturnType);
		
		var parameterTypes = new LLVMTypeRef[function.Symbol.Parameters.Length];
		for (var i = 0; i < function.Symbol.Parameters.Length; i++)
			parameterTypes[i] = MapTypeSymbol(function.Symbol.Parameters[i].Type);
		
		// TODO Variadic
		var functionType = LLVMTypeRef.CreateFunction(returnType, parameterTypes);
		var functionValue = llvmModule.AddFunction(function.Symbol.MangledName ?? function.Symbol.Name, functionType);
		
		var blockMap = new Dictionary<BasicBlock, LLVMBasicBlockRef>();
		foreach (var block in function.Blocks)
			blockMap.Add(block, functionValue.AppendBasicBlock(block.Label));
		
		using var builder = llvmModule.Context.CreateBuilder();
		foreach (var block in function.Blocks)
		{
			var llvmBlock = blockMap[block];
			builder.PositionAtEnd(llvmBlock);
			
			// TODO Emit instructions
			
			EmitTerminator(builder, block.Terminator, blockMap);
		}
		
		functionValue.VerifyFunction(LLVMVerifierFailureAction.LLVMAbortProcessAction);
	}
	
	private void EmitTerminator(LLVMBuilderRef builder, IBlockTerminator terminator, 
		Dictionary<BasicBlock, LLVMBasicBlockRef> blockMap)
	{
		switch (terminator)
		{
			case ReturnTerminator term:
				if (term.Value is not { } value)
					builder.BuildRetVoid();
				else
					builder.BuildRet(EmitValue(value));
				
				break;
			
			case BranchTerminator term:
				builder.BuildBr(blockMap[term.Target]);
				break;
			
			case ConditionalBranchTerminator term:
				var condition = EmitValue(term.Condition);
				builder.BuildCondBr(condition, blockMap[term.TrueTarget], blockMap[term.FalseTarget]);
				break;
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitValue(Value value)
	{
		switch (value)
		{
			case ConstantValue v:
				return EmitConstant(v);
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitConstant(ConstantValue constant)
	{
		var type = MapTypeSymbol(constant.Type);
		
		if (constant.Value is not { } value)
			return LLVMValueRef.CreateConstNull(type);
		
		if (constant.Type is PrimitiveType primitiveType)
		{
			switch (primitiveType.Kind)
			{
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
			}
		}
		
		throw new InvalidOperationException();
	}
	
	private static (string DataLayout, string TargetTriple, LLVMTargetMachineRef TargetMachine)
		GetDataLayout(TargetConfig? target)
	{
		var triple = target?.TargetTriple ?? LLVMTargetRef.DefaultTriple;
		
		var targetRef = LLVMTargetRef.GetTargetFromTriple(triple);
		var cpu = string.IsNullOrWhiteSpace(target?.Cpu) ? "generic" : target.Cpu;
		var features = target?.Features ?? "";
		
		var targetMachine = targetRef.CreateTargetMachine(
			triple,
			cpu,
			features,
			LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
			LLVMRelocMode.LLVMRelocPIC,
			LLVMCodeModel.LLVMCodeModelDefault);
		
		var dataLayout = targetMachine.CreateTargetDataLayout();
		sbyte* dataLayoutStr = null;
		try
		{
			dataLayoutStr = LLVM.CopyStringRepOfTargetData((LLVMOpaqueTargetData*)dataLayout.Handle);
			return (SpanExtensions.AsString(dataLayoutStr), triple, targetMachine);
		}
		finally
		{
			if (dataLayoutStr is not null)
				LLVM.DisposeMessage(dataLayoutStr);
			
			dataLayout.Dispose();
		}
	}
	
	public void Dispose()
	{
		_targetMachine.Dispose();
	}
}

public sealed record CodeGenConfig
(
	OutputConfig OutputConfig,
	TargetConfig? TargetConfig // if null, compiles for current platform
);

public sealed record OutputConfig
(
	string Directory,
	bool EmitIR = false,
	bool EmitAssembly = false
);

public sealed record TargetConfig
(
	string TargetTriple,
	string? Cpu,
	string? Features
);