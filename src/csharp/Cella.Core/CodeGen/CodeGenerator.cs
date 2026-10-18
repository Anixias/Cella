using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.CodeGen.Extensions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;
using LLVMSharp.Interop;

// ReSharper disable StringLiteralTypo

namespace Cella.Core.CodeGen;

internal readonly record struct LLVMFunctionInfo
(
	LLVMValueRef FunctionValue,
	LLVMTypeRef FunctionType,
	LLVMTypeRef ReturnType,
	CSignature? CSignature,
	uint? ResultParameter = null
);

// Create type definitions/forward declarations
// Then, create function definitions/forward declarations
public sealed unsafe partial class CodeGenerator : IDisposable
{
	private const ulong LargeValueBytes = 16;
	
	public DiagnosticList Diagnostics { get; } = new();
	
	private static readonly Lock _initLock = new();
	private static bool isInitialized;
	
	public static void Init()
	{
		lock (_initLock)
		{
			if (!isInitialized)
				InitializeTargets();
			
			isInitialized = true;
		}
	}
	
	private static void InitializeTargets()
	{
		LLVM.LinkInMCJIT();
		LLVM.InitializeX86TargetInfo();
		LLVM.InitializeX86Target();
		LLVM.InitializeX86TargetMC();
		LLVM.InitializeX86AsmParser();
		LLVM.InitializeX86AsmPrinter();
		LLVM.InitializeAArch64TargetInfo();
		LLVM.InitializeAArch64Target();
		LLVM.InitializeAArch64TargetMC();
		LLVM.InitializeAArch64AsmParser();
		LLVM.InitializeAArch64AsmPrinter();
		LLVM.InitializeWebAssemblyTargetInfo();
		LLVM.InitializeWebAssemblyTarget();
		LLVM.InitializeWebAssemblyTargetMC();
		LLVM.InitializeWebAssemblyAsmParser();
		LLVM.InitializeWebAssemblyAsmPrinter();
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
	private readonly HashSet<(TypeSymbol, SourceLocation)> _reportedSizes = [];
	private readonly LLVMPassBuilderOptionsRef _passBuilderOptions = LLVMPassBuilderOptionsRef.Create();
	private readonly LLVMContextRef _context = LLVMContextRef.Create();
	private readonly LLVMValueRef _true;
	private readonly LLVMValueRef _false;
	private readonly HashSet<FunctionSymbol> _sharedFunctions;
	private readonly ModuleIndex _modules;
	private readonly Dictionary<FunctionSymbol, LoweredFunction> _genericBodies;
	private readonly CodeGenInputs _inputs;
	private readonly Dictionary<ModuleSymbol, ModuleState> _moduleStates = [];
	private readonly Dictionary<string, ModuleState> _foreignOwners = [];
	private readonly List<ExportName> _exportNames = [];
	private readonly HashSet<GlobalSymbol> _exportedStatics = [];
	private readonly Queue<(ModuleState Owner, FunctionInfo Info)> _pendingInstantiations = [];
	private readonly Queue<(ModuleState Owner, GlobalInfo Info)> _pendingGlobals = [];
	private readonly HashSet<string> _requestedInstantiations = [];
	private readonly HashSet<GlobalSymbol> _requestedGlobals = [];
	private readonly Dictionary<TypeSymbol, bool> _largeTypes = [];
	private readonly Dictionary<LLVMValueRef, int> _temporaryIndices = [];
	
	private readonly Dictionary<(LLVMValueRef Function, LLVMTypeRef Type, int Index), LLVMValueRef>
		_temporarySlots = [];
	
	private ModuleState current = null!;
	private LLVMFunctionInfo currentFunction;
	private RuntimeChecks checks;
	
	private IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> substitution =
		new Dictionary<TypeParameterSymbol, TypeSymbol>();
	
	public CodeGenerator(AssemblySymbol assemblySymbol, TypePool typePool, CodeGenConfig config, ModuleIndex modules,
		CodeGenInputs inputs)
	{
		_modules = modules;
		_inputs = inputs;
		_genericBodies = inputs.GenericFunctions.ToDictionary(static function => function.Info.Symbol);
		Init();
		_true = LLVMValueRef.CreateConstInt(_context.Int1Type, 1uL);
		_false = LLVMValueRef.CreateConstInt(_context.Int1Type, 0uL);
		_assemblySymbol = assemblySymbol;
		_typePool = typePool;
		_config = config;
		checks = config.Checks;
		(_dataLayoutStr, TargetTriple, _targetMachine, _pointerSize) = config.GetDataLayout();
		_targetData = LLVMTargetDataRef.FromStringRepresentation(_dataLayoutStr);
		_cAbi = new CAbi(_targetData, TargetTriple);
		_sharedFunctions = assemblySymbol.SignatureTable.Globals.Values
			.Where(static global => global.Symbol.Visibility is Visibility.Project or Visibility.Public)
			.SelectMany(static global => FindFunctions(global.Value))
			.Concat(assemblySymbol.SignatureTable.Functions.Values
				.SelectMany(static function => function.Signature.Defaults.SelectMany(FindFunctions)))
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
	
	public bool HasStaticDrops { get; private set; }
	public IReadOnlyList<ExportName> ExportNames => _exportNames;
	
	private bool IsObjectLocal(FunctionSymbol function) =>
		function.Visibility is Visibility.Private or Visibility.Module &&
		function.Kind is not (FunctionKind.Destructor or FunctionKind.Constructor) && !function.OwnsCaptures &&
		!_sharedFunctions.Contains(function) && !_inputs.Exports.Contains(function);
	
	private bool IsExported(Symbol symbol) => SharesExports && _inputs.Exports.Contains(symbol);
	
	private bool SharesExports => _config.ExportsSymbols || TargetTriple.Contains("-linux");
	
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
	
	private void AddExternalName(LLVMValueRef target, LLVMTypeRef type, string name, bool isData, bool isPublic)
	{
		using var empty = new MarshaledString(string.Empty);
		var alias = new LLVMValueRef((nint)LLVM.AddAlias2((LLVMOpaqueModule*)current.Module.Handle,
			(LLVMOpaqueType*)type.Handle, 0, (LLVMOpaqueValue*)target.Handle, empty));
		
		var declared = isData ? current.Module.GetNamedGlobal(name) : current.Module.GetNamedFunction(name);
		if (declared.Handle != nint.Zero)
		{
			declared.ReplaceAllUsesWith(alias);
			foreach (var (function, info) in current.Functions.Where(pair => pair.Value.FunctionValue == declared)
				         .ToList())
				current.Functions[function] = info with { FunctionValue = alias };
			
			foreach (var global in current.Globals.Where(pair => pair.Value == declared).Select(static pair => pair.Key)
				         .ToList())
				current.Globals[global] = alias;
			
			if (isData)
				declared.DeleteGlobal();
			else
				declared.DeleteFunction();
		}
		
		alias.Name = name;
		
		if (!isPublic)
			return;
		
		if (_config.ExportsSymbols)
			alias.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		
		_exportNames.Add(new(name, isData));
	}
	
	private ModuleState FindOwner(ModuleSymbol module, string name)
	{
		if (_moduleStates.TryGetValue(module, out var owner))
			return owner;
		
		_foreignOwners.TryAdd(name, current);
		return _foreignOwners[name];
	}
	
	private string? RunOptimizationPass(LLVMModuleRef module)
	{
		using var passStr = new MarshaledString(_config.OptimizeMode switch
		{
			OptimizeMode.Release => "default<O3>",
			_ => "default<O0>"
		});
		
		var error = LLVM.RunPasses((LLVMOpaqueModule*)module.Handle, passStr,
			(LLVMOpaqueTargetMachine*)_targetMachine.Handle, (LLVMOpaquePassBuilderOptions*)_passBuilderOptions.Handle);
		
		if (error == null)
			return null;
		
		var message = LLVM.GetErrorMessage(error);
		try
		{
			return SpanExtensions.AsString(message);
		}
		finally
		{
			LLVM.DisposeErrorMessage(message);
		}
	}
	
	private void MapNativeSymbols()
	{
		// TODO Map address spaces based on target?
		
		var intSize = _context.GetIntType(_pointerSize * 8);
		current.Types[NativeSymbols.Void] = _context.VoidType;
		current.Types[NativeSymbols.Never] = _context.VoidType;
		current.Types[NativeSymbols.VoidPtr] = LLVMTypeRef.CreatePointer(_context.VoidType, 0u);
		current.Types[NativeSymbols.Int8] = _context.Int8Type;
		current.Types[NativeSymbols.Int16] = _context.Int16Type;
		current.Types[NativeSymbols.Int32] = _context.Int32Type;
		current.Types[NativeSymbols.Int64] = _context.Int64Type;
		current.Types[NativeSymbols.Int128] = _context.Int128Type;
		current.Types[NativeSymbols.IntSize] = intSize;
		current.Types[NativeSymbols.UInt8] = _context.Int8Type;
		current.Types[NativeSymbols.UInt16] = _context.Int16Type;
		current.Types[NativeSymbols.UInt32] = _context.Int32Type;
		current.Types[NativeSymbols.UInt64] = _context.Int64Type;
		current.Types[NativeSymbols.UInt128] = _context.Int128Type;
		current.Types[NativeSymbols.UIntSize] = intSize;
		current.Types[NativeSymbols.Float32] = _context.FloatType;
		current.Types[NativeSymbols.Float64] = _context.DoubleType;
		current.Types[NativeSymbols.Char] = _context.Int32Type;
		current.Types[NativeSymbols.Bool] = _context.Int1Type;
		current.Types[NativeSymbols.Str] =
			_context.GetStructType([intSize, LLVMTypeRef.CreatePointer(_context.Int8Type, 0u)], false);
		
		current.Types[NativeSymbols.CStr] = LLVMTypeRef.CreatePointer(_context.Int8Type, 0u);
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
			BuildStaticDropGlue(states);
			var hasRegistry = states.Count > 0 && _config.ExportsSymbols && (_inputs.HostedRegistries.Count > 0 ||
			                                                                 _inputs.Exports.Any(static symbol =>
				                                                                 symbol is GlobalSymbol
				                                                                 {
					                                                                 ContainingType: NamedTypeSymbol
					                                                                 {
						                                                                 IsGenericDefinition: true
					                                                                 }
				                                                                 }));
			
			if (hasRegistry)
			{
				current = states[0];
				EmitStaticRegistry();
			}
			
			HasStaticDrops = states.Count > 0 && (hasRegistry || _inputs.ContainedAssemblies.Length > 0 ||
			                                      states.Any(static state => state.DroppedStatics.Count > 0));
			
			if (HasStaticDrops)
				EmitStaticDrops(states, hasRegistry);
			else
				RemoveStaticDropCalls(states);
			
			var fileNames = GetFileNames(states);
			return [..states.Select(state => Finish(state, fileNames[state]))];
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
		var llvmModule = _context.CreateModuleWithName(module.Symbol.Name);
		llvmModule.Target = TargetTriple;
		llvmModule.DataLayout = _dataLayoutStr;
		current = new ModuleState(module, llvmModule, llvmModule.CreateDIBuilder());
		_moduleStates[module.Symbol] = current;
		MapNativeSymbols();
		if (_config.DebugInfo)
			StartDebugInfo(module);
		
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
			var function = GetFunctionValue(pending.Info);
			substitution = TypePool.CreateMap(pending.Info.Symbol.TypeParameters, pending.Info.TypeArguments);
			if (ReportTooLargeInstances(pending.Info.Symbol))
			{
				using var builder = current.Module.Context.CreateBuilder();
				builder.PositionAtEnd(_context.AppendBasicBlock(function, "entry"));
				builder.BuildUnreachable();
			}
			else
				BuildFunction(_genericBodies[pending.Info.Symbol], pending.Info);
			
			substitution = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		}
	}
	
	private bool ReportTooLargeInstances(FunctionSymbol function)
	{
		var errorCount = Diagnostics.ErrorCount;
		foreach (var (type, location) in _typePool.GenericSizeChecks.GetValueOrDefault(function) ?? [])
		{
			var instance = Substitute(type);
			if (_reportedSizes.Add((instance, location)) && _typePool.IsTooLarge(instance, _pointerSize * 8))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{instance.Name}' is too large"));
		}
		
		foreach (var (type, location) in _typePool.GenericNullChecks.GetValueOrDefault(function) ?? [])
		{
			var instance = Substitute(type);
			if (instance is not PointerType && instance != NativeSymbols.CStr &&
			    _reportedSizes.Add((instance, location)))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"'{instance.Name}' doesn't accept 'null'"));
		}
		
		return Diagnostics.ErrorCount > errorCount;
	}
	
	private static Dictionary<ModuleState, string> GetFileNames(List<ModuleState> states) => states
		.GroupBy(static state => state.Lowered.Symbol.Name, StringComparer.OrdinalIgnoreCase)
		.SelectMany(static group => group
			.OrderBy(static state => state.Lowered.Symbol.Name, StringComparer.Ordinal)
			.Select(static (state, index) => (State: state,
				Name: index == 0 ? state.Lowered.Symbol.Name : $"{state.Lowered.Symbol.Name}-{index}")))
		.ToDictionary(static entry => entry.State, static entry => entry.Name);
	
	private static string DescribeInvalidModule(string module, string message) =>
		$"Internal compiler error: invalid code generated for module '{module}'{Environment.NewLine}{message.TrimEnd()}";
	
	private CodeGenResult Finish(ModuleState state, string fileName)
	{
		current = state;
		var llvmModule = state.Module;
		var name = state.Lowered.Symbol.Name;
		AddUnwindTables(llvmModule);
		if (TargetTriple.Contains("windows"))
			PlaceFunctionsInSections(llvmModule);
		
		state.DiBuilder.DIBuilderFinalize();
		
		string message;
		if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out message))
			return CodeGenResult.Failure with { ErrorMessage = DescribeInvalidModule(name, message) };
		
		if (RunOptimizationPass(llvmModule) is { } passError)
			return CodeGenResult.Failure with { ErrorMessage = passError };
		
		if (!llvmModule.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out message))
			return CodeGenResult.Failure with { ErrorMessage = DescribeInvalidModule(name, message) };
		
		if (!Directory.Exists(_config.OutputConfig.Directory))
			Directory.CreateDirectory(_config.OutputConfig.Directory);
		
		var irFilePath = Path.Combine(_config.OutputConfig.Directory, $"{fileName}.ll");
		var assemblyFilePath = Path.Combine(_config.OutputConfig.Directory, $"{fileName}.s");
		if (!_config.OutputConfig.EmitIR)
			File.Delete(irFilePath);
		
		if (!_config.OutputConfig.EmitAssembly)
			File.Delete(assemblyFilePath);
		
		if (_config.OutputConfig.EmitIR)
		{
			if (!llvmModule.TryPrintToFile(irFilePath, out message))
				return CodeGenResult.Failure with { ErrorMessage = DescribeWriteFailure(irFilePath, message) };
		}
		
		if (_config.OutputConfig.EmitAssembly)
		{
			using var assemblyModule = llvmModule.Clone();
			if (!_targetMachine.TryEmitToFile(assemblyModule, assemblyFilePath, LLVMCodeGenFileType.LLVMAssemblyFile,
				    out message))
				return CodeGenResult.Failure with { ErrorMessage = DescribeWriteFailure(assemblyFilePath, message) };
		}
		
		var objectFilePath = Path.Combine(_config.OutputConfig.Directory, $"{fileName}.o");
		if (_targetMachine.TryEmitToFile(llvmModule, objectFilePath, LLVMCodeGenFileType.LLVMObjectFile, out message))
			return new(true, objectFilePath, null, state.ExternalLibraries)
			{
				ImportedFunctions =
				[
					..state.ExternalFunctions.Where(name => IsReferenced(llvmModule.GetNamedFunction(name)))
				],
				ImportedGlobals =
				[
					..state.ExternalGlobals.Where(name => IsReferenced(llvmModule.GetNamedGlobal(name)))
				]
			};
		
		return CodeGenResult.Failure with { ErrorMessage = DescribeWriteFailure(objectFilePath, message) };
	}
	
	private static bool IsReferenced(LLVMValueRef value) =>
		value.Handle != nint.Zero && value.FirstUse.Handle != nint.Zero;
	
	private static string DescribeWriteFailure(string path, string message) => $"Cannot write '{path}': {message}";
	
	public bool UsesWideEntry =>
		_assemblySymbol.EntryPoint is { Signature.ParameterTypes.IsEmpty: false } && TargetTriple.Contains("windows");
	
	public CodeGenResult GenerateLauncher(string startFunction)
	{
		var takesArguments = _assemblySymbol.EntryPoint is { Signature.ParameterTypes.IsEmpty: false };
		var isWide = UsesWideEntry;
		using var module = _context.CreateModuleWithName("launcher");
		module.Target = TargetTriple;
		module.DataLayout = _dataLayoutStr;
		var type = LLVMTypeRef.CreateFunction(_context.Int32Type,
			takesArguments ? [_context.Int32Type, OpaquePointer] : []);
		
		var main = module.AddFunction(isWide ? "wmain" : "main", type);
		var start = module.AddFunction(startFunction, type);
		if (TargetTriple.Contains("windows"))
			start.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
		
		using var builder = _context.CreateBuilder();
		builder.PositionAtEnd(main.AppendBasicBlock("entry"));
		builder.BuildRet(builder.BuildCall2(type, start, takesArguments ? [main.GetParam(0), main.GetParam(1)] : []));
		
		if (!module.TryVerify(LLVMVerifierFailureAction.LLVMReturnStatusAction, out var message))
			return CodeGenResult.Failure with { ErrorMessage = DescribeInvalidModule("launcher", message) };
		
		Directory.CreateDirectory(_config.OutputConfig.Directory);
		var objectFilePath = Path.Combine(_config.OutputConfig.Directory, "launcher.o");
		return _targetMachine.TryEmitToFile(module, objectFilePath, LLVMCodeGenFileType.LLVMObjectFile, out message)
			? new(true, objectFilePath, null, [])
			: CodeGenResult.Failure with { ErrorMessage = DescribeWriteFailure(objectFilePath, message) };
	}
	
	private static void PlaceFunctionsInSections(LLVMModuleRef module)
	{
		for (var function = module.FirstFunction; function.Handle != nint.Zero; function = function.NextFunction)
		{
			if (function.BasicBlocksCount == 0 || LLVM.GetComdat((LLVMOpaqueValue*)function.Handle) != null ||
			    function.Linkage is not (LLVMLinkage.LLVMExternalLinkage or LLVMLinkage.LLVMDLLExportLinkage))
				continue;
			
			using var name = new MarshaledString(function.Name);
			var comdat = LLVM.GetOrInsertComdat((LLVMOpaqueModule*)module.Handle, name);
			LLVM.SetComdatSelectionKind(comdat, LLVMComdatSelectionKind.LLVMNoDeduplicateComdatSelectionKind);
			LLVM.SetComdat((LLVMOpaqueValue*)function.Handle, comdat);
		}
	}
	
	private void AddUnwindTables(LLVMModuleRef module)
	{
		const uint asynchronous = 2;
		AddModuleFlag("uwtable", asynchronous);
		for (var function = module.FirstFunction; function.Handle != nint.Zero; function = function.NextFunction)
		{
			if (function.BasicBlocksCount > 0)
				AddFunctionAttribute(function, "uwtable", asynchronous);
		}
	}
	
	private static void AddFunctionAttribute(LLVMValueRef function, string name, ulong value = 0)
	{
		using var attributeName = new MarshaledString(name);
		var attribute = LLVM.CreateEnumAttribute((LLVMOpaqueContext*)function.TypeOf.Context.Handle,
			LLVM.GetEnumAttributeKindForName(attributeName, (nuint)attributeName.Length), value);
		
		LLVM.AddAttributeAtIndex((LLVMOpaqueValue*)function.Handle, LLVMAttributeIndex.LLVMAttributeFunctionIndex,
			attribute);
	}
	
	private void AddCAttributes(LLVMValueRef value, CSignature signature, bool isCall)
	{
		var first = signature.Return.Kind == CPassKind.Indirect ? 2 : 1;
		if (signature.Return.Kind == CPassKind.Direct)
			AddExtension(value, LLVMAttributeIndex.LLVMAttributeReturnIndex, signature.Return.Extension, isCall);
		
		for (var i = 0; i < signature.Parameters.Length; i++)
		{
			var parameter = signature.Parameters[i];
			var index = (LLVMAttributeIndex)(first + i);
			if (parameter.Kind == CPassKind.Direct)
				AddExtension(value, index, parameter.Extension, isCall);
			
			if (!parameter.IsByValue)
				continue;
			
			var alignment = Math.Max(8u, _targetData.ABIAlignmentOfType(parameter.Type));
			AddAttribute(value, index, CreateTypeAttribute(value, "byval", parameter.Type), isCall);
			AddAttribute(value, index, CreateEnumAttribute(value, "align", alignment), isCall);
		}
	}
	
	private static void AddExtension(LLVMValueRef value, LLVMAttributeIndex index, CExtension extension,
		bool isCall)
	{
		if (extension != CExtension.None)
			AddAttribute(value, index,
				CreateEnumAttribute(value, extension == CExtension.Sign ? "signext" : "zeroext", 0), isCall);
	}
	
	private static LLVMOpaqueAttributeRef* CreateEnumAttribute(LLVMValueRef value, string name, ulong argument)
	{
		using var marshaled = new MarshaledString(name);
		return LLVM.CreateEnumAttribute((LLVMOpaqueContext*)value.TypeOf.Context.Handle,
			LLVM.GetEnumAttributeKindForName(marshaled, (nuint)marshaled.Length), argument);
	}
	
	private static LLVMOpaqueAttributeRef* CreateTypeAttribute(LLVMValueRef value, string name, LLVMTypeRef type)
	{
		using var marshaled = new MarshaledString(name);
		return LLVM.CreateTypeAttribute((LLVMOpaqueContext*)value.TypeOf.Context.Handle,
			LLVM.GetEnumAttributeKindForName(marshaled, (nuint)marshaled.Length), (LLVMOpaqueType*)type.Handle);
	}
	
	private static void AddAttribute(LLVMValueRef value, LLVMAttributeIndex index, LLVMOpaqueAttributeRef* attribute,
		bool isCall)
	{
		if (isCall)
			LLVM.AddCallSiteAttribute((LLVMOpaqueValue*)value.Handle, index, attribute);
		else
			LLVM.AddAttributeAtIndex((LLVMOpaqueValue*)value.Handle, index, attribute);
	}
	
	private void BuildModule(LoweredModule module)
	{
		foreach (var file in module.Files)
		{
			// Create and map external functions
			foreach (var function in file.ExternalFunctions)
			{
				if (function.Origin is { } origin)
					current.ExternalLibraries.Add(origin);
				
				var llvmFunction = CreateFunction(current.Module, function).FunctionValue;
				llvmFunction.Linkage = LLVMLinkage.LLVMExternalLinkage;
				llvmFunction.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
				current.ExternalFunctions.Add(llvmFunction.Name);
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
				
				if (_inputs.Exports.Contains(function.Info.Symbol))
					_exportNames.Add(new(llvmFunction.Name, false));
				
				if (function.Info.Symbol is { Syntax: FunctionNode, ExternalName: { } externalName } symbol)
					AddExternalName(info.FunctionValue, info.FunctionType, externalName, false,
						symbol.Visibility == Visibility.Public);
				
				if (_assemblySymbol.EntryPoint?.Symbol == function.Info.Symbol)
					current.EntryPoint = info;
			}
			
			foreach (var function in file.Functions.Where(function => IsEmitted(function.Info) &&
			                                                          ExportsAdapter(function.Info)))
				_exportNames.Add(new(GetExternalThunk(function.Info).Name, false));
		}
		
		foreach (var file in module.Files)
		{
			var globals = file.Globals.Where(static global =>
				global.Symbol.ContainingType is not NamedTypeSymbol { IsGenericDefinition: true } &&
				global.Symbol is { Trait: null, Impl: null });
			
			foreach (var global in globals.Where(static global => !global.Symbol.IsImport))
				DefineGlobal(global);
		}
		
		foreach (var file in module.Files)
		{
			// Build function bodies
			foreach (var function in file.Functions.Where(static function => IsEmitted(function.Info)))
				BuildFunction(function, function.Info);
		}
		
		if (current.EntryPoint is { } entryPoint && _assemblySymbol.EntryPoint is { } entryInfo)
			BuildEntryPoint(entryPoint, entryInfo.Signature, entryInfo.Symbol.Definition);
	}
	
	public void Dispose()
	{
		_targetData.Dispose();
		_targetMachine.Dispose();
		LLVM.DisposePassBuilderOptions((LLVMOpaquePassBuilderOptions*)_passBuilderOptions.Handle);
		LLVM.ContextDispose((LLVMOpaqueContext*)_context.Handle);
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
	public ImmutableArray<string> ImportedFunctions { get; init; } = [];
	public ImmutableArray<string> ImportedGlobals { get; init; } = [];
	
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
	public IReadOnlySet<Symbol> Exports { get; init; } = ImmutableHashSet<Symbol>.Empty;
	public IReadOnlySet<GlobalSymbol> LibraryStatics { get; init; } = ImmutableHashSet<GlobalSymbol>.Empty;
	public IReadOnlySet<Symbol> StaticLibrarySymbols { get; init; } = ImmutableHashSet<Symbol>.Empty;
	public IReadOnlySet<string> DynamicAssemblies { get; init; } = ImmutableHashSet<string>.Empty;
	public IReadOnlySet<string> HostedRegistries { get; init; } = ImmutableHashSet<string>.Empty;
	public ImmutableArray<string> ContainedAssemblies { get; init; } = [];
	public ImmutableArray<string> LoadedAssemblies { get; init; } = [];
	
	public IReadOnlyDictionary<FunctionSymbol, RuntimeChecks> ForeignChecks { get; init; } =
		ImmutableDictionary<FunctionSymbol, RuntimeChecks>.Empty;
	
	public IReadOnlyDictionary<string, RuntimeChecks> AssemblyChecks { get; init; } =
		ImmutableDictionary<string, RuntimeChecks>.Empty;
	
	public IReadOnlyDictionary<string, FunctionInfo> PanicAdapters { get; init; } =
		ImmutableDictionary<string, FunctionInfo>.Empty;
}

public readonly record struct RuntimeChecks(bool Bounds, bool Overflow);

public sealed record CodeGenConfig
(
	OutputConfig OutputConfig,
	TargetConfig? TargetConfig, // if null, compiles for current platform
	OptimizeMode OptimizeMode
)
{
	public RuntimeChecks Checks { get; init; } = new(true, false);
	public bool ExportsSymbols { get; init; }
	public string? StartFunction { get; init; }
	public bool DebugInfo { get; init; }
	public string? SourceRoot { get; init; }
	public ImmutableArray<string> ProjectRoots { get; init; } = [];
	
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
			OptimizeMode == OptimizeMode.Release
				? LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault
				: LLVMCodeGenOptLevel.LLVMCodeGenLevelNone,
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