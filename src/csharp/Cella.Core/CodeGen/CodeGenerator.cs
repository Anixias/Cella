using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.CodeGen.Extensions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
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
	private readonly LLVMValueRef _true = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1uL);
	private readonly LLVMValueRef _false = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0uL);
	private readonly HashSet<FunctionSymbol> _sharedFunctions;
	private readonly ModuleIndex _modules;
	private readonly Dictionary<FunctionSymbol, LoweredFunction> _genericBodies;
	private readonly CodeGenInputs _inputs;
	private readonly Dictionary<ModuleSymbol, ModuleState> _moduleStates = [];
	private readonly Dictionary<string, ModuleState> _foreignOwners = [];
	private readonly HashSet<GlobalSymbol> _exportedStatics = [];
	private readonly Queue<(ModuleState Owner, FunctionInfo Info)> _pendingInstantiations = [];
	private readonly Queue<(ModuleState Owner, GlobalInfo Info)> _pendingGlobals = [];
	private readonly HashSet<string> _requestedInstantiations = [];
	private readonly HashSet<GlobalSymbol> _requestedGlobals = [];
	private ModuleState current = null!;
	private LLVMFunctionInfo currentFunction;
	
	private IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> substitution =
		new Dictionary<TypeParameterSymbol, TypeSymbol>();
	
	public CodeGenerator(AssemblySymbol assemblySymbol, TypePool typePool, CodeGenConfig config, ModuleIndex modules,
		CodeGenInputs inputs)
	{
		_modules = modules;
		_inputs = inputs;
		_genericBodies = inputs.GenericFunctions.ToDictionary(static function => function.Info.Symbol);
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
			.Concat(typePool.GetWitnessFunctions())
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
	
	public IReadOnlySet<GlobalSymbol> ExportedStatics => _exportedStatics;
	
	private bool IsObjectLocal(FunctionSymbol function) =>
		function.Visibility is Visibility.Private or Visibility.Module &&
		function.Kind is not (FunctionKind.Destructor or FunctionKind.Constructor) &&
		!_sharedFunctions.Contains(function);
	
	private bool IsExported(FunctionSymbol function) => function.Visibility == Visibility.Public ||
	                                                    _config.IsLibrary &&
	                                                    (function.Kind == FunctionKind.Destructor ||
	                                                     _inputs.GenericReferences.Contains(function));
	
	private bool IsExported(GlobalSymbol global) => global.Visibility == Visibility.Public ||
	                                                _config.IsLibrary && _inputs.GenericReferences.Contains(global);
	
	private void Import(LLVMValueRef value, Symbol symbol)
	{
		if (_inputs.StaticLibrarySymbols.Contains(symbol))
			return;
		
		value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
		value.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
	}
	
	private void ShareDefinition(LLVMValueRef value, bool export)
	{
		value.Linkage = LLVMLinkage.LLVMWeakODRLinkage;
		using var name = new MarshaledString(value.Name);
		LLVM.SetComdat((LLVMOpaqueValue*)value.Handle,
			LLVM.GetOrInsertComdat((LLVMOpaqueModule*)current.Module.Handle, name));
		
		if (export)
			value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		else
			value.Visibility = LLVMVisibility.LLVMHiddenVisibility;
	}
	
	private ModuleState FindOwner(ModuleSymbol module, string name)
	{
		if (_moduleStates.TryGetValue(module, out var owner))
			return owner;
		
		_foreignOwners.TryAdd(name, current);
		return _foreignOwners[name];
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
		current.Types[NativeSymbols.Void] = LLVMTypeRef.Void;
		current.Types[NativeSymbols.VoidPtr] = LLVMTypeRef.CreatePointer(LLVMTypeRef.Void, 0u);
		current.Types[NativeSymbols.Int8] = LLVMTypeRef.Int8;
		current.Types[NativeSymbols.Int16] = LLVMTypeRef.Int16;
		current.Types[NativeSymbols.Int32] = LLVMTypeRef.Int32;
		current.Types[NativeSymbols.Int64] = LLVMTypeRef.Int64;
		current.Types[NativeSymbols.Int128] = LLVMTypeRef.Int128;
		current.Types[NativeSymbols.IntSize] = intSize;
		current.Types[NativeSymbols.UInt8] = LLVMTypeRef.Int8;
		current.Types[NativeSymbols.UInt16] = LLVMTypeRef.Int16;
		current.Types[NativeSymbols.UInt32] = LLVMTypeRef.Int32;
		current.Types[NativeSymbols.UInt64] = LLVMTypeRef.Int64;
		current.Types[NativeSymbols.UInt128] = LLVMTypeRef.Int128;
		current.Types[NativeSymbols.UIntSize] = intSize;
		current.Types[NativeSymbols.Float32] = LLVMTypeRef.Float;
		current.Types[NativeSymbols.Float64] = LLVMTypeRef.Double;
		current.Types[NativeSymbols.Char] = LLVMTypeRef.Int32;
		current.Types[NativeSymbols.Bool] = LLVMTypeRef.Int1;
		current.Types[NativeSymbols.Str] =
			LLVMTypeRef.CreateStruct([intSize, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u)], false);
		
		current.Types[NativeSymbols.CStr] = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u);
	}
	
	public List<CodeGenResult> Generate(IEnumerable<LoweredModule> modules)
	{
		var states = new List<ModuleState>();
		try
		{
			foreach (var module in modules)
				states.Add(CreateModuleState(module));
			
			foreach (var state in states)
			{
				current = state;
				BuildModule(state.Lowered);
			}
			
			BuildInstantiations();
			return [..states.Select(Finish)];
		}
		finally
		{
			foreach (var state in states)
			{
				LLVM.DisposeDIBuilder((LLVMOpaqueDIBuilder*)state.DiBuilder.Handle);
				state.Module.Dispose();
			}
			
			_moduleStates.Clear();
			_foreignOwners.Clear();
			_requestedInstantiations.Clear();
			_requestedGlobals.Clear();
			current = null!;
		}
	}
	
	private ModuleState CreateModuleState(LoweredModule module)
	{
		var llvmModule = LLVMModuleRef.CreateWithName(module.Symbol.Name);
		llvmModule.Target = TargetTriple;
		llvmModule.DataLayout = _dataLayoutStr;
		current = new ModuleState(module, llvmModule, llvmModule.CreateDIBuilder());
		_moduleStates[module.Symbol] = current;
		MapNativeSymbols();
		return current;
	}
	
	private void BuildInstantiations()
	{
		while (_pendingGlobals.Count > 0 || _pendingInstantiations.Count > 0)
		{
			while (_pendingGlobals.TryDequeue(out var global))
			{
				current = global.Owner;
				GetGlobal(global.Info);
			}
			
			if (!_pendingInstantiations.TryDequeue(out var pending))
				continue;
			
			current = pending.Owner;
			GetFunctionValue(pending.Info);
			substitution = TypePool.CreateMap(pending.Info.Symbol.TypeParameters, pending.Info.TypeArguments);
			BuildFunction(_genericBodies[pending.Info.Symbol], pending.Info);
			substitution = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		}
	}
	
	private CodeGenResult Finish(ModuleState state)
	{
		current = state;
		var llvmModule = state.Module;
		var name = state.Lowered.Symbol.Name;
		if (state.EntryPoint is { } entryPoint)
			BuildEntryPoint(llvmModule, entryPoint);
		
		RunOptimizationPass(llvmModule);
		state.DiBuilder.DIBuilderFinalize();
		
		string message;
		if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMAbortProcessAction, out message))
			return CodeGenResult.Failure with { ErrorMessage = message };
		
		if (!Directory.Exists(_config.OutputConfig.Directory))
			Directory.CreateDirectory(_config.OutputConfig.Directory);
		
		if (_config.OutputConfig.EmitIR)
		{
			var irFilePath = Path.Combine(_config.OutputConfig.Directory, $"{name}.ll");
			if (!llvmModule.TryPrintToFile(irFilePath, out message))
				return CodeGenResult.Failure with { ErrorMessage = message };
		}
		
		if (_config.OutputConfig.EmitAssembly)
		{
			var assemblyFilePath = Path.Combine(_config.OutputConfig.Directory, $"{name}.s");
			if (!_targetMachine.TryEmitToFile(llvmModule, assemblyFilePath, LLVMCodeGenFileType.LLVMAssemblyFile,
				    out message))
				return CodeGenResult.Failure with { ErrorMessage = message };
		}
		
		var objectFilePath = Path.Combine(_config.OutputConfig.Directory, $"{name}.o");
		if (_targetMachine.TryEmitToFile(llvmModule, objectFilePath, LLVMCodeGenFileType.LLVMObjectFile, out message))
			return new(true, objectFilePath, null, state.ExternalLibraries);
		
		return CodeGenResult.Failure with { ErrorMessage = message };
	}
	
	private static LLVMTypeRef OpaquePointer => LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0u);
	private static LLVMTypeRef DropGlueType => LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [OpaquePointer]);
	
	private static LLVMTypeRef PanicType =>
		LLVMTypeRef.CreateFunction(LLVMTypeRef.Void, [OpaquePointer, LLVMTypeRef.Int32]);
	
	private static LLVMTypeRef FatPointerType => LLVMTypeRef.CreateStruct([OpaquePointer, OpaquePointer], false);
	
	private static LLVMTypeRef MapPassedPointer(TypeSymbol declared) =>
		declared is DynType ? FatPointerType : OpaquePointer;
	
	private LLVMTypeRef MapParameterType(TypeSymbol type, ParameterMode mode) =>
		_typePool.PassesByPointer(type, mode) ? MapPassedPointer(type) : MapTypeSymbol(type);
	
	private LLVMTypeRef[] MapParameterTypes(FunctionInfo function)
	{
		var declared = function.DeclaredSignature;
		return
		[
			..function.Signature.ParameterTypes.Select((type, i) =>
				_typePool.PassesByPointer(declared.ParameterTypes[i], declared.GetMode(i))
					? MapPassedPointer(declared.ParameterTypes[i])
					: MapTypeSymbol(type))
		];
	}
	
	private TypeSymbol Substitute(TypeSymbol type) => _typePool.Substitute(type, substitution);
	
	private static bool IsEmitted(FunctionInfo function) =>
		!IsOpenGeneric(function) && function.Symbol.Syntax is not FunctionNode { When.IsActive: false };
	
	private static bool IsOpenGeneric(FunctionInfo function) =>
		!function.Symbol.TypeParameters.IsEmpty && (function.TypeArguments.IsDefaultOrEmpty ||
		                                            function.TypeArguments.Any(TypePool.ContainsTypeParameters));
	
	private FunctionInfo SubstituteFunction(FunctionInfo function)
	{
		if (substitution.Count == 0 || function.Symbol.TypeParameters.IsEmpty)
			return function;
		
		IEnumerable<TypeSymbol> arguments = function.TypeArguments.IsDefaultOrEmpty
			? function.Symbol.TypeParameters
			: function.TypeArguments;
		
		return _typePool.InstantiateFunction(function, [..arguments.Select(Substitute)]);
	}
	
	private LLVMTypeRef MapTypeSymbol(TypeSymbol? symbol)
	{
		if (symbol is null)
			return LLVMTypeRef.Void;
		
		symbol = Substitute(symbol);
		if (current.Types.TryGetValue(symbol, out var type))
			return type;
		
		switch (symbol)
		{
			case ArrayType arrayType:
			{
				var elementType = MapTypeSymbol(arrayType.ElementType);
				var length = (uint)arrayType.Length;
				var llvmArray = LLVMTypeRef.CreateArray(elementType, length);
				current.Types[symbol] = llvmArray;
				return llvmArray;
			}
			
			case FunctionType functionType:
			{
				var llvmFunctionType = functionType.IsExternal
					? OpaquePointer
					: LLVMTypeRef.CreateStruct([OpaquePointer, OpaquePointer], false);
				
				current.Types[symbol] = llvmFunctionType;
				return llvmFunctionType;
			}
			
			case PointerType or BorrowType:
			{
				var pointerType = TypePool.IsFatPointer(symbol) ? FatPointerType : OpaquePointer;
				current.Types[symbol] = pointerType;
				return pointerType;
			}
			
			case FStrType:
			{
				var fstrType = LLVMTypeRef.CreateStruct(
					[MapTypeSymbol(NativeSymbols.UIntSize), OpaquePointer, OpaquePointer], false);
				
				current.Types[symbol] = fstrType;
				return fstrType;
			}
			
			case DynType:
				throw new InvalidOperationException($"'{symbol.Name}' has no size");
			
			case TypeParameterSymbol:
				throw new InvalidOperationException($"Type parameter '{symbol.Name}' has no type argument");
			
			case EnumSymbol enumType:
				return CreateEnumType(enumType);
			
			default:
				return CreateType(symbol);
			//throw new InvalidOperationException($"Type '{symbol.Name}' not mapped in LLVM");
		}
	}
	
	private void BuildModule(LoweredModule module)
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
		
		foreach (var file in module.Files)
		{
			// Create and map external functions
			foreach (var function in file.ExternalFunctions)
			{
				if (function.Origin is { } origin)
					current.ExternalLibraries.Add(origin);
				
				var info = CreateFunction(current.Module, function);
				var llvmFunction = info.FunctionValue;
				llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
				
				// TODO On Windows, check for DLL Import metadata/annotation/attribute
				// llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
				// llvmFunction.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
			}
			
			// Create and map imported functions
			foreach (var function in file.ImportedFunctions.Where(static function =>
				         !IsOpenGeneric(function) && FindDynDispatch(function) is null))
				GetFunctionValue(function);
			
			// Create and map functions
			foreach (var function in file.Functions.Where(static function => IsEmitted(function.Info)))
			{
				var info = CreateFunction(current.Module, function.Info);
				var llvmFunction = info.FunctionValue;
				
				if (IsExported(function.Info.Symbol))
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
					current.EntryPoint = info;
			}
		}
		
		foreach (var file in module.Files)
		{
			var globals = file.Globals.Where(static global =>
				global.Symbol.ContainingType is not NamedTypeSymbol { IsGenericDefinition: true });
			
			foreach (var global in globals)
				DefineGlobal(global);
		}
		
		foreach (var file in module.Files)
		{
			// Build function bodies
			foreach (var function in file.Functions.Where(static function => IsEmitted(function.Info)))
				BuildFunction(function, function.Info);
		}
	}
	
	private LLVMTypeRef CreateType(TypeSymbol type)
	{
		if (current.Types.TryGetValue(type, out var existing))
			return existing;
		
		var typeRef = LLVMContextRef.Global.CreateNamedStruct(type.Name);
		current.Types[type] = typeRef;
		
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
		if (enumType.IsMatch)
		{
			var matchedType = MapTypeSymbol(_typePool.GetMatchedType(enumType));
			current.Types[enumType] = matchedType;
			return matchedType;
		}
		
		var typeRef = LLVMContextRef.Global.CreateNamedStruct(enumType.Name);
		current.Types[enumType] = typeRef;
		
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
	
	private LLVMFunctionInfo CreateFunction(LLVMModuleRef llvmModule, FunctionInfo function, string? name = null)
	{
		if (current.Functions.TryGetValue(function, out var existing))
			return existing;
		
		var signature = function.Signature;
		var symbol = function.Symbol;
		var returnType = MapTypeSymbol(signature.ReturnType);
		var paramLlvmTypes = MapParameterTypes(function);
		
		CSignature? cSignature = symbol.IsExternal
			? _cAbi.Classify(paramLlvmTypes, returnType)
			: null;
		
		var functionType = cSignature?.CreateFunctionType(signature.IsVariadic)
		                   ?? LLVMTypeRef.CreateFunction(returnType, paramLlvmTypes, signature.IsVariadic);
		
		name ??= function.MangledName ?? symbol.Name;
		var declared = symbol.Kind == FunctionKind.External ? llvmModule.GetNamedFunction(name) : default;
		var functionValue = declared.Handle != IntPtr.Zero ? declared : llvmModule.AddFunction(name, functionType);
		if (cSignature is { Return: { Kind: CPassKind.Indirect } sret })
			AddSretAttribute(functionValue, sret.Type, false);
		
		var functionInfo = new LLVMFunctionInfo(functionValue, functionType, returnType, cSignature);
		
		current.Functions.Add(function, functionInfo);
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
	
	private void BuildFunction(LoweredFunction function, FunctionInfo info)
	{
		currentFunction = current.Functions[info];
		var functionValue = currentFunction.FunctionValue;
		var allocaBlock = functionValue.AppendBasicBlock("allocas");
		
		// Create blocks
		var blockMap = new Dictionary<BasicBlock, LLVMBasicBlockRef>();
		foreach (var block in function.Blocks)
			blockMap.Add(block, functionValue.AppendBasicBlock(block.Label));
		
		// Build blocks
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(allocaBlock);
		foreach (var local in function.Blocks.SelectMany(static b => b.Instructions).OfType<LocalVarInstruction>())
			current.Variables[new(local.Symbol, local.Symbol.Type)] =
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
						current.Variables[paramInfo] = paramLlvmValue;
						continue;
					}
					
					var paramLlvmType = MapTypeSymbol(paramType);
					if (signature is { Parameters: var passes })
						paramLlvmValue = ReceiveCArgument(passes[p], paramLlvmValue, paramLlvmType, builder);
					
					var paramPtr = BuildEntryAlloca(builder, paramLlvmType, paramSymbol.Name);
					builder.BuildStore(paramLlvmValue, paramPtr);
					current.Variables[paramInfo] = paramPtr;
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
					builder.BuildStore(EmitValue(i.Initializer, builder),
						current.Variables[new(i.Symbol, i.Symbol.Type)]);
				
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
	
	private void EmitDropCall(TypeSymbol type, LLVMValueRef address, LLVMBuilderRef builder)
	{
		type = Substitute(type);
		if (_typePool.NeedsDrop(type))
			builder.BuildCall2(DropGlueType, GetDropGlue(type), [address]);
	}
	
	private LLVMValueRef GetDropGlue(TypeSymbol type)
	{
		if (current.DropGlue.TryGetValue(type, out var glue))
			return glue;
		
		glue = current.Module.AddFunction($"drop${type.Name}", DropGlueType);
		glue.Linkage = LLVMLinkage.LLVMInternalLinkage;
		current.DropGlue[type] = glue;
		
		using var builder = current.Module.Context.CreateBuilder();
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
			var function = current.Functions[destructor];
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
		VariableValue v => builder.BuildLoad2(MapTypeSymbol(v.Type), current.Variables[v.Variable],
			v.Variable.Symbol.Name),
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
		SizeOfValue v => EmitSizeOf(v),
		AlignOfValue v => EmitAlignOf(v),
		NewValue v => EmitNew(v, builder),
		FStrValue v => EmitFStr(v, builder),
		FStrPartValue v => EmitFStrPart(v, builder),
		AtomicValue v => EmitAtomic(v, builder),
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef EmitAtomic(AtomicValue v, LLVMBuilderRef builder)
	{
		var ordering = MapOrdering(v.Ordering);
		if (v.Pointer is not { } pointer)
			return builder.BuildFence(ordering, false, "");
		
		var valueType = ((PointerType)Substitute(pointer.Type)).BaseType;
		var atomicType = GetAtomicType(valueType);
		var address = EmitValue(pointer, builder);
		switch (v.Access)
		{
			case AtomicAccess.Load:
			{
				var load = builder.BuildLoad2(atomicType, address, "atomic");
				load.Alignment = (uint)_targetData.ABISizeOfType(atomicType);
				LLVM.SetOrdering(load, ordering);
				return FromAtomic(load, valueType, builder);
			}
			
			case AtomicAccess.Store:
			{
				var value = EmitValue(v.Operand!, builder);
				var store = builder.BuildStore(ToAtomic(value, valueType, builder), address);
				store.Alignment = (uint)_targetData.ABISizeOfType(atomicType);
				LLVM.SetOrdering(store, ordering);
				return value;
			}
			
			case AtomicAccess.CompareSwap:
			{
				var expected = ToComparable(ToAtomic(EmitValue(v.Expected!, builder), valueType, builder), builder);
				var desired = ToComparable(ToAtomic(EmitValue(v.Operand!, builder), valueType, builder), builder);
				LLVMValueRef exchange = LLVM.BuildAtomicCmpXchg(builder, address, expected, desired, ordering,
					MapFailureOrdering(v.Ordering), 0);
				
				return builder.BuildExtractValue(exchange, 1, "swapped");
			}
		}
		
		var operand = ToAtomic(EmitValue(v.Operand!, builder), valueType, builder);
		var isFloat = valueType is FloatType;
		var operation = v.Operation switch
		{
			BinaryOperation.Addition when isFloat => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpFAdd,
			BinaryOperation.Addition => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd,
			BinaryOperation.Subtraction when isFloat => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpFSub,
			BinaryOperation.Subtraction => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpSub,
			BinaryOperation.BitwiseAnd => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAnd,
			BinaryOperation.BitwiseOr => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpOr,
			_ => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpXor
		};
		
		var old = builder.BuildAtomicRMW(operation, address, operand, ordering, false);
		var result = v.Operation switch
		{
			BinaryOperation.Addition when isFloat => builder.BuildFAdd(old, operand, "new"),
			BinaryOperation.Addition => builder.BuildAdd(old, operand, "new"),
			BinaryOperation.Subtraction when isFloat => builder.BuildFSub(old, operand, "new"),
			BinaryOperation.Subtraction => builder.BuildSub(old, operand, "new"),
			BinaryOperation.BitwiseAnd => builder.BuildAnd(old, operand, "new"),
			BinaryOperation.BitwiseOr => builder.BuildOr(old, operand, "new"),
			_ => builder.BuildXor(old, operand, "new")
		};
		
		return FromAtomic(result, valueType, builder);
	}
	
	private LLVMTypeRef GetAtomicType(TypeSymbol type) => type switch
	{
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => LLVMTypeRef.Int8,
		EnumSymbol { IsMatch: true } enumType => GetAtomicType(_typePool.GetMatchedType(enumType)),
		EnumSymbol enumType => MapTypeSymbol(_typePool.GetTagType(enumType)),
		_ => MapTypeSymbol(type)
	};
	
	private LLVMValueRef ToAtomic(LLVMValueRef value, TypeSymbol type, LLVMBuilderRef builder) => type switch
	{
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => builder.BuildZExt(value, LLVMTypeRef.Int8, "atomic"),
		EnumSymbol { IsMatch: true } enumType => ToAtomic(value, _typePool.GetMatchedType(enumType), builder),
		EnumSymbol => builder.BuildExtractValue(value, 0, "tag"),
		_ => value
	};
	
	private LLVMValueRef FromAtomic(LLVMValueRef value, TypeSymbol type, LLVMBuilderRef builder) => type switch
	{
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => builder.BuildTrunc(value, LLVMTypeRef.Int1, "value"),
		EnumSymbol { IsMatch: true } enumType => FromAtomic(value, _typePool.GetMatchedType(enumType), builder),
		EnumSymbol enumType => builder.BuildInsertValue(MapTypeSymbol(enumType).Undef, value, 0, "value"),
		_ => value
	};
	
	private static LLVMValueRef ToComparable(LLVMValueRef value, LLVMBuilderRef builder) => value.TypeOf.Kind switch
	{
		LLVMTypeKind.LLVMFloatTypeKind => builder.BuildBitCast(value, LLVMTypeRef.Int32, "bits"),
		LLVMTypeKind.LLVMDoubleTypeKind => builder.BuildBitCast(value, LLVMTypeRef.Int64, "bits"),
		_ => value
	};
	
	private static LLVMAtomicOrdering MapOrdering(AtomicOrdering ordering) => ordering switch
	{
		AtomicOrdering.Relaxed => LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
		AtomicOrdering.Acquire => LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
		AtomicOrdering.Release => LLVMAtomicOrdering.LLVMAtomicOrderingRelease,
		AtomicOrdering.AcquireRelease => LLVMAtomicOrdering.LLVMAtomicOrderingAcquireRelease,
		_ => LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent
	};
	
	private static LLVMAtomicOrdering MapFailureOrdering(AtomicOrdering ordering) => ordering switch
	{
		AtomicOrdering.Relaxed or AtomicOrdering.Release => LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
		AtomicOrdering.Acquire or AtomicOrdering.AcquireRelease => LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
		_ => LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent
	};
	
	private LLVMValueRef EmitFStr(FStrValue v, LLVMBuilderRef builder)
	{
		var texts = v.Text is { } text ? EmitValue(text, builder) : CreateTextsGlobal(v.Texts);
		var values = v.Values is { } array ? EmitValue(array, builder) : LLVMValueRef.CreateConstNull(OpaquePointer);
		var result = builder.BuildInsertValue(MapTypeSymbol(v.Type).Undef, EmitSizeConstant(v.Holes, true), 0, "fstr");
		result = builder.BuildInsertValue(result, texts, 1, "fstr");
		return builder.BuildInsertValue(result, values, 2, "fstr");
	}
	
	private LLVMValueRef CreateTextsGlobal(ImmutableArray<string> texts)
	{
		var lengthType = MapTypeSymbol(NativeSymbols.UIntSize);
		var elements = texts.Select(text => Encoding.UTF8.GetBytes(text)).Select(bytes =>
			LLVMValueRef.CreateConstStruct(
				[LLVMValueRef.CreateConstInt(lengthType, (ulong)bytes.Length), GetOrCreateStringGlobal(bytes)], false));
		
		var array = LLVMValueRef.CreateConstArray(MapTypeSymbol(NativeSymbols.Str), [..elements]);
		var global = current.Module.AddGlobal(array.TypeOf, "fstr.texts");
		global.Initializer = array;
		global.IsGlobalConstant = true;
		global.Linkage = LLVMLinkage.LLVMPrivateLinkage;
		global.HasUnnamedAddr = true;
		return global;
	}
	
	private LLVMValueRef EmitFStrPart(FStrPartValue v, LLVMBuilderRef builder)
	{
		var template = EmitValue(v.Target, builder);
		var index = EmitValue(v.Index, builder);
		if (_config.BoundsChecks)
		{
			var holes = builder.BuildExtractValue(template, 0, "holes");
			var predicate = v.Part == FStrPart.Piece ? LLVMIntPredicate.LLVMIntUGT : LLVMIntPredicate.LLVMIntUGE;
			PanicIf(builder.BuildICmp(predicate, index, holes), "index out of bounds", v.SourceLocation, builder);
		}
		
		if (v.Part == FStrPart.Value)
		{
			var valueType = MapTypeSymbol(v.Type);
			var values = builder.BuildExtractValue(template, 2, "values");
			var valueAddress = builder.BuildInBoundsGEP2(valueType, values, new[] { index }, "value.addr");
			return builder.BuildLoad2(valueType, valueAddress, "value");
		}
		
		var two = LLVMValueRef.CreateConstInt(index.TypeOf, 2);
		var position = builder.BuildMul(index, two, "position");
		if (v.Part == FStrPart.Spec)
			position = builder.BuildAdd(position, LLVMValueRef.CreateConstInt(index.TypeOf, 1), "position");
		
		var strType = MapTypeSymbol(NativeSymbols.Str);
		var texts = builder.BuildExtractValue(template, 1, "texts");
		var textAddress = builder.BuildInBoundsGEP2(strType, texts, new[] { position }, "text.addr");
		return builder.BuildLoad2(strType, textAddress, "text");
	}
	
	private LLVMValueRef EmitSizeOf(SizeOfValue v)
	{
		var bits = _typePool.SizeTable.GetSize(Substitute(v.Target)).CountBits(_pointerSize * 8);
		return EmitSizeConstant(new BigInteger((bits + 7) / 8), true);
	}
	
	private LLVMValueRef EmitAlignOf(AlignOfValue v)
	{
		var bits = _typePool.SizeTable.GetSize(Substitute(v.Target)).CountAlignmentBits(_pointerSize * 8);
		return EmitSizeConstant(new BigInteger((bits + 7) / 8), true);
	}
	
	private LLVMValueRef EmitNew(NewValue v, LLVMBuilderRef builder)
	{
		var type = Substitute(v.Type);
		var llvmType = MapTypeSymbol(type);
		if (_typePool.FindNewConstructor(type) is not { } constructor)
			return LLVMValueRef.CreateConstNull(llvmType);
		
		var slot = BuildEntryAlloca(builder, llvmType, "new");
		builder.BuildStore(LLVMValueRef.CreateConstNull(llvmType), slot);
		var info = SubstituteFunction(constructor);
		GetFunctionValue(info);
		var function = current.Functions[info];
		builder.BuildCall2(function.FunctionType, function.FunctionValue, [slot]);
		return builder.BuildLoad2(llvmType, slot, "new");
	}
	
	private static DynType? FindDynDispatch(FunctionInfo function) =>
		function.Symbol.Trait is not null && !function.TypeArguments.IsDefaultOrEmpty &&
		function.TypeArguments[0] is DynType dyn
			? dyn
			: null;
	
	private LLVMValueRef EmitDynCall(FunctionInfo info, DynType dyn, CallValue v, LLVMBuilderRef builder)
	{
		var args = v.Arguments.Select(a => EmitValue(a, builder)).ToArray();
		var trait = _typePool.GetTraitType(info.Symbol.Trait!, TypePool.GetTraitArguments(info));
		var table = EmitDynPath(dyn.Instance!, trait, builder.BuildExtractValue(args[0], 1, "table"), builder);
		args[0] = builder.BuildExtractValue(args[0], 0, "object");
		var slot = (uint)_typePool.GetDynSlot(trait.Trait, info.Symbol) + 1;
		var entry = builder.BuildStructGEP2(GetDynTableType(trait.Trait), table, slot, "entry");
		var method = builder.BuildLoad2(OpaquePointer, entry, "method");
		var parameterTypes = MapParameterTypes(info);
		parameterTypes[0] = OpaquePointer;
		var functionType = LLVMTypeRef.CreateFunction(MapTypeSymbol(info.Signature.ReturnType), parameterTypes);
		return builder.BuildCall2(functionType, method, args);
	}
	
	private LLVMValueRef EmitDynPath(TraitType from, TraitType to, LLVMValueRef table, LLVMBuilderRef builder)
	{
		var owner = from;
		foreach (var next in _typePool.FindDynPath(from, to)!.Value)
		{
			var members = _typePool.FindDynMembers(owner.Trait)!.Value.Length;
			var slot = (uint)(members + _typePool.GetDynRequirements(owner).IndexOf(next) + 1);
			var entry = builder.BuildStructGEP2(GetDynTableType(owner.Trait), table, slot, "entry");
			table = builder.BuildLoad2(OpaquePointer, entry, "table");
			owner = next;
		}
		
		return table;
	}
	
	private LLVMTypeRef GetDynTableType(TraitSymbol trait)
	{
		var entries = _typePool.FindDynMembers(trait)!.Value.Length + _typePool.GetDynRequirements(trait).Length;
		return LLVMTypeRef.CreateStruct([LLVMTypeRef.CreateInt(128), ..Enumerable.Repeat(OpaquePointer, entries)],
			false);
	}
	
	private static DynType GetDynTarget(TypeSymbol type) => type switch
	{
		BorrowType { Target: DynType target } => target,
		PointerType { BaseType: DynType target } => target,
		_ => throw new InvalidOperationException()
	};
	
	private LLVMValueRef GetDynTable(DynConversion conversion)
	{
		var declared = GetDynTarget(conversion.To);
		var trait = GetDynTarget(Substitute(conversion.To)).Instance!;
		var objectType = Substitute(conversion.ObjectType);
		return GetDynTable(trait, objectType,
			declared.Parameter is null ? conversion.Members : GetDynMemberInfos(trait, objectType));
	}
	
	private ImmutableArray<FunctionInfo> GetDynMemberInfos(TraitType trait, TypeSymbol objectType) =>
	[
		.._typePool.GetDynMemberInfos(trait.Trait).Select(member => _typePool.InstantiateFunction(member,
			_typePool.GetWitnessArguments(objectType, member.Symbol, trait.Arguments, [])))
	];
	
	private LLVMValueRef GetDynTable(TraitType trait, TypeSymbol objectType, ImmutableArray<FunctionInfo> members)
	{
		var dyn = _typePool.GetDynType(trait);
		var name = Mangling.MangleInstantiation($"?{Mangling.MangleTypeName(dyn, _modules)}", [objectType], _modules);
		var existing = current.Module.GetNamedGlobal(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		LLVMValueRef[] entries =
		[
			..members.Select(GetFunctionValue),
			.._typePool.GetDynRequirements(trait).Select(required =>
				GetDynTable(required, objectType, GetDynMemberInfos(required, objectType)))
		];
		
		var table = current.Module.AddGlobal(GetDynTableType(trait.Trait), name);
		table.Initializer = LLVMValueRef.CreateConstStruct([EmitTypeId(objectType), ..entries], false);
		table.IsGlobalConstant = true;
		table.Linkage = LLVMLinkage.LLVMInternalLinkage;
		return table;
	}
	
	private LLVMValueRef EmitTypeId(TypeSymbol type)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Mangling.MangleTypeName(type, _modules)));
		return LLVMValueRef.CreateConstIntOfArbitraryPrecision(LLVMTypeRef.CreateInt(128),
			[BinaryPrimitives.ReadUInt64LittleEndian(hash), BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8))]);
	}
	
	private LLVMValueRef EmitDynConversion(DynConversion conversion, ConversionValue v, LLVMBuilderRef builder)
	{
		var pointer = EmitValue(v.Source, builder);
		var value = builder.BuildInsertValue(LLVMValueRef.CreateConstNull(FatPointerType), pointer, 0, "dyn");
		return builder.BuildInsertValue(value, GetDynTable(conversion), 1, "dyn");
	}
	
	private LLVMValueRef EmitDynUpcast(DynUpcastConversion conversion, ConversionValue v, LLVMBuilderRef builder)
	{
		var value = EmitValue(v.Source, builder);
		var from = GetDynTarget(Substitute(conversion.From)).Instance!;
		var to = GetDynTarget(Substitute(conversion.To)).Instance!;
		var table = EmitDynPath(from, to, builder.BuildExtractValue(value, 1, "table"), builder);
		return builder.BuildInsertValue(value, table, 1, "dyn");
	}
	
	private LLVMValueRef EmitDynTest(DynTestConversion conversion, ConversionValue v, LLVMBuilderRef builder)
	{
		var table = builder.BuildExtractValue(EmitValue(v.Source, builder), 1, "table");
		var id = builder.BuildLoad2(LLVMTypeRef.CreateInt(128), table, "type");
		return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, id, EmitTypeId(Substitute(conversion.Tested)), "is");
	}
	
	private LLVMValueRef EmitCall(CallValue v, LLVMBuilderRef builder)
	{
		var info = SubstituteFunction(v.Function);
		if (FindDynDispatch(info) is { } dyn)
			return EmitDynCall(info, dyn, v, builder);
		
		if (FindWitness(info) is NativeWitness native)
			return EmitNativeWitness(info, native, [..v.Arguments.Select(a => EmitValue(a, builder))],
				v.SourceLocation, builder);
		
		GetFunctionValue(info);
		var function = current.Functions[info];
		var args = v.Arguments.Select(a => EmitValue(a, builder)).ToList();
		if (function.CSignature is not { } signature)
			return builder.BuildCall2(function.FunctionType, function.FunctionValue, args.ToArray());
		
		return EmitCCall(signature, function.FunctionType, function.FunctionValue, function.ReturnType, args, builder);
	}
	
	private LLVMValueRef EmitIndirectCall(IndirectCallValue v, LLVMBuilderRef builder)
	{
		var functionType = (FunctionType)Substitute(v.FunctionType);
		var target = EmitValue(v.Target, builder);
		var args = v.Arguments.Select((a, i) => EmitIndirectArgument(a, v.FunctionType, functionType, i, builder))
			.ToList();
		
		var returnType = MapTypeSymbol(functionType.ReturnType);
		if (!functionType.IsExternal)
		{
			var parameterTypes = functionType.ParameterTypes
				.Select((type, i) => MapParameterType(type, functionType.ParameterModes[i]));
			
			var codeType = LLVMTypeRef.CreateFunction(returnType, [OpaquePointer, ..parameterTypes]);
			var code = builder.BuildExtractValue(target, 0, "code");
			var environment = builder.BuildExtractValue(target, 1, "env");
			return builder.BuildCall2(codeType, code, [environment, ..args]);
		}
		
		var signature = _cAbi.Classify(functionType.ParameterTypes.Select(MapTypeSymbol), returnType);
		return EmitCCall(signature, signature.CreateFunctionType(false), target, returnType, args, builder);
	}
	
	private LLVMValueRef EmitIndirectArgument(Value argument, FunctionType declared, FunctionType actual, int index,
		LLVMBuilderRef builder)
	{
		var value = EmitValue(argument, builder);
		if (index >= declared.ParameterTypes.Length)
			return value;
		
		var mode = declared.ParameterModes[index];
		var type = actual.ParameterTypes[index];
		return _typePool.PassesByPointer(declared.ParameterTypes[index], mode) && !_typePool.PassesByPointer(type, mode)
			? builder.BuildLoad2(MapTypeSymbol(type), value, "argument")
			: value;
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
		
		var elementBits = _typePool.SizeTable.GetSize(Substitute(v.PointerType.BaseType)).CountBits(_pointerSize * 8);
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
		if (Substitute(v.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return EmitMatchValue(matchEnum, v, builder);
		
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
	
	private LLVMValueRef EmitMatchValue(EnumSymbol enumType, EnumValue v, LLVMBuilderRef builder)
	{
		var enumCase = enumType.Cases[v.Case.Index];
		if (v.Payload.IsEmpty)
			return EmitMatchConstant(enumType, _typePool.GetMatchValues(enumType, enumCase)[0]);
		
		var value = EmitValue(v.Payload[0], builder);
		var isElse = enumCase.Node.Else is not null;
		var listed = EmitIsListed(enumType, value, isElse ? enumType.Cases : [enumCase], builder);
		PanicIf(isElse ? listed : builder.BuildNot(listed), $"'{enumType.Name}.{enumCase.Name}' can't hold this value",
			v.SourceLocation, builder);
		
		return value;
	}
	
	private LLVMValueRef EmitIsListed(EnumSymbol enumType, LLVMValueRef value, IEnumerable<EnumCaseSymbol> cases,
		LLVMBuilderRef builder)
	{
		var result = _false;
		foreach (var pattern in cases.SelectMany(enumCase => _typePool.GetMatchValues(enumType, enumCase)))
		{
			var test = EmitMatchTest(enumType, value, pattern, builder);
			result = result.Handle == _false.Handle ? test : builder.BuildOr(result, test);
		}
		
		return result;
	}
	
	private LLVMValueRef EmitMatchTag(EnumSymbol enumType, LLVMValueRef value, LLVMBuilderRef builder)
	{
		var tagType = _typePool.GetTagType(enumType);
		var fallback = TypePool.GetElseCase(enumType) ?? enumType.Cases[^1];
		var tag = EmitIntegerConstant(new IntegerConstant(tagType, fallback.Index));
		foreach (var enumCase in enumType.Cases)
		{
			var caseTag = EmitIntegerConstant(new IntegerConstant(tagType, enumCase.Index));
			foreach (var pattern in _typePool.GetMatchValues(enumType, enumCase))
				tag = builder.BuildSelect(EmitMatchTest(enumType, value, pattern, builder), caseTag, tag, "tag");
		}
		
		return tag;
	}
	
	private LLVMValueRef EmitMatchTest(EnumSymbol enumType, LLVMValueRef value, BigInteger pattern,
		LLVMBuilderRef builder)
	{
		if (_typePool.GetMatchedType(enumType) is FunctionType { IsExternal: false })
			value = builder.BuildExtractValue(value, 0, "code");
		
		var expected = value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
			? LLVMValueRef.CreateConstNull(value.TypeOf)
			: EmitMatchConstant(enumType, pattern);
		
		return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value, expected, "listed");
	}
	
	private LLVMValueRef EmitMatchConstant(EnumSymbol enumType, BigInteger pattern)
	{
		var matchedType = _typePool.GetMatchedType(enumType);
		return matchedType switch
		{
			IntegerType integer => EmitIntegerConstant(new IntegerConstant(integer, pattern)),
			_ when matchedType == NativeSymbols.Bool => pattern.IsZero ? _false : _true,
			_ => LLVMValueRef.CreateConstNull(MapTypeSymbol(matchedType))
		};
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
		if (Substitute(v.Target.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return EmitMatchTag(matchEnum, EmitValue(v.Target, builder), builder);
		
		if (!IsAddressable(v.Target))
			return builder.BuildExtractValue(EmitValue(v.Target, builder), 0, "tag");
		
		var enumType = MapTypeSymbol(v.Target.Type);
		var address = builder.BuildStructGEP2(enumType, EmitAddress(v.Target, builder), 0, "tag.addr");
		return builder.BuildLoad2(MapTypeSymbol(v.Type), address, "tag");
	}
	
	private LLVMValueRef EmitEnumPayload(EnumPayloadValue v, LLVMBuilderRef builder)
	{
		if (Substitute(v.Target.Type) is EnumSymbol { IsMatch: true })
			return EmitValue(v.Target, builder);
		
		var name = v.Case.Fields[v.Index].Name;
		return builder.BuildLoad2(MapTypeSymbol(v.Type), EmitEnumPayloadAddress(v, builder), name);
	}
	
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
		
		if (Substitute(v.Target.Type) is EnumSymbol { IsMatch: true })
			return address;
		
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
		FreeConversion or MatchConversion => EmitValue(v.Source, builder),
		FunctionConversion c => EmitValue(new CallValue(c.Function, [v.Source], v.SourceLocation), builder),
		DynConversion c => EmitDynConversion(c, v, builder),
		DynUpcastConversion c => EmitDynUpcast(c, v, builder),
		DynTestConversion c => EmitDynTest(c, v, builder),
		DynCastConversion => builder.BuildExtractValue(EmitValue(v.Source, builder), 0, "object"),
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
		IndexerValue { Target.Type: StringType } => true,
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
		var function = current.Module.GetNamedFunction(name);
		return function.Handle == IntPtr.Zero ? current.Module.AddFunction(name, functionType) : function;
	}
	
	private uint CountBits(TypeSymbol type) =>
		_typePool.SizeTable.GetSize(Substitute(type)).CountBits(_pointerSize * 8);
	
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
		if (Substitute(v.Left.Type) is EnumSymbol { IsMatch: true } matchEnum)
		{
			left = EmitMatchTag(matchEnum, left, builder);
			right = EmitMatchTag(matchEnum, right, builder);
		}
		
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
			{
					IsConstant: false, Op: BinaryOperation.Addition or BinaryOperation.Subtraction
					or BinaryOperation.Multiplication
				} when _config.OverflowChecks =>
				EmitCheckedArithmetic(v.Op, left, right, signed, v.SourceLocation, builder),
			
			{ IsConstant: true, Op: BinaryOperation.Addition or BinaryOperation.WrappingAddition } =>
				LLVMValueRef.CreateConstAdd(left, right),
			{ Op: BinaryOperation.Addition or BinaryOperation.WrappingAddition } =>
				builder.BuildAdd(left, right),
			
			{ IsConstant: true, Op: BinaryOperation.Subtraction or BinaryOperation.WrappingSubtraction } =>
				LLVMValueRef.CreateConstSub(left, right),
			{ Op: BinaryOperation.Subtraction or BinaryOperation.WrappingSubtraction } =>
				builder.BuildSub(left, right),
			
			{ IsConstant: true, Op: BinaryOperation.Multiplication or BinaryOperation.WrappingMultiplication } =>
				LLVMValueRef.CreateConstMul(left, right),
			{ Op: BinaryOperation.Multiplication or BinaryOperation.WrappingMultiplication } =>
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
	
	private LLVMValueRef EmitCheckedArithmetic(BinaryOperation op, LLVMValueRef left, LLVMValueRef right, bool signed,
		SourceLocation location, LLVMBuilderRef builder)
	{
		var type = left.TypeOf;
		var (name, message) = op switch
		{
			BinaryOperation.Addition => ("add", "addition overflow"),
			BinaryOperation.Subtraction => ("sub", "subtraction overflow"),
			_ => ("mul", "multiplication overflow")
		};
		
		var resultType = LLVMTypeRef.CreateStruct([type, LLVMTypeRef.Int1], false);
		var functionType = LLVMTypeRef.CreateFunction(resultType, [type, type]);
		var intrinsic = GetIntrinsic($"llvm.{(signed ? 's' : 'u')}{name}.with.overflow.i{type.IntWidth}", functionType);
		var result = builder.BuildCall2(functionType, intrinsic, [left, right]);
		PanicIf(builder.BuildExtractValue(result, 1), message, location, builder);
		return builder.BuildExtractValue(result, 0);
	}
	
	private LLVMValueRef EmitCheckedNegation(UnaryOpValue v, LLVMBuilderRef builder)
	{
		var operand = EmitValue(v.Operand, builder);
		var type = operand.TypeOf;
		var minimum = builder.BuildShl(LLVMValueRef.CreateConstInt(type, 1),
			LLVMValueRef.CreateConstInt(type, type.IntWidth - 1));
		
		PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, operand, minimum), "negation overflow", v.SourceLocation,
			builder);
		
		return builder.BuildNeg(operand);
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
		if (current.PanicFunction.Handle != IntPtr.Zero)
			return current.PanicFunction;
		
		var panicFunction = current.Module.AddFunction("panic", PanicType);
		panicFunction.Linkage = LLVMLinkage.LLVMInternalLinkage;
		current.PanicFunction = panicFunction;
		using var builder = current.Module.Context.CreateBuilder();
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
		var function = current.Module.GetNamedFunction(name);
		if (function.Handle != IntPtr.Zero)
			return function;
		
		function = current.Module.AddFunction(name, type);
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
		{ Op: UnaryOperation.Negation } when _config.OverflowChecks => EmitCheckedNegation(v, builder),
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
			
			case StringType when v.Target.Type == NativeSymbols.Str:
			{
				var (length, data) = EmitStrParts(v.Target, builder);
				var index = EmitValue(v.Index, builder);
				if (!_config.BoundsChecks)
					return (builder.BuildGEP2(LLVMTypeRef.Int8, data, new[] { index }, "byteptr"), elemType);
				
				PanicIf(builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, index, length), "index out of bounds",
					v.SourceLocation, builder);
				
				return (builder.BuildInBoundsGEP2(LLVMTypeRef.Int8, data, new[] { index }, "byteptr"), elemType);
			}
			
			case StringType:
			{
				var data = EmitValue(v.Target, builder);
				var index = EmitValue(v.Index, builder);
				return (builder.BuildGEP2(LLVMTypeRef.Int8, data, new[] { index }, "byteptr"), elemType);
			}
			
			default:
				throw new InvalidOperationException();
		}
	}
	
	private (LLVMValueRef Length, LLVMValueRef Data) EmitStrParts(Value str, LLVMBuilderRef builder)
	{
		if (!IsAddressable(str))
		{
			var value = EmitValue(str, builder);
			return (builder.BuildExtractValue(value, 0, "length"), builder.BuildExtractValue(value, 1, "data"));
		}
		
		var strType = MapTypeSymbol(NativeSymbols.Str);
		var address = EmitAddress(str, builder);
		var lengthAddress = builder.BuildStructGEP2(strType, address, 0, "length.addr");
		var dataAddress = builder.BuildStructGEP2(strType, address, 1, "data.addr");
		return (builder.BuildLoad2(strType.StructGetTypeAtIndex(0), lengthAddress, "length"),
			builder.BuildLoad2(strType.StructGetTypeAtIndex(1), dataAddress, "data"));
	}
	
	private LLVMValueRef EmitAccessAddress(AccessValue v, LLVMBuilderRef builder)
	{
		var targetPtr = EmitAddress(v.Target, builder);
		var targetType = MapTypeSymbol(v.Target.Type);
		var fieldIndex = (uint)_typePool.GetFieldIndex(Substitute(v.Target.Type), v.Member);
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
		var fieldIndex = (uint)_typePool.GetFieldIndex(Substitute(v.Target.Type), v.Member);
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
		VariableValue v => current.Variables[v.Variable],
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
		var global = CreateGlobal(info);
		if (IsExported(info.Symbol))
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
	}
	
	private LLVMValueRef CreateGlobal(GlobalInfo info)
	{
		var initializer = EmitStaticConstant(info.Value!);
		var global = current.Module.AddGlobal(initializer.TypeOf, info.MangledName);
		global.Initializer = initializer;
		global.IsGlobalConstant = !info.Symbol.IsMutable;
		current.Globals[info.Symbol] = global;
		return global;
	}
	
	private LLVMValueRef DeclareGlobal(GlobalInfo info)
	{
		var global = current.Module.AddGlobal(MapTypeSymbol(info.Type), info.MangledName);
		global.IsGlobalConstant = !info.Symbol.IsMutable;
		current.Globals[info.Symbol] = global;
		return global;
	}
	
	private LLVMValueRef GetGlobal(GlobalInfo info)
	{
		info = SubstituteGlobal(info);
		if (current.Globals.TryGetValue(info.Symbol, out var existing))
			return existing;
		
		if (info.Symbol.ContainingType is NamedTypeSymbol { IsGenericInstance: true } instance)
			return GetInstanceGlobal(info, instance);
		
		var global = DeclareGlobal(info);
		if (!_assemblySymbol.SignatureTable.Globals.ContainsKey(info.Symbol))
			Import(global, info.Symbol);
		else if (info.Symbol.Visibility != Visibility.Public)
			global.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		
		return global;
	}
	
	private LLVMValueRef GetInstanceGlobal(GlobalInfo info, NamedTypeSymbol instance)
	{
		if (_inputs.LibraryStatics.Contains(info.Symbol))
		{
			var imported = DeclareGlobal(info);
			Import(imported, info.Symbol);
			return imported;
		}
		
		var owner = FindOwner(info.File.Module, info.MangledName);
		if (owner == current)
		{
			var outer = substitution;
			substitution = TypePool.CreateMap(instance.Definition.TypeParameters, instance.TypeArguments);
			var defined = CreateGlobal(info);
			substitution = outer;
			if (!_config.IsLibrary && _moduleStates.ContainsKey(info.File.Module) &&
			    info.Symbol.Visibility is Visibility.Private or Visibility.Module)
			{
				defined.Linkage = LLVMLinkage.LLVMInternalLinkage;
				return defined;
			}
			
			ShareDefinition(defined, _config.IsLibrary);
			if (_config.IsLibrary)
				_exportedStatics.Add(info.Symbol);
			
			return defined;
		}
		
		var global = DeclareGlobal(info);
		global.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		if (_requestedGlobals.Add(info.Symbol))
			_pendingGlobals.Enqueue((owner, info));
		
		return global;
	}
	
	private GlobalInfo SubstituteGlobal(GlobalInfo info)
	{
		if (info.Symbol.ContainingType is not NamedTypeSymbol owner || !TypePool.ContainsTypeParameters(owner))
			return info;
		
		var instance = (NamedTypeSymbol)Substitute(owner);
		return _typePool.InstantiateGlobal(instance.GetStaticField(info.Symbol.Name)!, _modules);
	}
	
	private LLVMValueRef EmitGlobalLoad(GlobalInfo info, LLVMBuilderRef builder)
	{
		info = SubstituteGlobal(info);
		return builder.BuildLoad2(MapTypeSymbol(info.Type), GetGlobal(info), info.Symbol.Name);
	}
	
	private LLVMValueRef EmitFunctionReference(FunctionInfo function, TypeSymbol type)
	{
		function = SubstituteFunction(function);
		if (type is FunctionType { IsExternal: false })
		{
			var environment = LLVMValueRef.CreateConstNull(OpaquePointer);
			return LLVMValueRef.CreateConstStruct([GetClosureThunk(function), environment], false);
		}
		
		return function.Symbol.IsExternal ? GetFunctionValue(function) : GetExternalThunk(function);
	}
	
	private LLVMValueRef GetClosureThunk(FunctionInfo function)
	{
		GetFunctionValue(function);
		var target = current.Functions[function];
		var name = $"{target.FunctionValue.Name}$fun";
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var actual = function.Signature;
		var parameterTypes = actual.ParameterTypes
			.Select((type, i) => MapParameterType(type, actual.GetMode(i)))
			.ToArray();
		
		var thunkType = LLVMTypeRef.CreateFunction(target.ReturnType, [OpaquePointer, ..parameterTypes]);
		var thunk = current.Module.AddFunction(name, thunkType);
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(thunk.AppendBasicBlock("entry"));
		var args = parameterTypes
			.Select((_, i) => PassAsDeclared(function, i, thunk.GetParam((uint)i + 1), builder))
			.ToList();
		
		var result = target.CSignature is { } signature
			? EmitCCall(signature, target.FunctionType, target.FunctionValue, target.ReturnType, args, builder)
			: builder.BuildCall2(target.FunctionType, target.FunctionValue, args.ToArray());
		
		if (target.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
			builder.BuildRetVoid();
		else
			builder.BuildRet(result);
		
		return thunk;
	}
	
	private LLVMValueRef PassAsDeclared(FunctionInfo function, int index, LLVMValueRef value, LLVMBuilderRef builder)
	{
		var mode = function.DeclaredSignature.GetMode(index);
		var actual = function.Signature.ParameterTypes[index];
		if (!_typePool.PassesByPointer(function.DeclaredSignature.ParameterTypes[index], mode) ||
		    _typePool.PassesByPointer(actual, mode))
			return value;
		
		var slot = BuildEntryAlloca(builder, MapTypeSymbol(actual), "argument");
		builder.BuildStore(value, slot);
		return slot;
	}
	
	private LLVMValueRef GetExternalThunk(FunctionInfo function)
	{
		GetFunctionValue(function);
		var target = current.Functions[function];
		var name = $"{target.FunctionValue.Name}$ext";
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var parameterTypes = function.Signature.ParameterTypes.Select(MapTypeSymbol).ToArray();
		var signature = _cAbi.Classify(parameterTypes, target.ReturnType);
		var thunk = current.Module.AddFunction(name, signature.CreateFunctionType(false));
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		var isIndirect = signature.Return.Kind == CPassKind.Indirect;
		if (isIndirect)
			AddSretAttribute(thunk, signature.Return.Type, false);
		
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(thunk.AppendBasicBlock("entry"));
		var first = isIndirect ? 1u : 0u;
		var args = parameterTypes
			.Select((type, i) => ReceiveCArgument(signature.Parameters[i], thunk.GetParam(first + (uint)i), type,
				builder))
			.Select((value, i) => PassAsDeclared(function, i, value, builder))
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
		function = SubstituteFunction(function);
		if (current.Functions.TryGetValue(function, out var existing))
			return existing.FunctionValue;
		
		if (FindWitness(function) is { } witness)
			return BuildWitness(function, witness);
		
		if (function.Symbol.Syntax is NativeConstructorNode native)
			return BuildNativeConstructor(function, native);
		
		if (!function.TypeArguments.IsDefaultOrEmpty)
			return Instantiate(function);
		
		var value = CreateFunction(current.Module, function).FunctionValue;
		if (function.Symbol.Kind == FunctionKind.External)
		{
			if (function.Origin is { } origin)
				current.ExternalLibraries.Add(origin);
			
			value.Linkage = LLVMLinkage.LLVMExternalLinkage;
		}
		else if (!_assemblySymbol.SignatureTable.Functions.ContainsKey(function.Symbol))
		{
			Import(value, function.Symbol);
		}
		else if (function.Symbol.Visibility != Visibility.Public)
		{
			value.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		}
		
		return value;
	}
	
	private Witness? FindWitness(FunctionInfo function)
	{
		if (function.Symbol.Trait is null && function.Symbol.Syntax is not ConstructorConstraintNode ||
		    function.TypeArguments.IsDefaultOrEmpty || TypePool.ContainsTypeParameters(function.TypeArguments[0]))
			return null;
		
		var self = function.TypeArguments[0];
		var witness = function.Symbol.Trait is null
			? _typePool.FindConstructorWitness(self, function.Signature)
			: _typePool.FindWitness(self, function.Symbol, TypePool.GetTraitArguments(function));
		
		return witness is FunctionWitness { Function: var target } && target == function.Symbol ? null : witness;
	}
	
	private LLVMValueRef BuildWitness(FunctionInfo function, Witness witness)
	{
		var definition = _typePool.GetGenericDefinition(function.Symbol);
		var name = Mangling.MangleInstantiation(definition.MangledName ?? function.Symbol.Name, function.Signature,
			function.TypeArguments, _modules);
		
		var thunk = CreateFunction(current.Module, function, name);
		var value = thunk.FunctionValue;
		value.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(value.AppendBasicBlock("entry"));
		var parameters = function.Signature.ParameterTypes.Select((_, i) => value.GetParam((uint)i)).ToList();
		var result = witness switch
		{
			FunctionWitness target => CallWitness(function, target, parameters, builder),
			NativeWitness native => EmitNativeWitness(function, native, parameters,
				function.Symbol.Syntax.SourceLocation, builder),
			MemberwiseWitness => EmitConstruction(function, parameters, true, builder),
			DefaultWitness => EmitConstruction(function, parameters, false, builder),
			ConversionWitness conversion => EmitConversionWitness(function, conversion.Conversion, parameters, builder),
			_ => throw new InvalidOperationException()
		};
		
		if (thunk.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
			builder.BuildRetVoid();
		else
			builder.BuildRet(result);
		
		return value;
	}
	
	private LLVMValueRef BuildNativeConstructor(FunctionInfo function, NativeConstructorNode native)
	{
		var value = CreateFunction(current.Module, function).FunctionValue;
		value.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(value.AppendBasicBlock("entry"));
		var result = native.Intrinsic switch
		{
			NativeMemberIntrinsic.StrNew => builder.BuildInsertValue(
				builder.BuildInsertValue(MapTypeSymbol(NativeSymbols.Str).Undef, value.GetParam(2), 0, "str"),
				value.GetParam(1), 1, "str"),
			_ => throw new InvalidOperationException()
		};
		
		builder.BuildStore(result, value.GetParam(0));
		builder.BuildRetVoid();
		return value;
	}
	
	private LLVMValueRef CallWitness(FunctionInfo function, FunctionWitness witness, List<LLVMValueRef> parameters,
		LLVMBuilderRef builder)
	{
		var self = function.TypeArguments[0];
		var declared = function.TypeArguments.TakeLast(function.Symbol.DeclaredTypeParameters.Length);
		var arguments = _typePool.GetWitnessArguments(self, witness.Function, TypePool.GetTraitArguments(function),
			declared);
		
		var target = _typePool.InstantiateFunction(witness.Info, arguments);
		GetFunctionValue(target);
		var callee = current.Functions[target];
		var args = parameters.Select((parameter, i) => PassToWitness(function, target, i, parameter, builder)).ToList();
		return callee.CSignature is { } signature
			? EmitCCall(signature, callee.FunctionType, callee.FunctionValue, callee.ReturnType, args, builder)
			: builder.BuildCall2(callee.FunctionType, callee.FunctionValue, args.ToArray());
	}
	
	private LLVMValueRef PassToWitness(FunctionInfo function, FunctionInfo target, int index, LLVMValueRef value,
		LLVMBuilderRef builder)
	{
		var mode = function.DeclaredSignature.GetMode(index);
		var fromPointer = _typePool.PassesByPointer(function.DeclaredSignature.ParameterTypes[index], mode);
		if (fromPointer == _typePool.PassesByPointer(target.DeclaredSignature.ParameterTypes[index], mode))
			return value;
		
		var type = MapTypeSymbol(function.Signature.ParameterTypes[index]);
		if (fromPointer)
			return builder.BuildLoad2(type, value, "argument");
		
		var slot = BuildEntryAlloca(builder, type, "argument");
		builder.BuildStore(value, slot);
		return slot;
	}
	
	private LLVMValueRef EmitNativeWitness(FunctionInfo function, NativeWitness witness, List<LLVMValueRef> parameters,
		SourceLocation location, LLVMBuilderRef builder)
	{
		var operation = witness.Operation;
		var operands = parameters
			.Select(Value (parameter, i) =>
				new VariableValue(BindWitnessParameter(function, i, parameter, builder), location))
			.ToList();
		
		if (witness.IsCompound)
		{
			var place = new UnaryOpValue(operation.ReturnType, operands[0], UnaryOperation.Dereference, location);
			var value = new BinOpValue(operation.ReturnType, place, operands[1],
				OperationMapping.ToBinaryOperation(operation.Op), location);
			
			EmitValue(new AssignValue(operation.ReturnType, place, value, location), builder);
			return default;
		}
		
		if (operation.ParameterTypes[0] is EnumSymbol enumType)
			operands =
			[
				..operands.Select(Value (operand) => new EnumTagValue(_typePool.GetTagType(enumType), operand,
					location))
			];
		
		Value result = operands is [var single]
			? new UnaryOpValue(operation.ReturnType, single, OperationMapping.ToUnaryOperation(operation.Op), location)
			: new BinOpValue(operation.ReturnType, operands[0], operands[1],
				OperationMapping.ToBinaryOperation(operation.Op), location);
		
		return EmitValue(result, builder);
	}
	
	private LLVMValueRef EmitConstruction(FunctionInfo function, List<LLVMValueRef> parameters, bool memberwise,
		LLVMBuilderRef builder)
	{
		var location = function.Symbol.Syntax.SourceLocation;
		var type = function.TypeArguments[0];
		var self = new UnaryOpValue(type, new VariableValue(BindWitnessParameter(function, 0, parameters[0], builder),
			location), UnaryOperation.Dereference, location);
		
		EmitValue(new AssignValue(type, self, new ZeroValue(type), location), builder);
		if (!memberwise)
			return default;
		
		var fields = _typePool.GetMembers(type).OfType<FieldSymbol>().ToList();
		for (var i = 0; i < fields.Count; i++)
		{
			var fieldType = _typePool.GetTypeOfMember(fields[i]);
			var value = new VariableValue(BindWitnessParameter(function, i + 1, parameters[i + 1], builder), location);
			var field = new AccessValue(fieldType, self, fields[i], location);
			EmitValue(new AssignValue(fieldType, field, value, location), builder);
		}
		
		return default;
	}
	
	private LLVMValueRef EmitConversionWitness(FunctionInfo function, Conversion conversion,
		List<LLVMValueRef> parameters, LLVMBuilderRef builder)
	{
		var location = function.Symbol.Syntax.SourceLocation;
		var type = function.TypeArguments[0];
		var self = new UnaryOpValue(type, new VariableValue(BindWitnessParameter(function, 0, parameters[0], builder),
			location), UnaryOperation.Dereference, location);
		
		var value = new VariableValue(BindWitnessParameter(function, 1, parameters[1], builder), location);
		EmitValue(new AssignValue(type, self, new ConversionValue(value, conversion, location), location), builder);
		return default;
	}
	
	private VariableInfo BindWitnessParameter(FunctionInfo function, int index, LLVMValueRef value,
		LLVMBuilderRef builder)
	{
		var type = function.Signature.ParameterTypes[index];
		var info = new VariableInfo(new ParameterSymbol($"${index}", function.Symbol.Syntax.SourceLocation), type);
		if (_typePool.PassesByPointer(function.DeclaredSignature.ParameterTypes[index],
			    function.DeclaredSignature.GetMode(index)))
		{
			current.Variables[info] = value;
			return info;
		}
		
		var slot = BuildEntryAlloca(builder, MapTypeSymbol(type), info.Symbol.Name);
		builder.BuildStore(value, slot);
		current.Variables[info] = slot;
		return info;
	}
	
	private LLVMValueRef Instantiate(FunctionInfo function)
	{
		var definition = _typePool.GetGenericDefinition(function.Symbol);
		var name = Mangling.MangleInstantiation(definition.MangledName ?? function.Symbol.Name, function.Signature,
			function.TypeArguments, _modules);
		
		var value = CreateFunction(current.Module, function, name).FunctionValue;
		var module = function.File!.Module;
		var owner = FindOwner(module, name);
		var hasBody = _genericBodies.ContainsKey(function.Symbol);
		if (owner != current || !hasBody)
			value.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		else if (_moduleStates.ContainsKey(module) && IsObjectLocal(function.Symbol))
			value.Linkage = LLVMLinkage.LLVMInternalLinkage;
		else
			ShareDefinition(value, false);
		
		if (hasBody && _requestedInstantiations.Add(name))
			_pendingInstantiations.Enqueue((owner, function));
		
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
		if (Substitute(constant.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return constant.Payload is [var value]
				? EmitStaticConstant(value)
				: EmitMatchConstant(matchEnum, _typePool.GetMatchValues(matchEnum, constant.Case)[0]);
		
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
		if (Substitute(constant.Type) is EnumSymbol { IsMatch: true } matchEnum)
			return EmitMatchConstant(matchEnum, constant.Tag);
		
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
		if (current.Strings.TryGetValue(bytes, out var existing))
			return existing;
		
		var byteType = MapTypeSymbol(NativeSymbols.UInt8);
		var byteValues = bytes.Select(b => LLVMValueRef.CreateConstInt(byteType, b));
		var arrayValue = LLVMValueRef.CreateConstArray(byteType, [..byteValues]);
		
		var global = current.Module.AddGlobal(arrayValue.TypeOf, string.Empty);
		global.Initializer = arrayValue;
		global.IsGlobalConstant = true;
		global.Linkage = LLVMLinkage.LLVMLinkerPrivateLinkage;
		global.HasUnnamedAddr = true;
		
		var ptr = LLVMValueRef.CreateConstInBoundsGEP2(byteType, global,
			[LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)]);
		
		current.Strings[bytes] = ptr;
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

public sealed record CodeGenInputs
{
	public IEnumerable<LoweredFunction> GenericFunctions { get; init; } = [];
	public IReadOnlySet<Symbol> GenericReferences { get; init; } = ImmutableHashSet<Symbol>.Empty;
	public IReadOnlySet<GlobalSymbol> LibraryStatics { get; init; } = ImmutableHashSet<GlobalSymbol>.Empty;
	public IReadOnlySet<Symbol> StaticLibrarySymbols { get; init; } = ImmutableHashSet<Symbol>.Empty;
}

public sealed record CodeGenConfig
(
	OutputConfig OutputConfig,
	TargetConfig? TargetConfig, // if null, compiles for current platform
	OptimizeMode OptimizeMode
)
{
	public bool BoundsChecks { get; init; } = true;
	public bool OverflowChecks { get; init; }
	public bool IsLibrary { get; init; }
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