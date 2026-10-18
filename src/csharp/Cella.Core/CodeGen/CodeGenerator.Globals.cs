using System.Text;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private const ulong MaxImageBytes = 1UL << 31;
	
	private void DefineGlobal(GlobalInfo info)
	{
		if (_targetData.ABISizeOfType(MapTypeSymbol(info.Type)) >= MaxImageBytes)
			Diagnostics.Add(new(DiagnosticSeverity.Error, info.Symbol.Syntax.Identifier.SourceLocation,
				$"Module variable '{info.Symbol.Name}' is too large"));
		
		var global = CreateGlobal(info);
		if (_typePool.NeedsDrop(info.Type))
			current.DroppedStatics.Add((global, info.Type));
		
		if (_inputs.Exports.Contains(info.Symbol))
			_exportNames.Add(new(global.Name, true));
		
		if (IsExported(info.Symbol))
		{
			global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
			global.Linkage = LLVMLinkage.LLVMDLLExportLinkage;
		}
		else if (info.Symbol.Visibility is Visibility.Private or Visibility.Module &&
		         !_inputs.Exports.Contains(info.Symbol))
		{
			global.Linkage = LLVMLinkage.LLVMInternalLinkage;
		}
		else
		{
			global.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		}
		
		if (info.Symbol.ExternalName is { } externalName)
			AddExternalName(global, MapTypeSymbol(info.Type), externalName, true,
				info.Symbol.Visibility == Visibility.Public);
	}
	
	private LLVMValueRef CreateGlobal(GlobalInfo info)
	{
		var initializer = EmitStaticConstant(info.Value!);
		var global = current.Module.AddGlobal(initializer.TypeOf, info.MangledName);
		global.Initializer = initializer;
		global.IsGlobalConstant = IsReadOnly(info);
		current.Globals[info.Symbol] = global;
		if (_config.DebugInfo)
			DescribeGlobal(info, global);
		
		return global;
	}
	
	private static LLVMValueRef FindNamedFunction(LLVMModuleRef module, string name)
	{
		var function = module.GetNamedFunction(name);
		return function.Handle != IntPtr.Zero ? function : FindNamedAlias(module, name);
	}
	
	private static LLVMValueRef FindNamedAlias(LLVMModuleRef module, string name)
	{
		using var marshaled = new MarshaledString(name);
		return new((IntPtr)LLVM.GetNamedGlobalAlias((LLVMOpaqueModule*)module.Handle, marshaled, (nuint)name.Length));
	}
	
	private LLVMValueRef DeclareGlobal(GlobalInfo info)
	{
		if (info.Symbol.IsImport && FindNamedAlias(current.Module, info.MangledName) is { Handle: not 0 } alias)
			return current.Globals[info.Symbol] = alias;
		
		var global = current.Module.AddGlobal(MapTypeSymbol(info.Type), info.MangledName);
		global.IsGlobalConstant = IsReadOnly(info);
		current.Globals[info.Symbol] = global;
		return global;
	}
	
	private static bool IsReadOnly(GlobalInfo info) => info.Symbol.IsConstant;
	
	private LLVMValueRef GetGlobal(GlobalInfo info)
	{
		info = SubstituteGlobal(info);
		if (current.Globals.TryGetValue(info.Symbol, out var existing))
			return existing;
		
		if (info.Symbol.ContainingType is NamedTypeSymbol { IsGenericInstance: true } instance)
			return GetInstanceGlobal(info, instance);
		
		var global = DeclareGlobal(info);
		if (info.Symbol.IsImport)
		{
			if (global.IsAGlobalAlias.Handle != IntPtr.Zero)
				return global;
			
			global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			global.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
			current.ExternalGlobals.Add(global.Name);
			if (info.Symbol.Syntax.Origin is { } origin)
				current.ExternalLibraries.Add(origin);
		}
		else if (!_assemblySymbol.SignatureTable.Globals.ContainsKey(info.Symbol) && !_config.ExportsSymbols &&
		         _inputs.StaticLibrarySymbols.Contains(info.Symbol))
			global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
		else if (!_assemblySymbol.SignatureTable.Globals.ContainsKey(info.Symbol))
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
			var type = Substitute(info.Type);
			substitution = outer;
			if (_typePool.NeedsDrop(type))
				current.DroppedStatics.Add((defined, type));
			
			var isReachable = IsReachableStatic(info, instance);
			if (!isReachable && _moduleStates.ContainsKey(info.File.Module) &&
			    info.Symbol.Visibility is Visibility.Private or Visibility.Module)
			{
				defined.Linkage = LLVMLinkage.LLVMInternalLinkage;
				return defined;
			}
			
			ShareDefinition(defined, SharesExports && isReachable);
			if (isReachable)
			{
				_exportedStatics.Add(info.Symbol);
				_exportNames.Add(new(defined.Name, true));
			}
			
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
		return LoadValue(builder, info.Type, EmitGlobalAddress(info, builder), info.Symbol.Name);
	}
	
	private LLVMValueRef EmitGlobalAddress(GlobalInfo info, LLVMBuilderRef builder)
	{
		info = SubstituteGlobal(info);
		if (info.Fault is { } fault)
			PanicIf(_true, fault.Message, fault.Location, builder);
		
		if (FindRegistryOwner(info) is not { } assembly)
			return GetGlobal(info);
		
		var accessor = GetStaticAccessor(info, assembly);
		return builder.BuildCall2(LLVMTypeRef.CreateFunction(OpaquePointer, []), accessor, Array.Empty<LLVMValueRef>(),
			info.Symbol.Name);
	}
	
	private LLVMTypeRef RegistryType => LLVMTypeRef.CreateFunction(OpaquePointer, [OpaquePointer]);
	
	private LLVMTypeRef RegistryNodeType =>
		_context.GetStructType([OpaquePointer, OpaquePointer, OpaquePointer, OpaquePointer], false);
	
	private LLVMTypeRef DropsType => LLVMTypeRef.CreateFunction(_context.VoidType, []);
	
	private string? FindRegistryOwner(GlobalInfo info) =>
		info.Symbol.ContainingType is NamedTypeSymbol { IsGenericInstance: true } instance &&
		_modules.FindAssembly(instance.Definition) is { } assembly &&
		(assembly != _assemblySymbol.Name || IsReachableStatic(info, instance))
			? assembly
			: null;
	
	private bool IsReachableStatic(GlobalInfo info, NamedTypeSymbol instance) =>
		instance.Definition.GetStaticField(info.Symbol.Name) is { } definition && _inputs.Exports.Contains(definition);
	
	private LLVMValueRef GetStaticAccessor(GlobalInfo info, string assembly)
	{
		if (current.StaticAccessors.TryGetValue(info.Symbol, out var existing))
			return existing;
		
		var cache = current.Module.AddGlobal(OpaquePointer, $"{info.MangledName}.cache");
		cache.Initializer = LLVMValueRef.CreateConstNull(OpaquePointer);
		ShareDefinition(cache, false);
		
		var accessor = current.Module.AddFunction($"{info.MangledName}.address",
			LLVMTypeRef.CreateFunction(OpaquePointer, []));
		
		ShareDefinition(accessor, false);
		current.StaticAccessors[info.Symbol] = accessor;
		
		var instance = (NamedTypeSymbol)info.Symbol.ContainingType!;
		var outer = substitution;
		substitution = TypePool.CreateMap(instance.Definition.TypeParameters, instance.TypeArguments);
		var value = EmitStaticConstant(info.Value!);
		var type = Substitute(info.Type);
		substitution = outer;
		var glue = _typePool.NeedsDrop(type) ? GetDropGlue(type) : LLVMValueRef.CreateConstNull(OpaquePointer);
		
		var storage = current.Module.AddGlobal(value.TypeOf, $"{info.MangledName}.storage");
		storage.Initializer = value;
		storage.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		var name = GetOrCreateStringGlobal(Encoding.UTF8.GetBytes(info.MangledName + "\0"));
		var node = current.Module.AddGlobal(RegistryNodeType, $"{info.MangledName}.node");
		node.Initializer = LLVMValueRef.CreateConstNamedStruct(RegistryNodeType,
			[name, storage, LLVMValueRef.CreateConstNull(OpaquePointer), glue]);
		
		node.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		using var builder = current.Module.Context.CreateBuilder();
		var entry = _context.AppendBasicBlock(accessor, "entry");
		var lookup = _context.AppendBasicBlock(accessor, "lookup");
		var cached = _context.AppendBasicBlock(accessor, "cached");
		builder.PositionAtEnd(entry);
		var pointer = builder.BuildLoad2(OpaquePointer, cache, "pointer");
		pointer.Alignment = (uint)_targetData.ABISizeOfType(OpaquePointer);
		LLVM.SetOrdering(pointer, LLVMAtomicOrdering.LLVMAtomicOrderingAcquire);
		builder.BuildCondBr(builder.BuildIsNull(pointer, "missing"), lookup, cached);
		
		builder.PositionAtEnd(lookup);
		var found = builder.BuildCall2(RegistryType, GetStaticRegistry(assembly), new[] { node }, "found");
		
		var store = builder.BuildStore(found, cache);
		store.Alignment = (uint)_targetData.ABISizeOfType(OpaquePointer);
		LLVM.SetOrdering(store, LLVMAtomicOrdering.LLVMAtomicOrderingRelease);
		builder.BuildRet(found);
		
		builder.PositionAtEnd(cached);
		builder.BuildRet(pointer);
		return accessor;
	}
	
	private LLVMValueRef GetStaticRegistry(string assembly)
	{
		var name = Mangling.MangleStaticRegistry(assembly);
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var registry = current.Module.AddFunction(name, RegistryType);
		if (_inputs.DynamicAssemblies.Contains(assembly))
		{
			registry.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			registry.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		
		return registry;
	}
	
	private void EmitStaticRegistry()
	{
		var sizeType = MapTypeSymbol(NativeSymbols.UIntSize);
		var byteType = _context.Int8Type;
		var nodeType = RegistryNodeType;
		var registry = GetStaticRegistry(_assemblySymbol.Name);
		registry.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		foreach (var hosted in _inputs.HostedRegistries)
		{
			var name = Mangling.MangleStaticRegistry(hosted);
			using var empty = new MarshaledString(string.Empty);
			var alias = new LLVMValueRef((IntPtr)LLVM.AddAlias2((LLVMOpaqueModule*)current.Module.Handle,
				(LLVMOpaqueType*)RegistryType.Handle, 0, (LLVMOpaqueValue*)registry.Handle, empty));
			
			var declared = current.Module.GetNamedFunction(name);
			if (declared.Handle != IntPtr.Zero)
			{
				declared.ReplaceAllUsesWith(alias);
				declared.DeleteFunction();
			}
			
			alias.Name = name;
			alias.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		}
		
		var head = current.Module.AddGlobal(OpaquePointer, ".statics.head");
		head.Initializer = LLVMValueRef.CreateConstNull(OpaquePointer);
		head.Linkage = LLVMLinkage.LLVMInternalLinkage;
		var guard = current.Module.AddGlobal(_context.Int32Type, ".statics.lock");
		guard.Initializer = LLVMValueRef.CreateConstInt(_context.Int32Type, 0);
		guard.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		var added = registry.GetParam(0);
		var zero = LLVMValueRef.CreateConstInt(sizeType, 0);
		var one = LLVMValueRef.CreateConstInt(sizeType, 1);
		
		var entry = _context.AppendBasicBlock(registry, "entry");
		var acquire = _context.AppendBasicBlock(registry, "acquire");
		var search = _context.AppendBasicBlock(registry, "search");
		var visit = _context.AppendBasicBlock(registry, "visit");
		var compare = _context.AppendBasicBlock(registry, "compare");
		var scan = _context.AppendBasicBlock(registry, "scan");
		var compareNext = _context.AppendBasicBlock(registry, "compare_next");
		var matched = _context.AppendBasicBlock(registry, "matched");
		var advance = _context.AppendBasicBlock(registry, "advance");
		var found = _context.AppendBasicBlock(registry, "found");
		var link = _context.AppendBasicBlock(registry, "link");
		var release = _context.AppendBasicBlock(registry, "release");
		
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(entry);
		var key = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, added, 0, "added_key"), "key");
		builder.BuildBr(acquire);
		
		builder.PositionAtEnd(acquire);
		LLVMValueRef exchange = LLVM.BuildAtomicCmpXchg(builder, guard,
			LLVMValueRef.CreateConstInt(_context.Int32Type, 0),
			LLVMValueRef.CreateConstInt(_context.Int32Type, 1), LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
			LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic, 0);
		
		builder.BuildCondBr(builder.BuildExtractValue(exchange, 1, "locked"), search, acquire);
		
		builder.PositionAtEnd(search);
		var first = builder.BuildLoad2(OpaquePointer, head, "first");
		builder.BuildBr(visit);
		
		builder.PositionAtEnd(visit);
		var node = builder.BuildPhi(OpaquePointer, "node");
		builder.BuildCondBr(builder.BuildIsNull(node, "end"), link, compare);
		
		builder.PositionAtEnd(compare);
		var nodeKey = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, node, 0, "key_slot"),
			"node_key");
		
		builder.BuildBr(scan);
		
		builder.PositionAtEnd(scan);
		var index = builder.BuildPhi(sizeType, "index");
		var left = builder.BuildLoad2(byteType, builder.BuildGEP2(byteType, nodeKey, new[] { index }, "left_slot"),
			"left");
		
		var right = builder.BuildLoad2(byteType, builder.BuildGEP2(byteType, key, new[] { index }, "right_slot"),
			"right");
		
		builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "same"), matched, advance);
		
		builder.PositionAtEnd(matched);
		builder.BuildCondBr(builder.BuildIsNull(left, "terminated"), found, compareNext);
		
		builder.PositionAtEnd(compareNext);
		var nextIndex = builder.BuildAdd(index, one, "next_index");
		builder.BuildBr(scan);
		index.AddIncoming([zero, nextIndex], [compare, compareNext], 2);
		
		builder.PositionAtEnd(advance);
		var next = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, node, 2, "next_slot"), "next");
		builder.BuildBr(visit);
		node.AddIncoming([first, next], [search, advance], 2);
		
		builder.PositionAtEnd(found);
		var storage = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, node, 1, "storage_slot"),
			"storage");
		
		builder.BuildBr(release);
		
		builder.PositionAtEnd(link);
		builder.BuildStore(first, builder.BuildStructGEP2(nodeType, added, 2, "added_next"));
		builder.BuildStore(added, head);
		var linked = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, added, 1, "added_storage"),
			"linked");
		
		builder.BuildBr(release);
		
		builder.PositionAtEnd(release);
		var result = builder.BuildPhi(OpaquePointer, "result");
		result.AddIncoming([storage, linked], [found, link], 2);
		var unlock = builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int32Type, 0), guard);
		unlock.Alignment = 4;
		LLVM.SetOrdering(unlock, LLVMAtomicOrdering.LLVMAtomicOrderingRelease);
		builder.BuildRet(result);
	}
	
	private LLVMValueRef GetStaticDrops(string assembly)
	{
		var name = Mangling.MangleStaticDrops(assembly);
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var drops = current.Module.AddFunction(name, DropsType);
		if (_inputs.LoadedAssemblies.Contains(assembly))
		{
			drops.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
			drops.Linkage = LLVMLinkage.LLVMDLLImportLinkage;
		}
		
		return drops;
	}
	
	private void BuildStaticDropGlue(List<ModuleState> states)
	{
		var prepared = states.ToDictionary(static state => state, static _ => 0);
		while (states.Any(state => prepared[state] < state.DroppedStatics.Count))
		{
			foreach (var state in states)
			{
				current = state;
				for (var i = prepared[state]; i < state.DroppedStatics.Count; i++)
					GetDropGlue(state.DroppedStatics[i].Type);
				
				prepared[state] = state.DroppedStatics.Count;
			}
			
			BuildInstantiations();
		}
	}
	
	private void EmitStaticDrops(List<ModuleState> states, bool hasRegistry)
	{
		var dropped = states.Where(static state => state.DroppedStatics.Count > 0).ToList();
		foreach (var state in dropped)
		{
			current = state;
			EmitModuleDrops(state);
		}
		
		current = states[0];
		var drops = GetStaticDrops(_assemblySymbol.Name);
		if (_config.ExportsSymbols)
			drops.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
		else
			drops.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		
		using var builder = CreateGeneratedBuilder(drops, drops.Name, SourceLocation.None);
		if (hasRegistry)
			EmitRegistryDrops(drops, builder);
		
		foreach (var state in OrderForDrops(dropped))
			builder.BuildCall2(DropsType, GetModuleDrops(state), Array.Empty<LLVMValueRef>());
		
		foreach (var assembly in _inputs.ContainedAssemblies)
			builder.BuildCall2(DropsType, GetStaticDrops(assembly), Array.Empty<LLVMValueRef>());
		
		builder.BuildRetVoid();
	}
	
	private void RemoveStaticDropCalls(List<ModuleState> states)
	{
		var name = Mangling.MangleStaticDrops(_assemblySymbol.Name);
		foreach (var state in states)
		{
			var drops = state.Module.GetNamedFunction(name);
			if (drops.Handle == IntPtr.Zero)
				continue;
			
			while (LLVM.GetFirstUse(drops) is var use && use != null)
				LLVM.InstructionEraseFromParent(LLVM.GetUser(use));
			
			drops.DeleteFunction();
		}
	}
	
	private LLVMValueRef GetModuleDrops(ModuleState state)
	{
		var name = Mangling.MangleModuleDrops(_assemblySymbol.Name, state.Lowered.Symbol.Name);
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var drops = current.Module.AddFunction(name, DropsType);
		drops.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		return drops;
	}
	
	private void EmitModuleDrops(ModuleState state)
	{
		var drops = GetModuleDrops(state);
		using var builder = CreateGeneratedBuilder(drops, drops.Name, SourceLocation.None);
		for (var i = state.DroppedStatics.Count - 1; i >= 0; i--)
		{
			var (global, type) = state.DroppedStatics[i];
			EmitDropCall(type, global, builder);
		}
		
		builder.BuildRetVoid();
	}
	
	private List<ModuleState> OrderForDrops(List<ModuleState> states)
	{
		var ordered = new List<ModuleState>();
		var visited = new HashSet<ModuleState>();
		var included = states.ToHashSet();
		foreach (var state in states)
			Visit(state);
		
		ordered.Reverse();
		return ordered;
		
		void Visit(ModuleState state)
		{
			if (!visited.Add(state))
				return;
			
			foreach (var dependency in FindImportedModules(state))
				Visit(dependency);
			
			if (included.Contains(state))
				ordered.Add(state);
		}
	}
	
	private IEnumerable<ModuleState> FindImportedModules(ModuleState state) => state.Lowered.Files
		.SelectMany(file =>
			_assemblySymbol.SignatureTable.ImportEnvironments.GetValueOrDefault(file.Symbol)?.ImportedSymbols ?? [])
		.Select(_modules.FindModule)
		.OfType<ModuleSymbol>()
		.Select(_moduleStates.GetValueOrDefault)
		.OfType<ModuleState>()
		.Where(dependency => dependency != state)
		.Distinct();
	
	private void EmitRegistryDrops(LLVMValueRef drops, LLVMBuilderRef builder)
	{
		var nodeType = RegistryNodeType;
		var head = current.Module.GetNamedGlobal(".statics.head");
		var entry = builder.InsertBlock;
		var visit = _context.AppendBasicBlock(drops, "visit");
		var check = _context.AppendBasicBlock(drops, "check");
		var drop = _context.AppendBasicBlock(drops, "drop");
		var advance = _context.AppendBasicBlock(drops, "advance");
		var done = _context.AppendBasicBlock(drops, "done");
		var first = builder.BuildLoad2(OpaquePointer, head, "first");
		builder.BuildBr(visit);
		
		builder.PositionAtEnd(visit);
		var node = builder.BuildPhi(OpaquePointer, "node");
		builder.BuildCondBr(builder.BuildIsNull(node, "end"), done, check);
		
		builder.PositionAtEnd(check);
		var glue = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, node, 3, "drop_slot"), "glue");
		builder.BuildCondBr(builder.BuildIsNull(glue, "kept"), advance, drop);
		
		builder.PositionAtEnd(drop);
		var storage = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, node, 1, "storage_slot"),
			"storage");
		
		builder.BuildCall2(DropGlueType, glue, [storage]);
		builder.BuildBr(advance);
		
		builder.PositionAtEnd(advance);
		var next = builder.BuildLoad2(OpaquePointer, builder.BuildStructGEP2(nodeType, node, 2, "next_slot"), "next");
		builder.BuildBr(visit);
		node.AddIncoming([first, next], [entry, advance], 2);
		builder.PositionAtEnd(done);
	}
}