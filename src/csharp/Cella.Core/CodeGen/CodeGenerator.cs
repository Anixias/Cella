using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cella.Core.Binding;
using Cella.Core.Binding.Operations;
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
	
	private readonly AssemblySymbol _assemblySymbol;
	private readonly TypeMemberTable _typeMemberTable;
	private readonly CodeGenConfig _config;
	private readonly string _dataLayoutStr;
	private readonly LLVMTargetMachineRef _targetMachine;
	private readonly uint _pointerSize;
	private readonly Dictionary<TypeSymbol, LLVMTypeRef> _typeMap = [];
	private readonly Dictionary<FunctionInfo, LLVMFunctionInfo> _funMap = [];
	private readonly Dictionary<VariableInfo, LLVMValueRef> _varMap = [];
	private readonly LLVMValueRef _true = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1uL);
	private readonly LLVMValueRef _false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0uL);
	private readonly HashSet<string> _externalLibraries = [];
	private readonly Dictionary<byte[], LLVMValueRef> _stringPool = new(ByteArrayComparer.Instance);
	private LLVMModuleRef currentModule;
	
	public CodeGenerator(AssemblySymbol assemblySymbol, TypeMemberTable typeMemberTable, CodeGenConfig config)
	{
		Init();
		_assemblySymbol = assemblySymbol;
		_typeMemberTable = typeMemberTable;
		_config = config;
		(_dataLayoutStr, TargetTriple, _targetMachine, _pointerSize) = GetDataLayout(config.TargetConfig);
		MapNativeSymbols();
	}
	
	private void MapNativeSymbols()
	{
		// TODO Map address spaces based on target?
		
		var intSize = LLVMTypeRef.CreateInt(_pointerSize * 8);
		_typeMap[NativeSymbols.Void] = LLVMTypeRef.Void;
		_typeMap[NativeSymbols.Int8] = LLVMTypeRef.Int8;
		_typeMap[NativeSymbols.Int16] = LLVMTypeRef.Int16;
		_typeMap[NativeSymbols.Int32] = LLVMTypeRef.Int32;
		_typeMap[NativeSymbols.Int64] = LLVMTypeRef.Int64;
		_typeMap[NativeSymbols.Int128] = LLVMTypeRef.Int128;
		_typeMap[NativeSymbols.IntSize] = intSize;
		_typeMap[NativeSymbols.UInt8] = LLVMTypeRef.Int8;
		_typeMap[NativeSymbols.UInt16] = LLVMTypeRef.Int16;
		_typeMap[NativeSymbols.UInt32] = LLVMTypeRef.Int32;
		_typeMap[NativeSymbols.UInt64] = LLVMTypeRef.Int64;
		_typeMap[NativeSymbols.UInt128] = LLVMTypeRef.Int128;
		_typeMap[NativeSymbols.UIntSize] = intSize;
		_typeMap[NativeSymbols.Bool] = LLVMTypeRef.Int1;
		_typeMap[NativeSymbols.Str] =
			LLVMTypeRef.CreateStruct([intSize, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u)], false);
		
		_typeMap[NativeSymbols.CStr] = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u);
	}
	
	public CodeGenResult Generate(LoweredModule module)
	{
		using var llvmModule = LLVMModuleRef.CreateWithName(module.Symbol.Name);
		currentModule = llvmModule;
		var llvmDiBuilder = llvmModule.CreateDIBuilder();
		try
		{
			string message;
			
			// Build code
			BuildModule(llvmModule, llvmDiBuilder, module);
			
			if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMAbortProcessAction, out message))
				return CodeGenResult.Failure with { ErrorMessage = message };
			
			llvmDiBuilder.DIBuilderFinalize();
			
			llvmModule.Target = TargetTriple;
			llvmModule.DataLayout = _dataLayoutStr;
			
			if (!Directory.Exists(_config.OutputConfig.Directory))
				Directory.CreateDirectory(_config.OutputConfig.Directory);
			
			if (_config.OutputConfig.EmitIR)
			{
				var irFilePath = Path.Combine(_config.OutputConfig.Directory, $"{module.Symbol.Name}.ll");
				if (!llvmModule.TryPrintToFile(irFilePath, out message))
					return CodeGenResult.Failure with { ErrorMessage = message };
			}
			
			if (_config.OutputConfig.EmitAssembly)
			{
				var assemblyFilePath = Path.Combine(_config.OutputConfig.Directory, $"{module.Symbol.Name}.s");
				if (!_targetMachine.TryEmitToFile(llvmModule, assemblyFilePath, LLVMCodeGenFileType.LLVMAssemblyFile,
					    out message))
					return CodeGenResult.Failure with { ErrorMessage = message };
			}
			
			var objectFilePath = Path.Combine(_config.OutputConfig.Directory, $"{module.Symbol.Name}.o");
			if (_targetMachine.TryEmitToFile(llvmModule, objectFilePath, LLVMCodeGenFileType.LLVMObjectFile,
				    out message))
				return new(true, objectFilePath, null, _externalLibraries);
			
			return CodeGenResult.Failure with { ErrorMessage = message };
			
		}
		finally
		{
			_externalLibraries.Clear();
			_stringPool.Clear();
			LLVM.DisposeDIBuilder((LLVMOpaqueDIBuilder*)llvmDiBuilder.Handle);
			currentModule = default;
		}
	}
	
	private LLVMTypeRef MapTypeSymbol(TypeSymbol? symbol)
	{
		if (symbol is null)
			return LLVMTypeRef.Void;
		
		if (_typeMap.TryGetValue(symbol, out var type))
			return type;
		
		// Lazy mapping for generic types
		if (symbol is ArrayType arrayType)
		{
			var elementType = MapTypeSymbol(arrayType.ElementType);
			var usizeType = LLVMTypeRef.CreateInt(_pointerSize * 8);
			var ptrType = LLVMTypeRef.CreatePointer(elementType, 0u);
			return LLVMTypeRef.CreateStruct([usizeType, ptrType], false);
		}
		
		throw new InvalidOperationException($"Symbol '{symbol.Name}' not mapped in LLVM");
	}
	
	private void BuildModule(LLVMModuleRef llvmModule, LLVMDIBuilderRef llvmDiBuilder, LoweredModule module)
	{
		// TODO Build types
		
		// Create and map external functions
		foreach (var function in module.ExternalFunctions)
		{
			if (function.Origin is { } origin)
				_externalLibraries.Add(origin);
			
			var info = CreateFunction(llvmModule, function);
			var llvmFunction = info.FunctionValue;
			llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
			
			// TODO On Windows, check for DLL Import metadata/annotation/attribute
			// llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			// llvmFunction.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		
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
			
			if (function.Info.Symbol.Visibility == Visibility.Public)
			{
				// TODO Use ExternalLinkage if not building a DLL?
				llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
				llvmFunction.Linkage = LLVMLinkage.LLVMDLLExportLinkage;
			}
			else
			{
				if (_assemblySymbol.EntryPoint is { } entryPoint && entryPoint.Symbol == function.Info.Symbol)
					llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
				else
					llvmFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
			}
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
			
			// This happens with an empty function body
			case UndefinedTerminator:
				builder.BuildRetVoid();
				break;
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitValue(Value value, LLVMBuilderRef builder) => value switch
	{
		ConstantValue v => EmitConstant(v),
		VariableValue v => builder.BuildLoad2(MapTypeSymbol(v.Type), _varMap[v.Variable], v.Variable.Symbol.Name),
		BinOpValue v => EmitBinaryOp(v, builder),
		UnaryOpValue v => EmitUnaryOp(v, builder),
		AssignValue v => EmitAssignValue(v, builder),
		IndexerValue v => EmitIndexer(v, builder),
		AccessValue v => EmitAccessValue(v, builder),
		CallValue v when _funMap[v.Function] is var (fv, ft, _) => builder.BuildCall2(ft, fv,
			v.Arguments.Select(a => EmitValue(a, builder)).ToArray()),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitBinaryOp(BinOpValue v, LLVMBuilderRef builder) => v switch
	{
		{ IsConstant: true, Op: BinaryOperation.Addition } =>
			LLVMValueRef.CreateConstAdd(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		{ Op: BinaryOperation.Addition } =>
			builder.BuildAdd(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		
		{ IsConstant: true, Op: BinaryOperation.Subtraction } =>
			LLVMValueRef.CreateConstSub(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		{ Op: BinaryOperation.Subtraction } =>
			builder.BuildSub(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		
		{ IsConstant: true, Op: BinaryOperation.Multiplication } =>
			LLVMValueRef.CreateConstMul(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		{ Op: BinaryOperation.Multiplication } =>
			builder.BuildMul(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check floating point?
		
		{ Op: BinaryOperation.Division } =>
			builder.BuildSDiv(EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.Greater } =>
			builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.GreaterEqual } =>
			builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.Less } =>
			builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.LessEqual } =>
			builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.Equal } =>
			builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.NotEqual } =>
			builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, EmitValue(v.Left, builder), EmitValue(v.Right, builder)), // TODO Check type for correct operation
		
		{ Op: BinaryOperation.And } =>
			builder.BuildAnd(EmitValue(v.Left, builder), EmitValue(v.Right, builder)),
		
		{ Op: BinaryOperation.Or } =>
			builder.BuildOr(EmitValue(v.Left, builder), EmitValue(v.Right, builder)),
		
		{ Op: BinaryOperation.Xor } =>
			builder.BuildXor(EmitValue(v.Left, builder), EmitValue(v.Right, builder)),
		
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitUnaryOp(UnaryOpValue v, LLVMBuilderRef builder) => v switch
	{
		{ IsConstant: true, Op: UnaryOperation.Negation } => LLVMValueRef.CreateConstNeg(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.Negation } => builder.BuildNeg(EmitValue(v.Operand, builder)), // TODO Check floating point?
		
		{ IsConstant: true, Op: UnaryOperation.Not } => LLVMValueRef.CreateConstNot(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.Not } => builder.BuildNot(EmitValue(v.Operand, builder)),
		
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitIndexer(IndexerValue v, LLVMBuilderRef builder)
	{
		var target = EmitValue(v.Target, builder);
		var index = EmitValue(v.Index, builder);
		var elementType = MapTypeSymbol(v.Type);
		
		// TODO Switch to BuildInBoundsGEP2 once compiler-generated bounds checks are implemented
		var dataPtr = builder.BuildExtractValue(target, 1, "dataptr");
		var elemPtr = builder.BuildGEP2(elementType, dataPtr, new[] { index }, "elemptr");
		
		return builder.BuildLoad2(elementType, elemPtr, "elem");
	}
	
	private LLVMValueRef EmitIndexerAddress(IndexerValue v, LLVMBuilderRef builder)
	{
		var target = EmitValue(v.Target, builder);
		var index = EmitValue(v.Index, builder);
		var elementType = MapTypeSymbol(v.Type);
		
		// TODO Switch to BuildInBoundsGEP2 once compiler-generated bounds checks are implemented
		var dataPtr = builder.BuildExtractValue(target, 1, "dataptr");
		var elemPtr = builder.BuildGEP2(elementType, dataPtr, new[] { index }, "elemptr");
		
		return elemPtr;
	}
	
	private LLVMValueRef EmitAccessValue(AccessValue v, LLVMBuilderRef builder)
	{
		// TODO Fields could have been reordered to pack them
		// TODO Also, GetFieldIndex is O(n), would probably want to cache the final indices in another dictionary
		var target = EmitValue(v.Target, builder);
		var fieldIndex = (uint)_typeMemberTable.GetFieldIndex(v.Target.Type, v.Member);
		return builder.BuildExtractValue(target, fieldIndex, v.Member.Name);
	}
	
	private LLVMValueRef EmitAssignValue(AssignValue v, LLVMBuilderRef builder)
	{
		var right = EmitValue(v.Right, builder);
		builder.BuildStore(right, EmitAddress(v.Left, builder));
		return right;
	}
	
	private LLVMValueRef EmitAddress(Value value, LLVMBuilderRef builder) => value switch
	{
		VariableValue v => _varMap[v.Variable],
		IndexerValue v => EmitIndexerAddress(v, builder),
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
				case PrimitiveTypeKind.Int8:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(sbyte)value), true);
				
				case PrimitiveTypeKind.Int16:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(short)value), true);
				
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
				
				// TODO How to properly interpret this?
				case PrimitiveTypeKind.IntSize:
					return LLVMValueRef.CreateConstInt(type, unchecked((ulong)(long)value), true);
				
				case PrimitiveTypeKind.UInt8:
					return LLVMValueRef.CreateConstInt(type, (byte)value);
				
				case PrimitiveTypeKind.UInt16:
					return LLVMValueRef.CreateConstInt(type, (ushort)value);
				
				case PrimitiveTypeKind.UInt32:
					return LLVMValueRef.CreateConstInt(type, (uint)value);
				
				case PrimitiveTypeKind.UInt64:
					return LLVMValueRef.CreateConstInt(type, (ulong)value);
				
				case PrimitiveTypeKind.UInt128:
				{
					// Little Endian
					var u128 = (UInt128)value;
					Span<ulong> words = stackalloc ulong[2];
					words[0] = (ulong)u128;
					words[1] = (ulong)(u128 >> 64);
					return LLVMValueRef.CreateConstIntOfArbitraryPrecision(type, words);
				}
				
				// TODO How to properly interpret this?
				case PrimitiveTypeKind.UIntSize:
					return LLVMValueRef.CreateConstInt(type, (ulong)value);
				
				case PrimitiveTypeKind.Str:
				{
					var lengthType = MapTypeSymbol(NativeSymbols.UIntSize);
					var str = (StrValue)value;
					var ptr = GetOrCreateStringGlobal(str.Bytes);
					
					var length = LLVMValueRef.CreateConstInt(lengthType, str.Length);
					return LLVMValueRef.CreateConstStruct([length, ptr], false);
				}
				
				case PrimitiveTypeKind.CStr:
					return GetOrCreateStringGlobal((byte[])value);
				
				case PrimitiveTypeKind.Bool:
					return (bool)value ? _true : _false;
			}
		}
		
		throw new InvalidOperationException();
	}
	
	private LLVMValueRef GetOrCreateStringGlobal(byte[] bytes)
	{
		if (_stringPool.TryGetValue(bytes, out var existing))
			return existing;
		
		var byteType = MapTypeSymbol(NativeSymbols.UInt8);
		var byteValues = bytes.Select(b => LLVMValueRef.CreateConstInt(byteType, b));
		var arrayValue = LLVMValueRef.CreateConstArray(byteType, [..byteValues]);
		
		var global = currentModule.AddGlobal(arrayValue.TypeOf, string.Empty);
		global.Initializer = arrayValue;
		global.IsGlobalConstant = true;
		global.Linkage = LLVMLinkage.LLVMLinkerPrivateLinkage;
		global.HasUnnamedAddr = true;
		
		var ptr = LLVMValueRef.CreateConstInBoundsGEP2(byteType, global,
			[LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)]);
		
		_stringPool[bytes] = ptr;
		return ptr;
	}
	
	private static (string DataLayout, string TargetTriple, LLVMTargetMachineRef TargetMachine, uint PointerSize)
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
			return (SpanExtensions.AsString(dataLayoutStr), triple, targetMachine, dataLayout.PointerSize());
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

public readonly struct CodeGenResult
{
	public static readonly CodeGenResult Failure = new(false, null, null, []);
	
	public bool IsSuccess { get; }
	
	[MemberNotNullWhen(true, nameof(IsSuccess))]
	public string? OutputPath { get; }
	
	[MemberNotNullWhen(false, nameof(IsSuccess))]
	public string? ErrorMessage { get; init; }
	
	public ImmutableHashSet<string> ExternalLibraries { get; }
	
	public CodeGenResult(bool isSuccess, string? outputPath, string? errorMessage,
		IEnumerable<string> externalLibraries)
	{
		IsSuccess = isSuccess;
		OutputPath = outputPath;
		ErrorMessage = errorMessage;
		ExternalLibraries = externalLibraries.ToImmutableHashSet();
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

internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
	public static ByteArrayComparer Instance { get; } = new();
	
	public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);
	
	public int GetHashCode(byte[] obj)
	{
		var hash = new HashCode();
		hash.AddBytes(obj);
		return hash.ToHashCode();
	}
}