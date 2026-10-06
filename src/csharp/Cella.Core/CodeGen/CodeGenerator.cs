using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.CodeGen.Extensions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
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
	private readonly Dictionary<TypeSymbol, LLVMValueRef> _dropGlue = [];
	private readonly Dictionary<VariableInfo, LLVMValueRef> _varMap = [];
	private readonly Dictionary<GlobalSymbol, LLVMValueRef> _globalMap = [];
	private readonly LLVMValueRef _true = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1uL);
	private readonly LLVMValueRef _false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0uL);
	private readonly HashSet<string> _externalLibraries = [];
	private readonly Dictionary<byte[], LLVMValueRef> _stringPool = new(ByteArrayComparer.Instance);
	private readonly HashSet<FunctionSymbol> _sharedFunctions;
	private LLVMModuleRef currentModule;
	private LLVMFunctionInfo currentFunction;
	private LLVMValueRef panicFunction;
	
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
		_sharedFunctions = assemblySymbol.SignatureTable.Globals.Values
			.Where(static global => global.Symbol.Visibility is Visibility.Project or Visibility.Public)
			.SelectMany(static global => FindFunctions(global.Value))
			.ToHashSet();
	}
	
	private static IEnumerable<FunctionSymbol> FindFunctions(Constant? constant) => constant switch
	{
		FunctionConstant c => [c.Function.Symbol],
		RecordConstant c => c.Fields.SelectMany(FindFunctions),
		ArrayConstant c => c.Elements.SelectMany(FindFunctions),
		EnumConstant c => c.Payload.SelectMany(FindFunctions),
		_ => []
	};
	
	private bool IsObjectLocal(FunctionSymbol function) =>
		function.Visibility is Visibility.Private or Visibility.Module && function.Kind != FunctionKind.Destructor &&
		!_sharedFunctions.Contains(function);
	
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
			_dropGlue.Clear();
			panicFunction = default;
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
	
	private static LLVMTypeRef OpaquePointer => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u);
	private static LLVMTypeRef DropGlueType => LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [OpaquePointer]);
	
	private static LLVMTypeRef PanicType =>
		LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [OpaquePointer, LLVMTypeRef.Int32]);
	
	private LLVMTypeRef MapParameterType(TypeSymbol type, ParameterMode mode) =>
		_typePool.PassesByPointer(type, mode) ? OpaquePointer : MapTypeSymbol(type);
	
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
			
			case FunctionType functionType:
			{
				var llvmFunctionType = functionType.IsExternal
					? OpaquePointer
					: LLVMTypeRef.CreateStruct([OpaquePointer, OpaquePointer], false);
				
				_typeMap[symbol] = llvmFunctionType;
				return llvmFunctionType;
			}
			
			case PointerType ptrType:
			{
				var baseType = MapTypeSymbol(ptrType.BaseType);
				var llvmPtrType = LLVMTypeRef.CreatePointer(baseType, 0u);
				_typeMap[symbol] = llvmPtrType;
				return llvmPtrType;
			}
			
			case BorrowType:
				_typeMap[symbol] = OpaquePointer;
				return OpaquePointer;
			
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
				GetFunctionValue(function);
			
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
				else if (IsObjectLocal(function.Info.Symbol))
				{
					llvmFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
				}
				else
				{
					llvmFunction.Visibility = LLVMVisibility.LLVMHiddenVisibility;
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
		
		var tagType = MapTypeSymbol(_typePool.GetTagType(enumType));
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
			paramLlvmTypes[i] = MapParameterType(paramTypes[i], signature.GetMode(i));
		
		CSignature? cSignature = symbol.IsExternal
			? _cAbi.Classify(paramLlvmTypes, returnType)
			: null;
		
		var functionType = cSignature?.CreateFunctionType(signature.IsVariadic)
		                   ?? LLVMTypeRef.CreateFunction(returnType, paramLlvmTypes, signature.IsVariadic);
		
		var name = function.MangledName ?? symbol.Name;
		var declared = symbol.Kind == FunctionKind.External ? llvmModule.GetNamedFunction(name) : default;
		var functionValue = declared.Handle != IntPtr.Zero ? declared : llvmModule.AddFunction(name, functionType);
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
					if (_typePool.PassesByPointer(paramType, function.Info.Signature.GetMode(p)))
					{
						_varMap[paramInfo] = paramLlvmValue;
						continue;
					}
					
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
			
			case DropInstruction { Guard: { } guard } i:
			{
				var function = builder.InsertBlock.Parent;
				var dropBlock = function.AppendBasicBlock("drop");
				var doneBlock = function.AppendBasicBlock("drop_done");
				builder.BuildCondBr(EmitValue(guard, builder), dropBlock, doneBlock);
				builder.PositionAtEnd(dropBlock);
				EmitDrop(i.Value, builder);
				builder.BuildBr(doneBlock);
				builder.PositionAtEnd(doneBlock);
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
		
		LLVMValueRef address;
		if (IsAddressable(value))
		{
			address = EmitAddress(value, builder);
		}
		else
		{
			address = BuildEntryAlloca(builder, MapTypeSymbol(value.Type), "dropped");
			builder.BuildStore(EmitValue(value, builder), address);
		}
		
		EmitDropCall(value.Type, address, builder);
	}
	
	private void EmitDropCall(TypeSymbol type, LLVMValueRef address, LLVMBuilderRef builder) =>
		builder.BuildCall2(DropGlueType, GetDropGlue(type), [address]);
	
	private LLVMValueRef GetDropGlue(TypeSymbol type)
	{
		if (_dropGlue.TryGetValue(type, out var glue))
			return glue;
		
		glue = currentModule.AddFunction($"drop${type.Name}", DropGlueType);
		glue.Linkage = LLVMLinkage.LLVMInternalLinkage;
		_dropGlue[type] = glue;
		
		using var builder = currentModule.Context.CreateBuilder();
		builder.PositionAtEnd(glue.AppendBasicBlock("entry"));
		var address = glue.GetParam(0);
		switch (type)
		{
			case RecordSymbol record:
				EmitRecordDropGlue(record, address, builder);
				break;
			
			case EnumSymbol enumType:
				EmitEnumDropGlue(enumType, address, builder);
				break;
			
			case ArrayType array:
				EmitArrayDropGlue(array, address, builder);
				break;
		}
		
		builder.BuildRetVoid();
		return glue;
	}
	
	private void EmitRecordDropGlue(RecordSymbol record, LLVMValueRef address, LLVMBuilderRef builder)
	{
		if (_typePool.GetDestructor(record) is { } destructor)
		{
			GetFunctionValue(destructor);
			var function = _funMap[destructor];
			builder.BuildCall2(function.FunctionType, function.FunctionValue, [address]);
		}
		
		var recordType = MapTypeSymbol(record);
		foreach (var field in record.Members.OfType<FieldSymbol>().Reverse())
		{
			var fieldType = _typePool.GetTypeOfMember(field);
			if (!_typePool.NeedsDrop(fieldType) || _typePool.IsMovedByDestructor(record, field))
				continue;
			
			var index = (uint)_typePool.GetFieldIndex(record, field);
			EmitDropCall(fieldType, builder.BuildStructGEP2(recordType, address, index, field.Name), builder);
		}
	}
	
	private void EmitEnumDropGlue(EnumSymbol enumType, LLVMValueRef address, LLVMBuilderRef builder)
	{
		var glue = builder.InsertBlock.Parent;
		var type = MapTypeSymbol(enumType);
		var done = glue.AppendBasicBlock("done");
		var droppedCases = enumType.Cases
			.Where(enumCase => enumCase.Fields.Any(field => _typePool.NeedsDrop(_typePool.GetTypeOfMember(field))))
			.DistinctBy(enumCase => _typePool.GetCaseValue(enumType, enumCase))
			.ToList();
		
		var tagAddress = builder.BuildStructGEP2(type, address, 0, "tag.addr");
		var tag = builder.BuildLoad2(MapTypeSymbol(_typePool.GetTagType(enumType)), tagAddress, "tag");
		var dispatch = builder.BuildSwitch(tag, done, (uint)droppedCases.Count);
		foreach (var enumCase in droppedCases)
		{
			var block = glue.AppendBasicBlock(enumCase.Name);
			dispatch.AddCase(EmitCaseTag(enumType, enumCase), block);
			builder.PositionAtEnd(block);
			
			var payload = builder.BuildStructGEP2(type, address, 1, "payload");
			var payloadType = GetPayloadType(enumCase);
			for (var i = enumCase.Fields.Length - 1; i >= 0; i--)
			{
				var field = enumCase.Fields[i];
				var fieldType = _typePool.GetTypeOfMember(field);
				if (_typePool.NeedsDrop(fieldType))
					EmitDropCall(fieldType, builder.BuildStructGEP2(payloadType, payload, (uint)i, field.Name),
						builder);
			}
			
			builder.BuildBr(done);
		}
		
		builder.PositionAtEnd(done);
	}
	
	private void EmitArrayDropGlue(ArrayType array, LLVMValueRef address, LLVMBuilderRef builder)
	{
		if (array.Length.IsZero)
			return;
		
		var glue = builder.InsertBlock.Parent;
		var entry = builder.InsertBlock;
		var loop = glue.AppendBasicBlock("loop");
		var done = glue.AppendBasicBlock("done");
		var indexType = MapTypeSymbol(NativeSymbols.UIntSize);
		builder.BuildBr(loop);
		
		builder.PositionAtEnd(loop);
		var index = builder.BuildPhi(indexType, "index");
		var element = builder.BuildSub(index, LLVMValueRef.CreateConstInt(indexType, 1), "element");
		var zero = LLVMValueRef.CreateConstInt(indexType, 0);
		var elementAddress = builder.BuildGEP2(MapTypeSymbol(array), address, new[] { zero, element }, "elemptr");
		EmitDropCall(array.ElementType, elementAddress, builder);
		builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, element, zero), loop, done);
		index.AddIncoming([LLVMValueRef.CreateConstInt(indexType, (ulong)array.Length), element],
			[entry, builder.InsertBlock], 2);
		
		builder.PositionAtEnd(done);
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
		ZeroValue or DefaultValue => EmitZero(value),
		VariableValue v => builder.BuildLoad2(MapTypeSymbol(v.Type), _varMap[v.Variable], v.Variable.Symbol.Name),
		GlobalValue v => EmitGlobalLoad(v.Global, builder),
		MoveValue v => EmitValue(v.Place, builder),
		BinOpValue v => EmitBinaryOp(v, builder),
		UnaryOpValue v => EmitUnaryOp(v, builder),
		AssignValue v => EmitAssignValue(v, builder),
		IndexerValue v => EmitIndexer(v, builder),
		AccessValue v => EmitAccessValue(v, builder),
		ArrayValue v => EmitArrayValue(v, builder),
		ConversionValue v => EmitConversion(v, builder),
		CallValue v => EmitCall(v, builder),
		FunctionReferenceValue v => EmitFunctionReference(v.Function, v.Type),
		IndirectCallValue v => EmitIndirectCall(v, builder),
		PointerOffsetValue v => EmitPointerOffset(v, builder),
		PointerDifferenceValue v => EmitPointerDifference(v, builder),
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
		if (!v.FunctionType.IsExternal)
		{
			var parameterTypes = v.FunctionType.ParameterTypes
				.Select((type, i) => MapParameterType(type, v.FunctionType.ParameterModes[i]));
			
			var codeType = LLVMTypeRef.CreateFunction(returnType, [OpaquePointer, ..parameterTypes]);
			var code = builder.BuildExtractValue(target, 0, "code");
			var environment = builder.BuildExtractValue(target, 1, "env");
			return builder.BuildCall2(codeType, code, [environment, ..args]);
		}
		
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
	
	private LLVMValueRef EmitZero(Value v) => LLVMValueRef.CreateConstNull(MapTypeSymbol(v.Type));
	
	private LLVMValueRef EmitEnumValue(EnumValue v, LLVMBuilderRef builder)
	{
		var enumType = MapTypeSymbol(v.Type);
		var tag = EmitCaseTag((EnumSymbol)v.Type, v.Case);
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
	
	private LLVMValueRef EmitCaseTag(EnumSymbol enumType, EnumCaseSymbol enumCase) => EmitIntegerConstant(
		new IntegerConstant(_typePool.GetTagType(enumType), _typePool.GetCaseValue(enumType, enumCase)));
	
	private LLVMValueRef EmitEnumConversion(EnumConversion c, ConversionValue v, LLVMBuilderRef builder)
	{
		var source = EmitValue(v.Source, builder);
		if (c.From is EnumSymbol from)
		{
			var tag = builder.BuildExtractValue(source, 0, "tag");
			return ResizeInteger(tag, MapTypeSymbol(c.To), _typePool.GetTagType(from).IsSigned, builder);
		}
		
		var to = (EnumSymbol)c.To;
		var sourceType = (IntegerType)c.From;
		if (!to.IsExternal)
			PanicIf(builder.BuildNot(IsCaseValue(to, source, sourceType, builder)),
				$"'{to.Name}' has no case with this value", v.SourceLocation, builder);
		
		var resized = ResizeInteger(source, MapTypeSymbol(_typePool.GetTagType(to)), sourceType.IsSigned, builder);
		return builder.BuildInsertValue(LLVMValueRef.CreateConstNull(MapTypeSymbol(to)), resized, 0, to.Name);
	}
	
	private LLVMValueRef IsCaseValue(EnumSymbol enumType, LLVMValueRef value, IntegerType type,
		LLVMBuilderRef builder)
	{
		var bits = (int)value.TypeOf.IntWidth;
		var minimum = type.IsSigned ? -(BigInteger.One << (bits - 1)) : BigInteger.Zero;
		var maximum = type.IsSigned ? (BigInteger.One << (bits - 1)) - 1 : (BigInteger.One << bits) - 1;
		var values = enumType.Cases
			.Select(enumCase => _typePool.GetCaseValue(enumType, enumCase))
			.Where(caseValue => caseValue >= minimum && caseValue <= maximum)
			.Distinct()
			.Order()
			.ToList();
		
		var result = _false;
		var start = 0;
		while (start < values.Count)
		{
			var end = start;
			while (end + 1 < values.Count && values[end + 1] == values[end] + 1)
				end++;
			
			var low = EmitIntegerConstant(new IntegerConstant(type, values[start]));
			var span = EmitIntegerConstant(new IntegerConstant(type, values[end] - values[start]));
			var offset = builder.BuildSub(value, low);
			var inRange = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, offset, span);
			result = result.Handle == _false.Handle ? inRange : builder.BuildOr(result, inRange);
			start = end + 1;
		}
		
		return result;
	}
	
	private LLVMValueRef EmitEnumTag(EnumTagValue v, LLVMBuilderRef builder)
	{
		if (!IsAddressable(v.Target))
			return builder.BuildExtractValue(EmitValue(v.Target, builder), 0, "tag");
		
		var enumType = MapTypeSymbol(v.Target.Type);
		var address = builder.BuildStructGEP2(enumType, EmitAddress(v.Target, builder), 0, "tag.addr");
		return builder.BuildLoad2(MapTypeSymbol(v.Type), address, "tag");
	}
	
	private LLVMValueRef EmitEnumPayload(EnumPayloadValue v, LLVMBuilderRef builder) =>
		builder.BuildLoad2(MapTypeSymbol(v.Type), EmitEnumPayloadAddress(v, builder), v.Case.Fields[v.Index].Name);
	
	private LLVMValueRef EmitEnumPayloadAddress(EnumPayloadValue v, LLVMBuilderRef builder)
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
		return builder.BuildStructGEP2(GetPayloadType(v.Case), payload, (uint)v.Index, name + ".addr");
	}
	
	private LLVMValueRef EmitConversion(ConversionValue v, LLVMBuilderRef builder) => v.Conversion switch
	{
		IdentityConversion => EmitValue(v.Source, builder),
		IntegerConversion c => EmitIntegerConversion(c, EmitValue(v.Source, builder), builder),
		FloatConversion c => EmitFloatConversion(c, EmitValue(v.Source, builder), builder),
		NativeConversion c => EmitNativeConversion(c, v, builder),
		EnumConversion c => EmitEnumConversion(c, v, builder),
		FreeConversion => EmitValue(v.Source, builder),
		FunctionConversion c => EmitValue(new CallValue(c.Function, [v.Source], v.SourceLocation), builder),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitNativeConversion(NativeConversion c, ConversionValue v, LLVMBuilderRef builder)
	{
		var source = EmitValue(v.Source, builder);
		
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
				EmitIntegerDivision(v.Op, left, right, signed, v.SourceLocation, builder),
			
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
		SourceLocation location, LLVMBuilderRef builder)
	{
		var type = left.TypeOf;
		var one = LLVMValueRef.CreateConstInt(type, 1);
		PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstNull(type)),
			"division by zero", location, builder);
		
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
		PanicIf(builder.BuildAnd(isMinusOne, builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, minimum)),
			"division overflow", location, builder);
		
		return builder.BuildSDiv(left, right);
	}
	
	private void PanicIf(LLVMValueRef condition, string message, SourceLocation location, LLVMBuilderRef builder)
	{
		if (condition.Handle == _false.Handle)
			return;
		
		var function = builder.InsertBlock.Parent;
		var panicBlock = function.AppendBasicBlock("panic");
		var continueBlock = function.AppendBasicBlock("no_panic");
		builder.BuildCondBr(condition, panicBlock, continueBlock);
		
		builder.PositionAtEnd(panicBlock);
		var text = $"Panic at {DescribeLocation(location)}: {message}\n";
		var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)Encoding.UTF8.GetByteCount(text));
		var messagePointer = builder.BuildGlobalStringPtr(text, "panic.message");
		builder.BuildCall2(PanicType, GetPanicFunction(), [messagePointer, length]);
		builder.BuildUnreachable();
		
		builder.PositionAtEnd(continueBlock);
	}
	
	private string DescribeLocation(SourceLocation location)
	{
		var path = location.Source.FilePath;
		if (_config.SourceRoot is { } root && Path.IsPathFullyQualified(path))
			path = Path.GetRelativePath(root, path);
		
		var (line, column) = location.GetLineColumn();
		return $"{path.Replace('\\', '/')}:{line}:{column}";
	}
	
	private LLVMValueRef GetPanicFunction()
	{
		if (panicFunction.Handle != IntPtr.Zero)
			return panicFunction;
		
		panicFunction = currentModule.AddFunction("panic", PanicType);
		panicFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = currentModule.Context.CreateBuilder();
		builder.PositionAtEnd(panicFunction.AppendBasicBlock("entry"));
		
		var flushType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Int32, [OpaquePointer]);
		builder.BuildCall2(flushType, GetCFunction("fflush", flushType), [LLVMValueRef.CreateConstNull(OpaquePointer)]);
		
		var isWindows = TargetTriple.Contains("windows");
		var countType = isWindows ? LLVMTypeRef.Int32 : MapTypeSymbol(NativeSymbols.UIntSize);
		var writeType = LLVMTypeRef.CreateFunction(countType, [LLVMTypeRef.Int32, OpaquePointer, countType]);
		var length = isWindows ? panicFunction.GetParam(1) : builder.BuildZExt(panicFunction.GetParam(1), countType);
		var standardError = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 2);
		builder.BuildCall2(writeType, GetCFunction(isWindows ? "_write" : "write", writeType),
			[standardError, panicFunction.GetParam(0), length]);
		
		var abortType = LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, []);
		builder.BuildCall2(abortType, GetCFunction("abort", abortType), []);
		builder.BuildUnreachable();
		return panicFunction;
	}
	
	private LLVMValueRef GetCFunction(string name, LLVMTypeRef type)
	{
		var function = currentModule.GetNamedFunction(name);
		if (function.Handle != IntPtr.Zero)
			return function;
		
		function = currentModule.AddFunction(name, type);
		function.Linkage = LLVMLinkage.LLVMExternalLinkage;
		return function;
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
		
		switch (v.Target.Type)
		{
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
				if (!_config.BoundsChecks || a.Length.Sign < 0)
					return (builder.BuildGEP2(arrayType, arrayPtr, new[] { zero, index }, "elemptr"), elemType);
				
				var length = LLVMValueRef.CreateConstInt(index.TypeOf, (ulong)a.Length);
				PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, index, length), "index out of bounds",
					v.SourceLocation, builder);
				
				return (builder.BuildInBoundsGEP2(arrayType, arrayPtr, new[] { zero, index }, "elemptr"), elemType);
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
		EnumPayloadValue v => EmitEnumPayloadAddress(v, builder),
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
		else if (info.Symbol.Visibility is Visibility.Private or Visibility.Module)
		{
			global.Linkage = LLVMLinkage.LLVMInternalLinkage;
		}
		else
		{
			global.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		}
		
		_globalMap[info.Symbol] = global;
	}
	
	private LLVMValueRef GetGlobal(GlobalInfo info)
	{
		if (_globalMap.TryGetValue(info.Symbol, out var existing))
			return existing;
		
		var global = currentModule.AddGlobal(GetGlobalStorageType(info), info.MangledName);
		global.IsGlobalConstant = !info.Symbol.IsMutable;
		if (!_assemblySymbol.SignatureTable.Globals.ContainsKey(info.Symbol))
		{
			global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			global.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		else if (info.Symbol.Visibility != Visibility.Public)
		{
			global.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		}
		
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
	
	private LLVMValueRef EmitFunctionReference(FunctionInfo function, TypeSymbol type)
	{
		if (type is FunctionType { IsExternal: false })
		{
			var environment = LLVMValueRef.CreateConstNull(OpaquePointer);
			return LLVMValueRef.CreateConstStruct([GetClosureThunk(function), environment], false);
		}
		
		return function.Symbol.IsExternal ? GetFunctionValue(function) : GetExternalThunk(function);
	}
	
	private LLVMValueRef GetClosureThunk(FunctionInfo function)
	{
		var name = $"{function.MangledName ?? function.Symbol.Name}$fun";
		var existing = currentModule.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		GetFunctionValue(function);
		var target = _funMap[function];
		var declared = function.Signature;
		var parameterTypes = declared.ParameterTypes
			.Select((type, i) => MapParameterType(type, declared.GetMode(i)))
			.ToArray();
		
		var thunkType = LLVMTypeRef.CreateFunction(target.ReturnType, [OpaquePointer, ..parameterTypes]);
		var thunk = currentModule.AddFunction(name, thunkType);
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		using var builder = currentModule.Context.CreateBuilder();
		builder.PositionAtEnd(thunk.AppendBasicBlock("entry"));
		var args = parameterTypes.Select((_, i) => thunk.GetParam((uint)i + 1)).ToList();
		var result = target.CSignature is { } signature
			? EmitCCall(signature, target.FunctionType, target.FunctionValue, target.ReturnType, args, builder)
			: builder.BuildCall2(target.FunctionType, target.FunctionValue, args.ToArray());
		
		if (target.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
			builder.BuildRetVoid();
		else
			builder.BuildRet(result);
		
		return thunk;
	}
	
	private LLVMValueRef GetExternalThunk(FunctionInfo function)
	{
		var name = $"{function.MangledName}$ext";
		var existing = currentModule.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		GetFunctionValue(function);
		var target = _funMap[function];
		var parameterTypes = function.Signature.ParameterTypes.Select(MapTypeSymbol).ToArray();
		var signature = _cAbi.Classify(parameterTypes, target.ReturnType);
		var thunk = currentModule.AddFunction(name, signature.CreateFunctionType(false));
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		var isIndirect = signature.Return.Kind == CPassKind.Indirect;
		if (isIndirect)
			AddSretAttribute(thunk, signature.Return.Type, false);
		
		using var builder = currentModule.Context.CreateBuilder();
		builder.PositionAtEnd(thunk.AppendBasicBlock("entry"));
		var first = isIndirect ? 1u : 0u;
		var args = parameterTypes
			.Select((type, i) => ReceiveCArgument(signature.Parameters[i], thunk.GetParam(first + (uint)i), type,
				builder))
			.ToArray();
		
		var result = builder.BuildCall2(target.FunctionType, target.FunctionValue, args);
		if (target.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
		{
			builder.BuildRetVoid();
		}
		else if (isIndirect)
		{
			builder.BuildStore(result, thunk.GetParam(0));
			builder.BuildRetVoid();
		}
		else
		{
			builder.BuildRet(PassCArgument(signature.Return, result, builder));
		}
		
		return thunk;
	}
	
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
		else if (!_assemblySymbol.SignatureTable.Functions.ContainsKey(function.Symbol))
		{
			value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			value.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		else if (function.Symbol.Visibility != Visibility.Public)
		{
			value.Visibility = LLVMVisibility.LLVMHiddenVisibility;
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
		EnumTagConstant c => EmitEnumTagConstant(c),
		FunctionConstant c => EmitFunctionReference(c.Function, c.Type),
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
		var tag = EmitCaseTag((EnumSymbol)constant.Type, constant.Case);
		if (constant.Payload.IsEmpty)
			return EmitTagOnly(enumType, tag);
		
		var area = enumType.StructGetTypeAtIndex(1);
		var payload = LLVMValueRef.CreateConstStruct([..constant.Payload.Select(EmitStaticConstant)], false);
		var alignment = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreateArray(area.ElementType, 0));
		var tail = _targetData.ABISizeOfType(area) - _targetData.ABISizeOfType(payload.TypeOf);
		if (tail == 0)
			return LLVMValueRef.CreateConstStruct([tag, alignment, payload], false);
		
		var padding = LLVMValueRef.CreateConstNull(LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)tail));
		return LLVMValueRef.CreateConstStruct([tag, alignment, payload, padding], false);
	}
	
	private LLVMValueRef EmitEnumTagConstant(EnumTagConstant constant)
	{
		var enumType = (EnumSymbol)constant.Type;
		var tag = EmitIntegerConstant(new IntegerConstant(_typePool.GetTagType(enumType), constant.Tag));
		return EmitTagOnly(MapTypeSymbol(enumType), tag);
	}
	
	private static LLVMValueRef EmitTagOnly(LLVMTypeRef enumType, LLVMValueRef tag) =>
		enumType.StructElementTypesCount == 1
			? LLVMValueRef.CreateConstNamedStruct(enumType, [tag])
			: LLVMValueRef.CreateConstNamedStruct(enumType,
				[tag, LLVMValueRef.CreateConstNull(enumType.StructGetTypeAtIndex(1))]);
	
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
	public bool BoundsChecks { get; init; } = true;
	public string? SourceRoot { get; init; }
	
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