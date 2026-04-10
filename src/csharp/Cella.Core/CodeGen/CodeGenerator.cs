using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
	
	public static void Init()
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
		(_dataLayoutStr, TargetTriple, _targetMachine, _pointerSize) = config.GetDataLayout();
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
		switch (symbol)
		{
			case ArrayType arrayType:
			{
				var elementType = MapTypeSymbol(arrayType.ElementType);
				var length = (uint)arrayType.Length;
				var llvmArray = LLVMTypeRef.CreateArray(elementType, length);
				_typeMap[symbol] = llvmArray;
				return llvmArray;
			}
			
			case SpanType spanType:
			{
				var elementType = MapTypeSymbol(spanType.ElementType);
				var usizeType = LLVMTypeRef.CreateInt(_pointerSize * 8);
				var ptrType = LLVMTypeRef.CreatePointer(elementType, 0u);
				var llvmSpan = LLVMTypeRef.CreateStruct([usizeType, ptrType], false);
				_typeMap[symbol] = llvmSpan;
				return llvmSpan;
			}
			
			default:
				throw new InvalidOperationException($"Symbol '{symbol.Name}' not mapped in LLVM");
		}
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
		ArrayValue v => EmitArrayValue(v, builder),
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
		var (elemPtr, elemType) = EmitIndexerAddress(v, builder);
		return builder.BuildLoad2(elemType, elemPtr, "elem");
	}
	
	private (LLVMValueRef Ptr, LLVMTypeRef ElemType) EmitIndexerAddress(IndexerValue v, LLVMBuilderRef builder)
	{
		var index = EmitValue(v.Index, builder);
		var elemType = MapTypeSymbol(v.Type);
		
		// TODO Switch to BuildInBoundsGEP2 once compiler-generated bounds checks are implemented
		switch (v.Target.Type)
		{
			case SpanType:
			{
				var target = EmitValue(v.Target, builder);
				var dataPtr = builder.BuildExtractValue(target, 1, "dataptr");
				var elemPtr = builder.BuildGEP2(elemType, dataPtr, new[] { index }, "elemptr");
				return (elemPtr, elemType);
			}
			
			case ArrayType a:
			{
				var arrayPtr = EmitAddress(v.Target, builder);
				var arrayType = MapTypeSymbol(a);
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				var elemPtr = builder.BuildGEP2(arrayType, arrayPtr, new[] { zero, index }, "elemptr");
				return (elemPtr, elemType);
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitAccessValue(AccessValue v, LLVMBuilderRef builder)
	{
		// Special case for arrays
		switch (v.Target.Type)
		{
			case ArrayType arrayType:
				return EmitSizeConstant(arrayType.Length, true);
		}
		
		// TODO Fields could have been reordered to pack them
		// TODO Also, GetFieldIndex is O(n), would probably want to cache the final indices in another dictionary
		var target = EmitValue(v.Target, builder);
		var fieldIndex = (uint)_typeMemberTable.GetFieldIndex(v.Target.Type, v.Member);
		return builder.BuildExtractValue(target, fieldIndex, v.Member.Name);
	}
	
	private LLVMValueRef EmitArrayValue(ArrayValue value, LLVMBuilderRef builder)
	{
		var arrayType = MapTypeSymbol(value.ArrayType);
		
		if (value.IsConstant)
			return LLVMValueRef.CreateConstArray(MapTypeSymbol(value.ArrayType.ElementType),
				[..value.Elements.Select(e => EmitValue(e, builder))]);
		
		var agg = arrayType.Undef;
		for (uint i = 0; i < value.Elements.Length; i++)
			agg = builder.BuildInsertValue(agg, EmitValue(value.Elements[(int)i], builder), i, $"arr{i}");
		
		return agg;
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
		IndexerValue v => EmitIndexerAddress(v, builder).Ptr,
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitConstant(ConstantValue constant)
	{
		var type = MapTypeSymbol(constant.Type);
		
		if (constant.Value is not { } value)
			return LLVMValueRef.CreateConstNull(type);
		
		if (constant.Type is PrimitiveType primitiveType)
		{
			var ptrBits = (int)(_pointerSize * 8);
			var ptrWordCount = GetWordCount(ptrBits);
			
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
				
				case PrimitiveTypeKind.IntSize:
					return EmitSizeConstant((BigInteger)value, false);
				
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
				
				case PrimitiveTypeKind.UIntSize:
					return EmitSizeConstant((BigInteger)value, true);
				
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
		var spanValue = LLVMValueRef.CreateConstArray(byteType, [..byteValues]);
		
		var global = currentModule.AddGlobal(spanValue.TypeOf, string.Empty);
		global.Initializer = spanValue;
		global.IsGlobalConstant = true;
		global.Linkage = LLVMLinkage.LLVMLinkerPrivateLinkage;
		global.HasUnnamedAddr = true;
		
		var ptr = LLVMValueRef.CreateConstInBoundsGEP2(byteType, global,
			[LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)]);
		
		_stringPool[bytes] = ptr;
		return ptr;
	}
	
	private LLVMValueRef EmitSizeConstant(BigInteger value, bool isUnsigned)
	{
		var usizeType = MapTypeSymbol(isUnsigned ? NativeSymbols.UIntSize : NativeSymbols.IntSize);
		var ptrBits = (int)(_pointerSize * 8);
		var wordCount = GetWordCount(ptrBits);
		Span<ulong> words = stackalloc ulong[wordCount];
		BigIntegerToWords(value, ptrBits, isUnsigned, words);
		return LLVMValueRef.CreateConstIntOfArbitraryPrecision(usizeType, words);
	}
	
	private static void BigIntegerToWords(BigInteger value, int maxBitCount, bool isUnsigned, Span<ulong> words)
    {
        switch (maxBitCount)
        {
	        case < 0:
		        throw new ArgumentException($"{nameof(maxBitCount)} must be non‑negative.", nameof(maxBitCount));
	        
	        case 0:
		        return;
        }
        
        var maxByteSize = (value.GetByteCount() + 7) & ~7;
        Span<byte> bytes = stackalloc byte[maxByteSize];
        value.TryWriteBytes(bytes, out var bytesWritten, isUnsigned);
        
        for (var i = 0; i < words.Length; i++)
        {
	        var offset = i * 8;
	        var word = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
	        
	        // The last word may contain padding bytes, need to sign-extend if signed
	        if (!isUnsigned && i == words.Length - 1)
	        {
		        var lastWordBytes = bytesWritten & 7;
		        if (lastWordBytes == 0)
			        lastWordBytes = 8;
		        
		        if (lastWordBytes < 8)
		        {
			        // If highest bit is set, sign-extend padding bytes
			        var msb = bytes[offset + lastWordBytes - 1];
			        if ((msb & 0x80) != 0)
				        word |= ulong.MaxValue << (lastWordBytes * 8);
		        }
	        }
	        
	        words[i] = word;
        }
    }
	
	private const int BitsPerWord = sizeof(ulong) * 8;
	
    /// <summary>
    /// Returns the number of ulong words required to hold <see cref="maxBitCount"/> bits.
    /// </summary>
    private static int GetWordCount(int maxBitCount) => (maxBitCount + BitsPerWord - 1) / BitsPerWord;
	
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
)
{
	public uint GetPointerSize() => GetDataLayout().PointerSize;
	
	public unsafe (string DataLayout, string TargetTriple, LLVMTargetMachineRef TargetMachine, uint PointerSize)
		GetDataLayout()
	{
		CodeGenerator.Init();
		var triple = TargetConfig?.TargetTriple ?? LLVMTargetRef.DefaultTriple;
		
		var targetRef = LLVMTargetRef.GetTargetFromTriple(triple);
		var cpu = string.IsNullOrWhiteSpace(TargetConfig?.Cpu) ? "generic" : TargetConfig.Cpu;
		var features = TargetConfig?.Features ?? "";
		
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
}

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