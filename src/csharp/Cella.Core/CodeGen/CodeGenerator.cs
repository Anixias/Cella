using Cella.Core.Binding;
using Cella.Core.CodeGen.Extensions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

internal readonly record struct LLVMFunctionInfo
(
	LLVMValueRef FunctionValue,
	LLVMTypeRef FunctionType,
	LLVMTypeRef ReturnType
);

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
	private readonly Dictionary<FunctionInfo, LLVMFunctionInfo> _funMap = [];
	private readonly Dictionary<VariableInfo, LLVMValueRef> _varMap = [];
	private readonly LLVMValueRef _true = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1uL);
	private readonly LLVMValueRef _false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0uL);
	
	public CodeGenerator(CodeGenConfig config)
	{
		Init();
		_config = config;
		(_dataLayoutStr, TargetTriple, _targetMachine) = GetDataLayout(config.TargetConfig);
		MapNativeSymbols();
	}
	
	private void MapNativeSymbols()
	{
		_typeMap[NativeSymbols.Void] = LLVMTypeRef.Void;
		_typeMap[NativeSymbols.Int32] = LLVMTypeRef.Int32;
		_typeMap[NativeSymbols.Int64] = LLVMTypeRef.Int64;
		_typeMap[NativeSymbols.Int128] = LLVMTypeRef.Int128;
		_typeMap[NativeSymbols.Bool] = LLVMTypeRef.Int1;
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
		
		// Create and map imported functions
		foreach (var function in module.ImportedFunctions)
		{
			var info = CreateFunction(llvmModule, function);
			var llvmFunction = info.FunctionValue;
			llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			llvmFunction.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		
		// Create and map functions
		foreach (var function in module.Functions)
		{
			var info = CreateFunction(llvmModule, function.Info);
			var llvmFunction = info.FunctionValue;
			
			if (function.Info.Symbol.Visibility != Visibility.Public)
				continue;
			
			llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
			llvmFunction.Linkage = LLVMLinkage.LLVMDLLExportLinkage;
		}
		
		// Build function bodies
		foreach (var function in module.Functions)
			BuildFunction(llvmModule, llvmDiBuilder, function);
	}
	
	private LLVMFunctionInfo CreateFunction(LLVMModuleRef llvmModule, FunctionInfo function)
	{
		var signature = function.Signature;
		var symbol = function.Symbol;
		var returnType = MapTypeSymbol(signature.ReturnType);
		
		var paramTypes = signature.ParameterTypes;
		var paramLlvmTypes = new LLVMTypeRef[paramTypes.Length];
		for (var i = 0; i < paramTypes.Length; i++)
			paramLlvmTypes[i] = MapTypeSymbol(paramTypes[i]);
		
		// TODO Variadic
		var functionType = LLVMTypeRef.CreateFunction(returnType, paramLlvmTypes);
		var functionValue = llvmModule.AddFunction(function.MangledName ?? symbol.Name, functionType);
		var functionInfo = new LLVMFunctionInfo(functionValue, functionType, returnType);
		
		_funMap.Add(function, functionInfo);
		return functionInfo;
	}
	
	private void BuildFunction(LLVMModuleRef llvmModule, LLVMDIBuilderRef llvmDiBuilder, LoweredFunction function)
	{
		var functionValue = _funMap[function.Info].FunctionValue;
		
		// Create blocks
		var blockMap = new Dictionary<BasicBlock, LLVMBasicBlockRef>();
		foreach (var block in function.Blocks)
			blockMap.Add(block, functionValue.AppendBasicBlock(block.Label));
		
		// Build blocks
		using var builder = llvmModule.Context.CreateBuilder();
		for (var i = 0; i < function.Blocks.Count; i++)
		{
			var block = function.Blocks[i];
			var llvmBlock = blockMap[block];
			builder.PositionAtEnd(llvmBlock);
			
			// First block allocates stack variables for parameters
			if (i == 0)
			{
				var parameters = function.Info.Symbol.Parameters;
				for (var p = 0; p < parameters.Length; p++)
				{
					var paramSymbol = parameters[p];
					var paramType = function.Info.Signature.ParameterTypes[p];
					var paramInfo = new VariableInfo(paramSymbol, paramType);
					
					var paramLlvmValue = functionValue.GetParam((uint)p);
					var paramLlvmType = MapTypeSymbol(paramType);
					var paramPtr = builder.BuildAlloca(paramLlvmType, paramSymbol.Name);
					builder.BuildStore(paramLlvmValue, paramPtr);
					_varMap[paramInfo] = paramPtr;
				}
			}
			
			foreach (var instruction in block.Instructions)
				EmitInstruction(builder, instruction, blockMap);
			
			EmitTerminator(builder, block.Terminator, blockMap);
		}
		
		functionValue.VerifyFunction(LLVMVerifierFailureAction.LLVMAbortProcessAction);
	}
	
	private void EmitInstruction(LLVMBuilderRef builder, IInstruction instruction,
		Dictionary<BasicBlock, LLVMBasicBlockRef> blockMap)
	{
		switch (instruction)
		{
			case LocalVarInstruction i:
			{
				var type = MapTypeSymbol(i.Symbol.Type);
				var ptr = builder.BuildAlloca(type, i.Symbol.Name);
				_varMap[new(i.Symbol, i.Symbol.Type)] = ptr;
				
				if (i.Initializer is { } initializer)
					builder.BuildStore(EmitValue(initializer, builder), ptr);
				
				break;
			}
			
			case ExpressionInstruction i:
			{
				EmitValue(i.Value, builder);
				break;
			}
		}
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
					builder.BuildRet(EmitValue(value, builder));
				
				break;
			
			case BranchTerminator term:
				builder.BuildBr(blockMap[term.Target]);
				break;
			
			case ConditionalBranchTerminator term:
				var condition = EmitValue(term.Condition, builder);
				builder.BuildCondBr(condition, blockMap[term.TrueTarget], blockMap[term.FalseTarget]);
				break;
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitValue(Value value, LLVMBuilderRef builder) => value switch
	{
		ConstantValue v => EmitConstant(v),
		VariableValue v => builder.BuildLoad2(MapTypeSymbol(v.Type), _varMap[v.Variable], v.Variable.Symbol.Name),
		AddValue { IsConstant: true } v => LLVMValueRef.CreateConstAdd(EmitValue(v.Left, builder), EmitValue(v.Right, builder)),
		AddValue v => builder.BuildAdd(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		SubValue { IsConstant: true } v => LLVMValueRef.CreateConstSub(EmitValue(v.Left, builder), EmitValue(v.Right, builder)),
		SubValue v => builder.BuildSub(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		MulValue { IsConstant: true } v => LLVMValueRef.CreateConstMul(EmitValue(v.Left, builder), EmitValue(v.Right, builder)),
		MulValue v => builder.BuildMul(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		DivValue v => builder.BuildSDiv(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		AssignValue v => EmitAssignValue(v, builder),
		NegValue { IsConstant: true } v => LLVMValueRef.CreateConstNeg(EmitValue(v.Operand, builder)),
		NegValue v => builder.BuildNeg(EmitValue(v.Operand, builder)), // TODO Check floating point?
		CallValue v when _funMap[v.Function] is var (fv, ft, _) => builder.BuildCall2(ft, fv,
			v.Arguments.Select(a => EmitValue(a, builder)).ToArray()),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitAssignValue(AssignValue value, LLVMBuilderRef builder)
	{
		var right = EmitValue(value.Right, builder);
		builder.BuildStore(right, EmitAddress(value.Left, builder));
		return right;
	}
	
	private LLVMValueRef EmitAddress(Value value, LLVMBuilderRef builder) => value switch
	{
		VariableValue v => _varMap[v.Variable],
		_ => throw new InvalidOperationException()
	};
	
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
				
				case PrimitiveTypeKind.Bool:
					return (bool)value ? _true : _false;
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
	string? Cpu = null,
	string? Features = null
);