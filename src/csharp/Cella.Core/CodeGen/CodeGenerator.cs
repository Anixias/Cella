using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.CodeGen.Extensions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

// ReSharper disable StringLiteralTypo

namespace Cella.Core.CodeGen;

internal readonly record struct LLVMFunctionInfo
(
	LLVMValueRef FunctionValue,
	LLVMTypeRef FunctionType,
	LLVMTypeRef ReturnType,
	CSignature? CSignature
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
	private readonly TypePool _typePool;
	private readonly CodeGenConfig _config;
	private readonly string _dataLayoutStr;
	private readonly LLVMTargetMachineRef _targetMachine;
	private readonly LLVMTargetDataRef _targetData;
	private readonly CAbi _cAbi;
	private readonly uint _pointerSize;
	private readonly LLVMPassBuilderOptionsRef _passBuilderOptions = LLVMPassBuilderOptionsRef.Create();
	private readonly Dictionary<TypeSymbol, LLVMTypeRef> _typeMap = [];
	private readonly Dictionary<FunctionInfo, LLVMFunctionInfo> _funMap = [];
	private readonly Dictionary<VariableInfo, LLVMValueRef> _varMap = [];
	private readonly Dictionary<GlobalSymbol, LLVMValueRef> _globalMap = [];
	private readonly LLVMValueRef _true = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1uL);
	private readonly LLVMValueRef _false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0uL);
	private readonly HashSet<string> _externalLibraries = [];
	private readonly Dictionary<byte[], LLVMValueRef> _stringPool = new(ByteArrayComparer.Instance);
	private LLVMModuleRef currentModule;
	private LLVMFunctionInfo currentFunction;
	
	public CodeGenerator(AssemblySymbol assemblySymbol, TypePool typePool, CodeGenConfig config)
	{
		Init();
		_passBuilderOptions.SetVerifyEach(true);
		_assemblySymbol = assemblySymbol;
		_typePool = typePool;
		_config = config;
		(_dataLayoutStr, TargetTriple, _targetMachine, _pointerSize) = config.GetDataLayout();
		_targetData = LLVMTargetDataRef.FromStringRepresentation(_dataLayoutStr);
		_cAbi = new CAbi(_targetData, TargetTriple);
	}
	
	private LLVMErrorRef RunOptimizationPass(LLVMModuleRef module)
	{
		using var passStr = new MarshaledString(_config.OptimizeMode switch
		{
			OptimizeMode.Release => "default<O3>",
			_ => "default<O0>"
		});
		
		var errorHandle = LLVM.RunPasses((LLVMOpaqueModule*)module.Handle, passStr,
			(LLVMOpaqueTargetMachine*)_targetMachine.Handle, (LLVMOpaquePassBuilderOptions*)_passBuilderOptions.Handle);
		
		return new LLVMErrorRef((nint)errorHandle);
	}
	
	private void MapNativeSymbols()
	{
		// TODO Map address spaces based on target?
		
		var intSize = LLVMTypeRef.CreateInt(_pointerSize * 8);
		_typeMap[NativeSymbols.Void] = LLVMTypeRef.Void;
		_typeMap[NativeSymbols.VoidPtr] = LLVMTypeRef.CreatePointer(LLVMTypeRef.Void, 0u);
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
		_typeMap[NativeSymbols.Float32] = LLVMTypeRef.Float;
		_typeMap[NativeSymbols.Float64] = LLVMTypeRef.Double;
		_typeMap[NativeSymbols.Char] = LLVMTypeRef.Int32;
		_typeMap[NativeSymbols.Bool] = LLVMTypeRef.Int1;
		_typeMap[NativeSymbols.Str] =
			LLVMTypeRef.CreateStruct([intSize, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u)], false);
		
		_typeMap[NativeSymbols.CStr] = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u);
	}
	
	public CodeGenResult Generate(LoweredModule module)
	{
		MapNativeSymbols();
		
		using var llvmModule = LLVMModuleRef.CreateWithName(module.Symbol.Name);
		llvmModule.Target = TargetTriple;
		llvmModule.DataLayout = _dataLayoutStr;
		currentModule = llvmModule;
		var llvmDiBuilder = llvmModule.CreateDIBuilder();
		try
		{
			string message;
			
			// Build code
			BuildModule(llvmModule, llvmDiBuilder, module);
			
			_funMap.Clear();
			_varMap.Clear();
			_globalMap.Clear();
			_typeMap.Clear();
			
			if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMAbortProcessAction, out message))
				return CodeGenResult.Failure with { ErrorMessage = message };
			
			llvmDiBuilder.DIBuilderFinalize();
			
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
			
			case BufferType bufferType:
			{
				var elementType = MapTypeSymbol(bufferType.ElementType);
				var usizeType = LLVMTypeRef.CreateInt(_pointerSize * 8);
				var ptrType = LLVMTypeRef.CreatePointer(elementType, 0u);
				var llvmBuffer = LLVMTypeRef.CreateStruct([usizeType, ptrType], false);
				_typeMap[symbol] = llvmBuffer;
				return llvmBuffer;
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
			
			case ViewType viewType:
			{
				var elementType = MapTypeSymbol(viewType.ElementType);
				var usizeType = LLVMTypeRef.CreateInt(_pointerSize * 8);
				var ptrType = LLVMTypeRef.CreatePointer(elementType, 0u);
				var llvmSpan = LLVMTypeRef.CreateStruct([usizeType, ptrType], false);
				_typeMap[symbol] = llvmSpan;
				return llvmSpan;
			}
			
			case FunctionType:
			{
				var llvmFunctionPointer = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u);
				_typeMap[symbol] = llvmFunctionPointer;
				return llvmFunctionPointer;
			}
			
			case PointerType ptrType:
			{
				var baseType = MapTypeSymbol(ptrType.BaseType);
				var llvmPtrType = LLVMTypeRef.CreatePointer(baseType, 0u);
				_typeMap[symbol] = llvmPtrType;
				return llvmPtrType;
			}
			
			case EnumSymbol enumType:
				return CreateEnumType(enumType);
			
			default:
				return CreateType(symbol);
			//throw new InvalidOperationException($"Type '{symbol.Name}' not mapped in LLVM");
		}
	}
	
	private void BuildModule(LLVMModuleRef llvmModule, LLVMDIBuilderRef llvmDiBuilder, LoweredModule module)
	{
		var isOptimized = _config.OptimizeMode == OptimizeMode.Debug ? 0 : 1;
		var dwarfLang = LLVMDWARFSourceLanguage.LLVMDWARFSourceLanguageC99;
		
		/*foreach (var file in module.Files)
		{
			LLVMMetadataRef fileMetadata;
			{
				var fullPath = file.Symbol.FullPath;
				var fileName = Path.GetFileName(fullPath);
				var directory = Path.GetDirectoryName(fullPath)?.Replace('\\', '/') ?? string.Empty;
				fileMetadata = llvmDiBuilder.CreateFile(fileName, directory);
			}
		
			// TODO Emit debug info for types, functions, etc.
			var compileUnit = llvmDiBuilder.CreateCompileUnit(dwarfLang, fileMetadata, "", isOptimized, "", 0, "",
				LLVMDWARFEmissionKind.LLVMDWARFEmissionFull, 0, 1, 0, "", "");
		}*/
		
		LLVMFunctionInfo? entryPoint = null;
		foreach (var file in module.Files)
		{
			// Create and map external functions
			foreach (var function in file.ExternalFunctions)
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
			foreach (var function in file.ImportedFunctions)
			{
				var info = CreateFunction(llvmModule, function);
				var llvmFunction = info.FunctionValue;
				llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
				llvmFunction.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
			}
			
			// Create and map functions
			foreach (var function in file.Functions)
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
					llvmFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
				}
				
				if (_assemblySymbol.EntryPoint?.Symbol == function.Info.Symbol)
					entryPoint = info;
			}
		}
		
		foreach (var file in module.Files)
			foreach (var global in file.Globals)
				DefineGlobal(global);
		
		foreach (var file in module.Files)
		{
			// Build function bodies
			foreach (var function in file.Functions)
				BuildFunction(llvmModule, llvmDiBuilder, function);
		}
		
		if (entryPoint is { } entry)
			BuildEntryPoint(llvmModule, entry);
		
		// Optimize the module
		RunOptimizationPass(llvmModule);
		
		llvmDiBuilder.DIBuilderFinalize();
	}
	
	private LLVMTypeRef CreateType(TypeSymbol type)
	{
		if (_typeMap.TryGetValue(type, out var existing))
			return existing;
		
		var typeRef = LLVMContextRef.Global.CreateNamedStruct(type.Name);
		_typeMap[type] = typeRef;
		
		var fieldTypes = _typePool
			.GetMembers(type)
			.OfType<FieldSymbol>()
			.Select(f => _typePool.GetTypeOfMember(f))
			.Select(MapTypeSymbol)
			.ToArray();
		
		// TODO Allow controlling packed?
		typeRef.StructSetBody(fieldTypes, false);
		return typeRef;
	}
	
	private LLVMTypeRef CreateEnumType(EnumSymbol enumType)
	{
		var typeRef = LLVMContextRef.Global.CreateNamedStruct(enumType.Name);
		_typeMap[enumType] = typeRef;
		
		var tagType = MapTypeSymbol(TypePool.GetTagType(enumType));
		var payloadTypes = enumType.Cases.Where(static c => c.Fields.Length > 0).Select(GetPayloadType).ToList();
		if (payloadTypes.Count == 0)
		{
			typeRef.StructSetBody([tagType], false);
			return typeRef;
		}
		
		var alignment = payloadTypes.Max(type => _targetData.ABIAlignmentOfType(type));
		var size = payloadTypes.Max(type => _targetData.ABISizeOfType(type));
		var count = (uint)((size + alignment - 1) / alignment);
		typeRef.StructSetBody([tagType, LLVMTypeRef.CreateArray(LLVMTypeRef.CreateInt(alignment * 8), count)], false);
		return typeRef;
	}
	
	private LLVMTypeRef GetPayloadType(EnumCaseSymbol enumCase) => LLVMTypeRef.CreateStruct(
		[..enumCase.Fields.Select(field => MapTypeSymbol(_typePool.GetTypeOfMember(field)))], false);
	
	private LLVMFunctionInfo CreateFunction(LLVMModuleRef llvmModule, FunctionInfo function)
	{
		if (_funMap.TryGetValue(function, out var existing))
			return existing;
		
		var signature = function.Signature;
		var symbol = function.Symbol;
		var returnType = MapTypeSymbol(signature.ReturnType);
		
		var paramTypes = signature.ParameterTypes;
		var paramLlvmTypes = new LLVMTypeRef[paramTypes.Length];
		for (var i = 0; i < paramTypes.Length; i++)
			paramLlvmTypes[i] = MapTypeSymbol(paramTypes[i]);
		
		CSignature? cSignature = symbol.IsExternal
			? _cAbi.Classify(paramLlvmTypes, returnType)
			: null;
		
		var functionType = cSignature?.CreateFunctionType(signature.IsVariadic)
		                   ?? LLVMTypeRef.CreateFunction(returnType, paramLlvmTypes, signature.IsVariadic);
		
		var functionValue = llvmModule.AddFunction(function.MangledName ?? symbol.Name, functionType);
		if (cSignature is { Return: { Kind: CPassKind.Indirect } sret })
			AddSretAttribute(functionValue, sret.Type, false);
		
		var functionInfo = new LLVMFunctionInfo(functionValue, functionType, returnType, cSignature);
		
		_funMap.Add(function, functionInfo);
		return functionInfo;
	}
	
	private static void BuildEntryPoint(LLVMModuleRef llvmModule, LLVMFunctionInfo entryPoint)
	{
		var main = llvmModule.AddFunction("main", LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, []));
		main.Linkage = LLVMLinkage.LLVMExternalLinkage;
		
		using var builder = llvmModule.Context.CreateBuilder();
		builder.PositionAtEnd(main.AppendBasicBlock("entry"));
		
		var result = builder.BuildCall2(entryPoint.FunctionType, entryPoint.FunctionValue, []);
		builder.BuildRet(entryPoint.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind
			? LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			: result);
	}
	
	private void BuildFunction(LLVMModuleRef llvmModule, LLVMDIBuilderRef llvmDiBuilder, LoweredFunction function)
	{
		currentFunction = _funMap[function.Info];
		var functionValue = currentFunction.FunctionValue;
		var allocaBlock = functionValue.AppendBasicBlock("allocas");
		
		// Create blocks
		var blockMap = new Dictionary<BasicBlock, LLVMBasicBlockRef>();
		foreach (var block in function.Blocks)
			blockMap.Add(block, functionValue.AppendBasicBlock(block.Label));
		
		// Build blocks
		using var builder = llvmModule.Context.CreateBuilder();
		builder.PositionAtEnd(allocaBlock);
		foreach (var local in function.Blocks.SelectMany(static b => b.Instructions).OfType<LocalVarInstruction>())
			_varMap[new(local.Symbol, local.Symbol.Type)] =
				BuildEntryAlloca(builder, MapTypeSymbol(local.Symbol.Type), local.Symbol.Name);
		
		for (var i = 0; i < function.Blocks.Count; i++)
		{
			var block = function.Blocks[i];
			var llvmBlock = blockMap[block];
			builder.PositionAtEnd(llvmBlock);
			
			// First block allocates stack variables for parameters
			if (i == 0)
			{
				var parameters = function.Info.Symbol.Parameters;
				var signature = currentFunction.CSignature;
				var firstParameter = signature?.Return.Kind == CPassKind.Indirect ? 1u : 0u;
				for (var p = 0; p < parameters.Length; p++)
				{
					var paramSymbol = parameters[p];
					var paramType = function.Info.Signature.ParameterTypes[p];
					var paramInfo = new VariableInfo(paramSymbol, paramType);
					
					var paramLlvmValue = functionValue.GetParam(firstParameter + (uint)p);
					var paramLlvmType = MapTypeSymbol(paramType);
					if (signature is { Parameters: var passes })
						paramLlvmValue = ReceiveCArgument(passes[p], paramLlvmValue, paramLlvmType, builder);
					
					var paramPtr = BuildEntryAlloca(builder, paramLlvmType, paramSymbol.Name);
					builder.BuildStore(paramLlvmValue, paramPtr);
					_varMap[paramInfo] = paramPtr;
				}
			}
			
			foreach (var instruction in block.Instructions)
				EmitInstruction(builder, instruction, blockMap);
			
			EmitTerminator(builder, block.Terminator, blockMap);
		}
		
		builder.PositionAtEnd(allocaBlock);
		builder.BuildBr(blockMap[function.Blocks[0]]);
		
		functionValue.VerifyFunction(LLVMVerifierFailureAction.LLVMAbortProcessAction);
	}
	
	private static LLVMValueRef BuildEntryAlloca(LLVMBuilderRef builder, LLVMTypeRef type, string name)
	{
		using var allocaBuilder = type.Context.CreateBuilder();
		allocaBuilder.PositionAtEnd(builder.InsertBlock.Parent.EntryBasicBlock);
		return allocaBuilder.BuildAlloca(type, name);
	}
	
	private void EmitInstruction(LLVMBuilderRef builder, IInstruction instruction,
		Dictionary<BasicBlock, LLVMBasicBlockRef> blockMap)
	{
		switch (instruction)
		{
			case BeginScopeInstruction:
			case EndScopeInstruction:
				break;
			
			case LocalVarInstruction i:
			{
				if (i.Initializer is not UndefValue)
					builder.BuildStore(EmitValue(i.Initializer, builder), _varMap[new(i.Symbol, i.Symbol.Type)]);
				
				break;
			}
			
			case ExpressionInstruction i:
			{
				EmitValue(i.Value, builder);
				break;
			}
			
			case DropInstruction i:
			{
				EmitDrop(i.Value, builder);
				break;
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private void EmitDrop(Value value, LLVMBuilderRef builder)
	{
		while (value is ConversionValue conversion)
			value = conversion.Source;
		
		switch (value.Type)
		{
			case PointerType { PointerKind: PointerKind.Owning }:
			{
				// Drop the owned pointer value, not the address of the storage slot that
				// contains it. For example, `drop x: own[i32]` must lower to
				// `free(load x)`, and `drop record.field: own[i32]` must lower to
				// `free(load &record.field)`.
				var ptr = EmitValue(value, builder);
				builder.BuildFree(ptr);
				break;
			}
			
			case ArrayType array:
				EmitArrayDrop(value, array, builder);
				break;
			
			case RecordSymbol record:
				EmitRecordDrop(value, record, builder);
				break;
		}
	}
	
	private void EmitArrayDrop(Value value, ArrayType array, LLVMBuilderRef builder)
	{
		if (!_typePool.NeedsDrop(array.ElementType))
			return;
		
		if (array.Length < 0 || array.Length > int.MaxValue)
			throw new InvalidOperationException(
				$"Cannot drop array type '{array.Name}' with non-fixed or too-large length");
		
		var length = (int)array.Length;
		
		if (IsAddressable(value))
		{
			for (var i = length - 1; i >= 0; i--)
			{
				var index = new ConstantValue(NativeSymbols.Int32, i);
				var element = new IndexerValue(array.ElementType, value, index, value.SourceLocation);
				EmitDrop(element, builder);
			}
			
			return;
		}
		
		var aggregate = EmitValue(value, builder);
		for (var i = length - 1; i >= 0; i--)
		{
			var elementValue = builder.BuildExtractValue(aggregate, (uint)i, $"drop.elem{i}");
			EmitDropValue(elementValue, array.ElementType, builder);
		}
	}
	
	private void EmitDropValue(LLVMValueRef value, TypeSymbol type, LLVMBuilderRef builder)
	{
		switch (type)
		{
			case PointerType { PointerKind: PointerKind.Owning }:
				builder.BuildFree(value);
				break;
			
			case ArrayType array:
			{
				if (!_typePool.NeedsDrop(array.ElementType))
					break;
				
				if (array.Length < 0 || array.Length > int.MaxValue)
					throw new InvalidOperationException(
						$"Cannot drop array type '{array.Name}' with non-fixed or too-large length");
				
				var length = (int)array.Length;
				for (var i = length - 1; i >= 0; i--)
				{
					var elementValue = builder.BuildExtractValue(value, (uint)i, $"drop.elem{i}");
					EmitDropValue(elementValue, array.ElementType, builder);
				}
				
				break;
			}
			
			case RecordSymbol record:
			{
				foreach (var field in record.Members.OfType<FieldSymbol>().Reverse())
				{
					var fieldType = _typePool.GetTypeOfMember(field);
					if (!_typePool.NeedsDrop(fieldType))
						continue;
					
					var fieldIndex = (uint)_typePool.GetFieldIndex(record, field);
					var fieldValue = builder.BuildExtractValue(value, fieldIndex, field.Name);
					EmitDropValue(fieldValue, fieldType, builder);
				}
				
				break;
			}
		}
	}
	
	private void EmitRecordDrop(Value value, RecordSymbol record, LLVMBuilderRef builder)
	{
		// Records own their dropping fields; the record storage itself may be stack
		// storage, a field, or a dereferenced pointer. Never free the record address.
		foreach (var field in record.Members.OfType<FieldSymbol>().Reverse())
		{
			var fieldType = _typePool.GetTypeOfMember(field);
			if (!_typePool.NeedsDrop(fieldType))
				continue;
			
			EmitDrop(new AccessValue(fieldType, value, field, value.SourceLocation), builder);
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
					EmitReturn(EmitValue(value, builder), builder);
				
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
	
	private void EmitReturn(LLVMValueRef value, LLVMBuilderRef builder)
	{
		switch (currentFunction.CSignature?.Return)
		{
			case { Kind: CPassKind.Indirect }:
				builder.BuildStore(value, currentFunction.FunctionValue.GetParam(0));
				builder.BuildRetVoid();
				break;
			
			case { } pass:
				builder.BuildRet(PassCArgument(pass, value, builder));
				break;
			
			default:
				builder.BuildRet(value);
				break;
		}
	}
	
	private LLVMValueRef EmitValue(Value value, LLVMBuilderRef builder) => value switch
	{
		ConstantValue v => EmitConstant(v),
		ZeroValue v => EmitZero(v),
		VariableValue v => builder.BuildLoad2(MapTypeSymbol(v.Type), _varMap[v.Variable], v.Variable.Symbol.Name),
		GlobalValue v => EmitGlobalLoad(v.Global, builder),
		BinOpValue v => EmitBinaryOp(v, builder),
		UnaryOpValue v => EmitUnaryOp(v, builder),
		AssignValue v => EmitAssignValue(v, builder),
		IndexerValue v => EmitIndexer(v, builder),
		AccessValue v => EmitAccessValue(v, builder),
		ArrayValue v => EmitArrayValue(v, builder),
		ConversionValue v => EmitConversion(v, builder),
		CallValue v => EmitCall(v, builder),
		FunctionReferenceValue v => GetFunctionValue(v.Function),
		IndirectCallValue v => EmitIndirectCall(v, builder),
		PointerOffsetValue v => EmitPointerOffset(v, builder),
		PointerDifferenceValue v => EmitPointerDifference(v, builder),
		HeapValue v => EmitHeap(v, builder),
		EnumValue v => EmitEnumValue(v, builder),
		EnumTagValue v => EmitEnumTag(v, builder),
		EnumPayloadValue v => EmitEnumPayload(v, builder),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitCall(CallValue v, LLVMBuilderRef builder)
	{
		var function = _funMap[v.Function];
		var args = v.Arguments.Select(a => EmitValue(a, builder)).ToList();
		if (function.CSignature is not { } signature)
			return builder.BuildCall2(function.FunctionType, function.FunctionValue, args.ToArray());
		
		return EmitCCall(signature, function.FunctionType, function.FunctionValue, function.ReturnType, args, builder);
	}
	
	private LLVMValueRef EmitIndirectCall(IndirectCallValue v, LLVMBuilderRef builder)
	{
		var target = EmitValue(v.Target, builder);
		var args = v.Arguments.Select(a => EmitValue(a, builder)).ToList();
		var returnType = MapTypeSymbol(v.FunctionType.ReturnType);
		var signature = _cAbi.Classify(v.FunctionType.ParameterTypes.Select(MapTypeSymbol), returnType);
		return EmitCCall(signature, signature.CreateFunctionType(false), target, returnType, args, builder);
	}
	
	private LLVMValueRef EmitCCall(CSignature signature, LLVMTypeRef functionType, LLVMValueRef callee,
		LLVMTypeRef returnType, List<LLVMValueRef> args, LLVMBuilderRef builder)
	{
		for (var i = 0; i < signature.Parameters.Length; i++)
			args[i] = PassCArgument(signature.Parameters[i], args[i], builder);
		
		var returnSlot = default(LLVMValueRef);
		if (signature.Return.Kind == CPassKind.Indirect)
		{
			returnSlot = BuildIndirectSlot(builder, signature.Return.Type);
			args.Insert(0, returnSlot);
		}
		
		var result = builder.BuildCall2(functionType, callee, args.ToArray());
		switch (signature.Return.Kind)
		{
			case CPassKind.Integer:
			{
				var slot = BuildEntryAlloca(builder, signature.Return.Type, "coerce");
				builder.BuildStore(result, slot);
				return builder.BuildLoad2(returnType, slot);
			}
			
			case CPassKind.Indirect:
				AddSretAttribute(result, signature.Return.Type, true);
				return builder.BuildLoad2(signature.Return.Type, returnSlot);
			
			default:
				return result;
		}
	}
	
	private static LLVMValueRef PassCArgument(CPass pass, LLVMValueRef value, LLVMBuilderRef builder)
	{
		switch (pass.Kind)
		{
			case CPassKind.Integer:
			{
				var slot = BuildEntryAlloca(builder, pass.Type, "coerce");
				builder.BuildStore(value, slot);
				return builder.BuildLoad2(pass.Type, slot);
			}
			
			case CPassKind.Indirect:
			{
				var copy = BuildIndirectSlot(builder, pass.Type);
				builder.BuildStore(value, copy);
				return copy;
			}
			
			default:
				return value;
		}
	}
	
	private static LLVMValueRef ReceiveCArgument(CPass pass, LLVMValueRef value, LLVMTypeRef type,
		LLVMBuilderRef builder)
	{
		switch (pass.Kind)
		{
			case CPassKind.Integer:
			{
				var slot = BuildEntryAlloca(builder, pass.Type, "coerce");
				builder.BuildStore(value, slot);
				return builder.BuildLoad2(type, slot);
			}
			
			case CPassKind.Indirect:
				return builder.BuildLoad2(type, value);
			
			default:
				return value;
		}
	}
	
	private static LLVMValueRef BuildIndirectSlot(LLVMBuilderRef builder, LLVMTypeRef type)
	{
		var slot = BuildEntryAlloca(builder, type, "indirect");
		slot.Alignment = Math.Max(slot.Alignment, 16);
		return slot;
	}
	
	private static void AddSretAttribute(LLVMValueRef value, LLVMTypeRef type, bool isCall)
	{
		using var name = new MarshaledString("sret");
		var kind = LLVM.GetEnumAttributeKindForName(name, (nuint)name.Length);
		var attribute = LLVM.CreateTypeAttribute((LLVMOpaqueContext*)type.Context.Handle, kind,
			(LLVMOpaqueType*)type.Handle);
		
		if (isCall)
			LLVM.AddCallSiteAttribute((LLVMOpaqueValue*)value.Handle, (LLVMAttributeIndex)1, attribute);
		else
			LLVM.AddAttributeAtIndex((LLVMOpaqueValue*)value.Handle, (LLVMAttributeIndex)1, attribute);
	}
	
	private LLVMValueRef EmitPointerOffset(PointerOffsetValue v, LLVMBuilderRef builder)
	{
		var ptr = EmitValue(v.Pointer, builder);
		var offset = EmitValue(v.Offset, builder);
		
		if (v.Op == BinaryOperation.Subtraction)
			offset = offset.IsConstant
				? LLVMValueRef.CreateConstNeg(offset)
				: builder.BuildNeg(offset, "ptroff.negate");
		
		var ptrType = (PointerType)v.Type;
		
		var elementType = ptrType.BaseType == NativeSymbols.Void
			? LLVMTypeRef.Int8
			: MapTypeSymbol(ptrType.BaseType);
		
		if (ptrType.BaseType != NativeSymbols.Void)
			return builder.BuildGEP2(elementType, ptr, new[] { offset }, "ptroff");
		
		var bytePtrType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
		var bytePtr = builder.BuildBitCast(ptr, bytePtrType, "voidptr.as.byteptr");
		var result = builder.BuildGEP2(elementType, bytePtr, new[] { offset }, "ptroff");
		return builder.BuildBitCast(result, MapTypeSymbol(ptrType), "byteptr.as.voidptr");
	}
	
	private LLVMValueRef EmitPointerDifference(PointerDifferenceValue v, LLVMBuilderRef builder)
	{
		var isize = MapTypeSymbol(NativeSymbols.IntSize);
		var left = builder.BuildPtrToInt(EmitValue(v.Left, builder), isize, "ptrdiff.left");
		var right = builder.BuildPtrToInt(EmitValue(v.Right, builder), isize, "ptrdiff.right");
		
		var byteDiff = builder.BuildSub(left, right, "ptrdiff.bytes");
		
		if (v.PointerType.BaseType == NativeSymbols.Void)
			return byteDiff;
		
		var elementBits = _typePool.SizeTable.GetSize(v.PointerType.BaseType).CountBits(_pointerSize * 8);
		var elementBytes = (elementBits + 7) / 8;
		
		if (elementBytes == 0)
			throw new InvalidOperationException("Cannot subtract pointers to zero-sized type " +
			                                    v.PointerType.BaseType.Name);
		
		var elementSize = EmitSizeConstant(elementBytes, false);
		return builder.BuildSDiv(byteDiff, elementSize, "ptrdiff.typed");
	}
	
	private LLVMValueRef EmitHeap(HeapValue v, LLVMBuilderRef builder)
	{
		var ptrType = (PointerType)v.Type;
		var baseType = MapTypeSymbol(ptrType.BaseType);
		var malloc = builder.BuildMalloc(baseType);
		
		if (v.Initializer is not UndefValue)
			builder.BuildStore(EmitValue(v.Initializer, builder), malloc);
		
		return malloc;
	}
	
	private LLVMValueRef EmitZero(ZeroValue v) => LLVMValueRef.CreateConstNull(MapTypeSymbol(v.Type));
	
	private LLVMValueRef EmitEnumValue(EnumValue v, LLVMBuilderRef builder)
	{
		var enumType = MapTypeSymbol(v.Type);
		var tagType = MapTypeSymbol(TypePool.GetTagType((EnumSymbol)v.Type));
		var tag = LLVMValueRef.CreateConstInt(tagType, (ulong)v.Case.Index);
		if (v.Payload.IsEmpty)
			return builder.BuildInsertValue(LLVMValueRef.CreateConstNull(enumType), tag, 0, v.Case.Name);
		
		var slot = BuildEntryAlloca(builder, enumType, v.Case.Name);
		builder.BuildStore(LLVMValueRef.CreateConstNull(enumType), slot);
		builder.BuildStore(tag, builder.BuildStructGEP2(enumType, slot, 0, "tag.addr"));
		
		var payloadType = GetPayloadType(v.Case);
		var payload = builder.BuildStructGEP2(enumType, slot, 1, "payload");
		for (var i = 0; i < v.Payload.Length; i++)
		{
			var value = EmitValue(v.Payload[i], builder);
			builder.BuildStore(value, builder.BuildStructGEP2(payloadType, payload, (uint)i, v.Case.Fields[i].Name));
		}
		
		return builder.BuildLoad2(enumType, slot, v.Case.Name);
	}
	
	private LLVMValueRef EmitEnumTag(EnumTagValue v, LLVMBuilderRef builder)
	{
		if (!IsAddressable(v.Target))
			return builder.BuildExtractValue(EmitValue(v.Target, builder), 0, "tag");
		
		var enumType = MapTypeSymbol(v.Target.Type);
		var address = builder.BuildStructGEP2(enumType, EmitAddress(v.Target, builder), 0, "tag.addr");
		return builder.BuildLoad2(MapTypeSymbol(v.Type), address, "tag");
	}
	
	private LLVMValueRef EmitEnumPayload(EnumPayloadValue v, LLVMBuilderRef builder)
	{
		var enumType = MapTypeSymbol(v.Target.Type);
		LLVMValueRef address;
		if (IsAddressable(v.Target))
		{
			address = EmitAddress(v.Target, builder);
		}
		else
		{
			address = BuildEntryAlloca(builder, enumType, "scrutinee");
			builder.BuildStore(EmitValue(v.Target, builder), address);
		}
		
		var name = v.Case.Fields[v.Index].Name;
		var payload = builder.BuildStructGEP2(enumType, address, 1, "payload");
		var field = builder.BuildStructGEP2(GetPayloadType(v.Case), payload, (uint)v.Index, name + ".addr");
		return builder.BuildLoad2(MapTypeSymbol(v.Type), field, name);
	}
	
	private LLVMValueRef EmitConversion(ConversionValue v, LLVMBuilderRef builder) => v.Conversion switch
	{
		IdentityConversion => EmitValue(v.Source, builder),
		IntegerConversion c => EmitIntegerConversion(c, EmitValue(v.Source, builder), builder),
		FloatConversion c => EmitFloatConversion(c, EmitValue(v.Source, builder), builder),
		NativeConversion c => EmitNativeConversion(c, v, builder),
		FreeConversion => EmitValue(v.Source, builder),
		FunctionConversion c => EmitValue(new CallValue(c.Function, [v.Source], v.SourceLocation), builder),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitNativeConversion(NativeConversion c, ConversionValue v, LLVMBuilderRef builder)
	{
		var source = EmitValue(v.Source, builder);
		
		// Arrays
		if (c.From is ArrayType arrayType)
		{
			var llvmArrayType = MapTypeSymbol(arrayType);
			
			// Array -> Buffer
			if (c.To is BufferType bufferType)
			{
				// TODO This will probably crash for an empty array (and the InBounds would be incorrect?)
				var lengthValue = EmitSizeConstant(arrayType.Length, true);
				
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				LLVMValueRef dataPtr;
				if (IsAddressable(v.Source))
				{
					var arrayPtr = EmitAddress(v.Source, builder);
					dataPtr = builder.BuildInBoundsGEP2(llvmArrayType, arrayPtr, new[] { zero, zero }, "data");
				}
				else
				{
					// Trying to convert literal into span, need to implicitly stack-allocate literal array
					var arrayPtr = BuildEntryAlloca(builder, llvmArrayType, "array");
					builder.BuildStore(source, arrayPtr);
					dataPtr = builder.BuildInBoundsGEP2(llvmArrayType, arrayPtr, new[] { zero, zero }, "data");
				}
				
				var llvmSpanType = MapTypeSymbol(bufferType);
				var bufferValue = llvmSpanType.Undef;
				bufferValue = builder.BuildInsertValue(bufferValue, lengthValue, 0, "buffer.length");
				bufferValue = builder.BuildInsertValue(bufferValue, dataPtr, 1, "buffer.ptr");
				return bufferValue;
			}
			
			// Array -> Span
			if (c.To is SpanType spanType)
			{
				// TODO This will probably crash for an empty array (and the InBounds would be incorrect?)
				var lengthValue = EmitSizeConstant(arrayType.Length, true);
				
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				LLVMValueRef dataPtr;
				if (IsAddressable(v.Source))
				{
					var arrayPtr = EmitAddress(v.Source, builder);
					dataPtr = builder.BuildInBoundsGEP2(llvmArrayType, arrayPtr, new[] { zero, zero }, "data");
				}
				else
				{
					// Trying to convert literal into span, need to implicitly stack-allocate literal array
					var arrayPtr = BuildEntryAlloca(builder, llvmArrayType, "array");
					builder.BuildStore(source, arrayPtr);
					dataPtr = builder.BuildInBoundsGEP2(llvmArrayType, arrayPtr, new[] { zero, zero }, "data");
				}
				
				var llvmSpanType = MapTypeSymbol(spanType);
				var spanValue = llvmSpanType.Undef;
				spanValue = builder.BuildInsertValue(spanValue, lengthValue, 0, "span.length");
				spanValue = builder.BuildInsertValue(spanValue, dataPtr, 1, "span.ptr");
				return spanValue;
			}
			
			// Array -> View
			if (c.To is ViewType viewType)
			{
				// TODO This will probably crash for an empty array (and the InBounds would be incorrect?)
				var lengthValue = EmitSizeConstant(arrayType.Length, true);
				
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				LLVMValueRef dataPtr;
				if (IsAddressable(v.Source))
				{
					var arrayPtr = EmitAddress(v.Source, builder);
					dataPtr = builder.BuildInBoundsGEP2(llvmArrayType, arrayPtr, new[] { zero, zero }, "data");
				}
				else
				{
					// Trying to convert literal into span, need to implicitly stack-allocate literal array
					var arrayPtr = BuildEntryAlloca(builder, llvmArrayType, "array");
					builder.BuildStore(source, arrayPtr);
					dataPtr = builder.BuildInBoundsGEP2(llvmArrayType, arrayPtr, new[] { zero, zero }, "data");
				}
				
				var llvmViewType = MapTypeSymbol(viewType);
				var viewValue = llvmViewType.Undef;
				viewValue = builder.BuildInsertValue(viewValue, lengthValue, 0, "view.length");
				viewValue = builder.BuildInsertValue(viewValue, dataPtr, 1, "view.ptr");
				return viewValue;
			}
		}
		
		// Pointers
		switch (c)
		{
			case { From: PointerType, To: PointerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildBitCast(source, destType, "ptrcast");
			}
			
			case { From: PointerType, To: IntegerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildPtrToInt(source, destType, "ptrtoint");
			}
			
			case { From: IntegerType, To: PointerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildIntToPtr(source, destType, "inttoptr");
			}
			
			case { From: PrimitiveType { Kind: PrimitiveTypeKind.CStr }, To: IntegerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildPtrToInt(source, destType, "cstrtoint");
			}
			
			case { From: IntegerType, To: PrimitiveType { Kind: PrimitiveTypeKind.CStr } }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildIntToPtr(source, destType, "inttocstr");
			}
			
			case { From: PrimitiveType { Kind: PrimitiveTypeKind.Bool }, To: IntegerType }:
			{
				var destType = MapTypeSymbol(c.To);
				return builder.BuildZExt(source, destType, "booltoint");
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private bool IsAddressable(Value value) => value switch
	{
		VariableValue => true,
		GlobalValue => true,
		IndexerValue { Target.Type: SpanType or ViewType } => true,
		IndexerValue v => IsAddressable(v.Target),
		AccessValue v => IsAddressable(v.Target),
		UnaryOpValue { Op: UnaryOperation.Dereference } => true,
		_ => false
	};
	
	private LLVMValueRef EmitIntegerConversion(IntegerConversion c, LLVMValueRef source, LLVMBuilderRef builder) =>
		ResizeInteger(source, MapTypeSymbol(c.To), c.FromSigned, builder);
	
	private static LLVMValueRef ResizeInteger(LLVMValueRef source, LLVMTypeRef destType, bool isSigned,
		LLVMBuilderRef builder)
	{
		var srcType = source.TypeOf;
		
		if (destType.IntWidth == srcType.IntWidth)
			return source;
		
		if (destType.IntWidth < srcType.IntWidth)
			return builder.BuildTrunc(source, destType);
		
		return isSigned
			? builder.BuildSExt(source, destType)
			: builder.BuildZExt(source, destType);
	}
	
	private LLVMValueRef EmitFloatConversion(FloatConversion c, LLVMValueRef source, LLVMBuilderRef builder)
	{
		var destType = MapTypeSymbol(c.To);
		return (c.From, c.To) switch
		{
			(FloatType, FloatType) when CountBits(c.To) > CountBits(c.From) => builder.BuildFPExt(source, destType),
			(FloatType, FloatType) => builder.BuildFPTrunc(source, destType),
			(IntegerType { IsSigned: true }, _) => builder.BuildSIToFP(source, destType),
			(IntegerType, _) => builder.BuildUIToFP(source, destType),
			(_, IntegerType { IsSigned: var signed }) => BuildSaturatingFloatToInt(source, destType, signed, builder),
			_ => throw new InvalidOperationException()
		};
	}
	
	private LLVMValueRef BuildSaturatingFloatToInt(LLVMValueRef source, LLVMTypeRef destType, bool isSigned,
		LLVMBuilderRef builder)
	{
		var sourceName = source.TypeOf.Kind == LLVMTypeKind.LLVMFloatTypeKind ? "f32" : "f64";
		var name = $"llvm.{(isSigned ? "fptosi" : "fptoui")}.sat.i{destType.IntWidth}.{sourceName}";
		var functionType = LLVMTypeRef.CreateFunction(destType, [source.TypeOf]);
		return builder.BuildCall2(functionType, GetIntrinsic(name, functionType), [source]);
	}
	
	private LLVMValueRef GetIntrinsic(string name, LLVMTypeRef functionType)
	{
		var function = currentModule.GetNamedFunction(name);
		return function.Handle == IntPtr.Zero ? currentModule.AddFunction(name, functionType) : function;
	}
	
	private uint CountBits(TypeSymbol type) => _typePool.SizeTable.GetSize(type).CountBits(_pointerSize * 8);
	
	private LLVMValueRef EmitBinaryOp(BinOpValue v, LLVMBuilderRef builder)
	{
		if (v.Op is BinaryOperation.ShiftLeft or BinaryOperation.ShiftRight)
			return EmitShift(v, builder);
		
		if (v.Op is BinaryOperation.RotateLeft or BinaryOperation.RotateRight)
			return EmitRotate(v, builder);
		
		if (v.Left.Type is FloatType)
			return EmitFloatBinaryOp(v, builder);
		
		var left = EmitValue(v.Left, builder);
		var right = EmitValue(v.Right, builder);
		var signed = v.Left.Type is IntegerType { IsSigned: true };
		
		if (v.Left.Type is IntegerType { IsSigned: var leftSigned } &&
		    v.Right.Type is IntegerType { IsSigned: var rightSigned } &&
		    leftSigned != rightSigned)
		{
			var wide = LLVMTypeRef.CreateInt(Math.Max(left.TypeOf.IntWidth, right.TypeOf.IntWidth) + 1);
			left = leftSigned ? builder.BuildSExt(left, wide) : builder.BuildZExt(left, wide);
			right = rightSigned ? builder.BuildSExt(right, wide) : builder.BuildZExt(right, wide);
			signed = true;
		}
		
		return v switch
		{
			{ IsConstant: true, Op: BinaryOperation.Addition } =>
				LLVMValueRef.CreateConstAdd(left, right),
			{ Op: BinaryOperation.Addition } =>
				builder.BuildAdd(left, right),
			
			{ IsConstant: true, Op: BinaryOperation.Subtraction } =>
				LLVMValueRef.CreateConstSub(left, right),
			{ Op: BinaryOperation.Subtraction } =>
				builder.BuildSub(left, right),
			
			{ IsConstant: true, Op: BinaryOperation.Multiplication } =>
				LLVMValueRef.CreateConstMul(left, right),
			{ Op: BinaryOperation.Multiplication } =>
				builder.BuildMul(left, right),
			
			{ Op: BinaryOperation.Division or BinaryOperation.Modulo } =>
				EmitIntegerDivision(v.Op, left, right, signed, builder),
			
			{ Op: BinaryOperation.Greater } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntUGT, left, right),
			
			{ Op: BinaryOperation.GreaterEqual } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, left, right),
			
			{ Op: BinaryOperation.Less } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, left, right),
			
			{ Op: BinaryOperation.LessEqual } => signed
				? builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right)
				: builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, left, right),
			
			{ Op: BinaryOperation.Equal } =>
				builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right), // TODO Check type for correct operation
			
			{ Op: BinaryOperation.NotEqual } =>
				builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right), // TODO Check type for correct operation
			
			{ Op: BinaryOperation.BitwiseAnd } =>
				builder.BuildAnd(left, right),
			
			{ Op: BinaryOperation.BitwiseOr } =>
				builder.BuildOr(left, right),
			
			{ Op: BinaryOperation.BitwiseXor } =>
				builder.BuildXor(left, right),
			
			{ Op: BinaryOperation.LogicalAnd } =>
				builder.BuildAnd(left, right),
			
			{ Op: BinaryOperation.LogicalOr } =>
				builder.BuildOr(left, right),
			
			_ => throw new InvalidOperationException()
		};
	}
	
	private LLVMValueRef EmitIntegerDivision(BinaryOperation op, LLVMValueRef left, LLVMValueRef right, bool signed,
		LLVMBuilderRef builder)
	{
		var type = left.TypeOf;
		var one = LLVMValueRef.CreateConstInt(type, 1);
		TrapIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstNull(type)), builder);
		
		if (!signed)
		{
			return op == BinaryOperation.Division
				? builder.BuildUDiv(left, right)
				: builder.BuildURem(left, right);
		}
		
		var isMinusOne = builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstAllOnes(type));
		if (isMinusOne.Handle == _false.Handle)
		{
			return op == BinaryOperation.Division
				? builder.BuildSDiv(left, right)
				: builder.BuildSRem(left, right);
		}
		
		if (op == BinaryOperation.Modulo)
			return builder.BuildSRem(left, builder.BuildSelect(isMinusOne, one, right));
		
		var minimum = builder.BuildShl(one, LLVMValueRef.CreateConstInt(type, type.IntWidth - 1));
		TrapIf(builder.BuildAnd(isMinusOne, builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, minimum)), builder);
		return builder.BuildSDiv(left, right);
	}
	
	private void TrapIf(LLVMValueRef condition, LLVMBuilderRef builder)
	{
		if (condition.Handle == _false.Handle)
			return;
		
		var function = builder.InsertBlock.Parent;
		var trapBlock = function.AppendBasicBlock("trap");
		var continueBlock = function.AppendBasicBlock("no_trap");
		builder.BuildCondBr(condition, trapBlock, continueBlock);
		
		builder.PositionAtEnd(trapBlock);
		var trapType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, []);
		builder.BuildCall2(trapType, GetIntrinsic("llvm.trap", trapType), []);
		builder.BuildUnreachable();
		
		builder.PositionAtEnd(continueBlock);
	}
	
	private LLVMValueRef EmitFloatBinaryOp(BinOpValue v, LLVMBuilderRef builder)
	{
		var left = EmitValue(v.Left, builder);
		var right = EmitValue(v.Right, builder);
		return v.Op switch
		{
			BinaryOperation.Addition => builder.BuildFAdd(left, right),
			BinaryOperation.Subtraction => builder.BuildFSub(left, right),
			BinaryOperation.Multiplication => builder.BuildFMul(left, right),
			BinaryOperation.Division => builder.BuildFDiv(left, right),
			BinaryOperation.Modulo => builder.BuildFRem(left, right),
			BinaryOperation.Equal => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
			BinaryOperation.NotEqual => builder.BuildFCmp(LLVMRealPredicate.LLVMRealUNE, left, right),
			BinaryOperation.Greater => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
			BinaryOperation.GreaterEqual => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
			BinaryOperation.Less => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
			BinaryOperation.LessEqual => builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
			_ => throw new InvalidOperationException()
		};
	}
	
	private LLVMValueRef EmitShift(BinOpValue v, LLVMBuilderRef builder)
	{
		var value = EmitValue(v.Left, builder);
		var amount = EmitValue(v.Right, builder);
		var type = value.TypeOf;
		var bits = type.IntWidth;
		var count = ResizeInteger(amount, type, v.Right.Type is IntegerType { IsSigned: true }, builder);
		
		var checkedAmount = amount.TypeOf.IntWidth > bits ? amount : count;
		var inRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, checkedAmount,
			LLVMValueRef.CreateConstInt(checkedAmount.TypeOf, bits));
		
		var isSigned = v.Left.Type is IntegerType { IsSigned: true };
		var shifted = v.Op switch
		{
			BinaryOperation.ShiftLeft => builder.BuildShl(value, count),
			_ when isSigned => builder.BuildAShr(value, count),
			_ => builder.BuildLShr(value, count)
		};
		
		var shiftedOut = v.Op == BinaryOperation.ShiftRight && isSigned
			? builder.BuildAShr(value, LLVMValueRef.CreateConstInt(type, bits - 1))
			: LLVMValueRef.CreateConstNull(type);
		
		return builder.BuildSelect(inRange, shifted, shiftedOut);
	}
	
	private LLVMValueRef EmitRotate(BinOpValue v, LLVMBuilderRef builder)
	{
		var value = EmitValue(v.Left, builder);
		var type = value.TypeOf;
		var amount = ResizeInteger(EmitValue(v.Right, builder), type,
			v.Right.Type is IntegerType { IsSigned: true }, builder);
		
		var mask = LLVMValueRef.CreateConstInt(type, type.IntWidth - 1);
		var forward = builder.BuildAnd(amount, mask);
		var backward = builder.BuildAnd(builder.BuildNeg(amount), mask);
		var (leftCount, rightCount) = v.Op == BinaryOperation.RotateLeft
			? (forward, backward)
			: (backward, forward);
		
		return builder.BuildOr(builder.BuildShl(value, leftCount), builder.BuildLShr(value, rightCount));
	}
	
	private LLVMValueRef EmitUnaryOp(UnaryOpValue v, LLVMBuilderRef builder) => v switch
	{
		{ Op: UnaryOperation.Identity } => EmitValue(v.Operand, builder),
		{ Op: UnaryOperation.Negation, Operand.Type: FloatType } => builder.BuildFNeg(EmitValue(v.Operand, builder)),
		{ IsConstant: true, Op: UnaryOperation.Negation } => LLVMValueRef.CreateConstNeg(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.Negation } => builder.BuildNeg(EmitValue(v.Operand, builder)),
		
		{ IsConstant: true, Op: UnaryOperation.BitwiseNot } =>
			LLVMValueRef.CreateConstNot(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.BitwiseNot } => builder.BuildNot(EmitValue(v.Operand, builder)),
		
		{ IsConstant: true, Op: UnaryOperation.LogicalNot } =>
			LLVMValueRef.CreateConstNot(EmitValue(v.Operand, builder)),
		{ Op: UnaryOperation.LogicalNot } => builder.BuildNot(EmitValue(v.Operand, builder)),
		
		{ Op: UnaryOperation.AddressOf } => EmitAddress(v.Operand, builder),
		{ Op: UnaryOperation.Dereference } => builder.BuildLoad2(MapTypeSymbol(v.Type), EmitValue(v.Operand, builder)),
		
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitIndexer(IndexerValue v, LLVMBuilderRef builder)
	{
		var (elemPtr, elemType) = EmitIndexerAddress(v, builder);
		return builder.BuildLoad2(elemType, elemPtr, "elem");
	}
	
	private (LLVMValueRef Ptr, LLVMTypeRef ElemType) EmitIndexerAddress(IndexerValue v, LLVMBuilderRef builder)
	{
		var elemType = MapTypeSymbol(v.Type);
		
		// TODO Switch to BuildInBoundsGEP2 once compiler-generated bounds checks are implemented
		switch (v.Target.Type)
		{
			case SpanType:
			{
				var target = EmitValue(v.Target, builder);
				var index = EmitValue(v.Index, builder);
				var dataPtr = builder.BuildExtractValue(target, 1, "dataptr");
				var elemPtr = builder.BuildGEP2(elemType, dataPtr, new[] { index }, "elemptr");
				return (elemPtr, elemType);
			}
			
			case ViewType:
			{
				var target = EmitValue(v.Target, builder);
				var index = EmitValue(v.Index, builder);
				var dataPtr = builder.BuildExtractValue(target, 1, "dataptr");
				var elemPtr = builder.BuildGEP2(elemType, dataPtr, new[] { index }, "elemptr");
				return (elemPtr, elemType);
			}
			
			case ArrayType a:
			{
				var arrayType = MapTypeSymbol(a);
				LLVMValueRef arrayPtr;
				if (IsAddressable(v.Target))
				{
					arrayPtr = EmitAddress(v.Target, builder);
				}
				else
				{
					arrayPtr = BuildEntryAlloca(builder, arrayType, "array");
					builder.BuildStore(EmitValue(v.Target, builder), arrayPtr);
				}
				
				var index = EmitValue(v.Index, builder);
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				var elemPtr = builder.BuildGEP2(arrayType, arrayPtr, new[] { zero, index }, "elemptr");
				return (elemPtr, elemType);
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private LLVMValueRef EmitAccessAddress(AccessValue v, LLVMBuilderRef builder)
	{
		var targetPtr = EmitAddress(v.Target, builder);
		var targetType = MapTypeSymbol(v.Target.Type);
		var fieldIndex = (uint)_typePool.GetFieldIndex(v.Target.Type, v.Member);
		return builder.BuildStructGEP2(targetType, targetPtr, fieldIndex, v.Member.Name + ".addr");
	}
	
	private LLVMValueRef EmitAccessValue(AccessValue v, LLVMBuilderRef builder)
	{
		// Intrinsic properties
		switch (v.Member)
		{
			case PropertySymbol { Getter: NativeAccessor getter }:
				switch (getter.Intrinsic)
				{
					case NativeMemberIntrinsic.ArrayLength when v.Target.Type is ArrayType arrayType:
						return EmitSizeConstant(arrayType.Length, true);
				}
				
				break;
		}
		
		// If target has an address, GEP + load only the field
		if (IsAddressable(v.Target))
		{
			var fieldAddress = EmitAccessAddress(v, builder);
			var fieldType = MapTypeSymbol(_typePool.GetTypeOfMember((TypedMemberSymbol)v.Member));
			return builder.BuildLoad2(fieldType, fieldAddress, v.Member.Name);
		}
		
		// TODO Fields could have been reordered to pack them
		// TODO Also, GetFieldIndex is O(n), would probably want to cache the final indices in another dictionary
		var target = EmitValue(v.Target, builder);
		var fieldIndex = (uint)_typePool.GetFieldIndex(v.Target.Type, v.Member);
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
		if (v.Left is GlobalValue global)
			EmitGlobalStore(global.Global, right, builder);
		else
			builder.BuildStore(right, EmitAddress(v.Left, builder));
		
		return right;
	}
	
	private LLVMValueRef EmitAddress(Value value, LLVMBuilderRef builder) => value switch
	{
		VariableValue v => _varMap[v.Variable],
		GlobalValue v => GetGlobal(v.Global),
		IndexerValue v => EmitIndexerAddress(v, builder).Ptr,
		AccessValue v => EmitAccessAddress(v, builder),
		UnaryOpValue { Op: UnaryOperation.Dereference } v => EmitValue(v.Operand, builder),
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
				
				case PrimitiveTypeKind.Char:
					return LLVMValueRef.CreateConstInt(type, (uint)value);
				
				case PrimitiveTypeKind.Float32:
				case PrimitiveTypeKind.Float64:
					return LLVMValueRef.CreateConstReal(type, (double)value);
				
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
	
	private void DefineGlobal(GlobalInfo info)
	{
		var initializer = info.Symbol.IsMutable && info.Value is BoolConstant flag
			? LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, flag.Value ? 1uL : 0uL)
			: EmitStaticConstant(info.Value!);
		
		var global = currentModule.AddGlobal(initializer.TypeOf, info.MangledName);
		global.Initializer = initializer;
		
		global.IsGlobalConstant = !info.Symbol.IsMutable;
		if (info.Symbol.Visibility == Visibility.Public)
		{
			global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
			global.Linkage = LLVMLinkage.LLVMDLLExportLinkage;
		}
		else
		{
			global.Linkage = LLVMLinkage.LLVMInternalLinkage;
		}
		
		_globalMap[info.Symbol] = global;
	}
	
	private LLVMValueRef GetGlobal(GlobalInfo info)
	{
		if (_globalMap.TryGetValue(info.Symbol, out var existing))
			return existing;
		
		var global = currentModule.AddGlobal(GetGlobalStorageType(info), info.MangledName);
		global.IsGlobalConstant = !info.Symbol.IsMutable;
		global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
		global.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		_globalMap[info.Symbol] = global;
		return global;
	}
	
	private LLVMTypeRef GetGlobalStorageType(GlobalInfo info) =>
		info.Symbol.IsMutable && info.Type == NativeSymbols.Bool ? LLVMTypeRef.Int8 : MapTypeSymbol(info.Type);
	
	private LLVMValueRef EmitGlobalLoad(GlobalInfo info, LLVMBuilderRef builder)
	{
		var storageType = GetGlobalStorageType(info);
		var load = builder.BuildLoad2(storageType, GetGlobal(info), info.Symbol.Name);
		if (!info.Symbol.IsMutable)
			return load;
		
		MakeMonotonic(load);
		load.Alignment = _targetData.ABIAlignmentOfType(storageType);
		return info.Type == NativeSymbols.Bool ? builder.BuildTrunc(load, LLVMTypeRef.Int1) : load;
	}
	
	private void EmitGlobalStore(GlobalInfo info, LLVMValueRef value, LLVMBuilderRef builder)
	{
		var storageType = GetGlobalStorageType(info);
		if (info.Type == NativeSymbols.Bool)
			value = builder.BuildZExt(value, storageType);
		
		var store = builder.BuildStore(value, GetGlobal(info));
		MakeMonotonic(store);
		store.Alignment = _targetData.ABIAlignmentOfType(storageType);
	}
	
	private static void MakeMonotonic(LLVMValueRef instruction) =>
		LLVM.SetOrdering((LLVMOpaqueValue*)instruction.Handle, LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic);
	
	private LLVMValueRef GetFunctionValue(FunctionInfo function)
	{
		if (_funMap.TryGetValue(function, out var existing))
			return existing.FunctionValue;
		
		var value = CreateFunction(currentModule, function).FunctionValue;
		if (function.Symbol.Kind == FunctionKind.External)
		{
			if (function.Origin is { } origin)
				_externalLibraries.Add(origin);
			
			value.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else
		{
			value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			value.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		
		return value;
	}
	
	private LLVMValueRef EmitStaticConstant(Constant constant) => constant switch
	{
		IntegerConstant c => EmitIntegerConstant(c),
		FloatConstant c => LLVMValueRef.CreateConstReal(MapTypeSymbol(c.Type), c.Value),
		BoolConstant c => c.Value ? _true : _false,
		NullConstant c => LLVMValueRef.CreateConstNull(MapTypeSymbol(c.Type)),
		StringConstant c => EmitConstant(new ConstantValue(c.Type, c.Value)),
		RecordConstant c => EmitStructConstant(MapTypeSymbol(c.Type), [..c.Fields.Select(EmitStaticConstant)]),
		ArrayConstant c => EmitArrayConstant(MapTypeSymbol(c.ArrayType.ElementType),
			[..c.Elements.Select(EmitStaticConstant)]),
		EnumConstant c => EmitEnumConstant(c),
		FunctionConstant c => GetFunctionValue(c.Function),
		ZeroConstant c => LLVMValueRef.CreateConstNull(MapTypeSymbol(c.Type)),
		_ => throw new InvalidOperationException()
	};
	
	private static LLVMValueRef EmitStructConstant(LLVMTypeRef type, LLVMValueRef[] fields) =>
		fields.Select((field, i) => field.TypeOf == type.StructGetTypeAtIndex((uint)i)).All(static same => same)
			? LLVMValueRef.CreateConstNamedStruct(type, fields)
			: LLVMValueRef.CreateConstStruct(fields, false);
	
	private static LLVMValueRef EmitArrayConstant(LLVMTypeRef elementType, LLVMValueRef[] elements) =>
		elements.All(element => element.TypeOf == elementType)
			? LLVMValueRef.CreateConstArray(elementType, elements)
			: LLVMValueRef.CreateConstStruct(elements, false);
	
	private LLVMValueRef EmitEnumConstant(EnumConstant constant)
	{
		var enumType = MapTypeSymbol(constant.Type);
		var tag = EmitIntegerConstant(new IntegerConstant(TypePool.GetTagType((EnumSymbol)constant.Type),
			constant.Case.Index));
		
		if (enumType.StructElementTypesCount == 1)
			return LLVMValueRef.CreateConstNamedStruct(enumType, [tag]);
		
		var area = enumType.StructGetTypeAtIndex(1);
		if (constant.Payload.IsEmpty)
			return LLVMValueRef.CreateConstNamedStruct(enumType, [tag, LLVMValueRef.CreateConstNull(area)]);
		
		var payload = LLVMValueRef.CreateConstStruct([..constant.Payload.Select(EmitStaticConstant)], false);
		var alignment = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreateArray(area.ElementType, 0));
		var tail = _targetData.ABISizeOfType(area) - _targetData.ABISizeOfType(payload.TypeOf);
		if (tail == 0)
			return LLVMValueRef.CreateConstStruct([tag, alignment, payload], false);
		
		var padding = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)tail));
		return LLVMValueRef.CreateConstStruct([tag, alignment, payload, padding], false);
	}
	
	private LLVMValueRef EmitIntegerConstant(IntegerConstant constant)
	{
		var type = MapTypeSymbol(constant.Type);
		var bits = (int)type.IntWidth;
		var pattern = constant.Value.Sign < 0 ? constant.Value + (BigInteger.One << bits) : constant.Value;
		var words = new ulong[GetWordCount(bits)];
		for (var i = 0; i < words.Length; i++)
			words[i] = (ulong)((pattern >> (BitsPerWord * i)) & ulong.MaxValue);
		
		return LLVMValueRef.CreateConstIntOfArbitraryPrecision(type, words);
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
		_targetData.Dispose();
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
	TargetConfig? TargetConfig, // if null, compiles for current platform
	OptimizeMode OptimizeMode
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

public enum OptimizeMode
{
	Debug,
	Release
}

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