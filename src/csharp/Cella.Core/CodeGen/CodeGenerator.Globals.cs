using System.Text;
using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Diagnostics;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private const ulong MaxImageBytes = 1UL << 31;
	
	private void DefineGlobal(GlobalInfo info)
	{
		if (TargetTriple.Contains("windows") && _targetData.ABISizeOfType(MapTypeSymbol(info.Type)) >= MaxImageBytes)
			Diagnostics.Add(new(DiagnosticSeverity.Error, info.Symbol.Syntax.Identifier.SourceLocation,
				$"Module variable '{info.Symbol.Name}' is too large"));
		
		var global = CreateGlobal(info);
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
	
	private bool IsReadOnly(GlobalInfo info) =>
		!info.Symbol.IsMutable && !info.Symbol.IsExternal && !_typePool.NeedsDrop(info.Type);
	
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
		}
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
			substitution = outer;
			var isReachable = IsReachableStatic(info, instance);
			if (!isReachable && _moduleStates.ContainsKey(info.File.Module) &&
			    info.Symbol.Visibility is Visibility.Private or Visibility.Module)
			{
				defined.Linkage = LLVMLinkage.LLVMInternalLinkage;
				return defined;
			}
			
			ShareDefinition(defined, _config.ExportsSymbols && isReachable);
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
		if (FindRegistryOwner(info) is not { } assembly)
			return GetGlobal(info);
		
		var accessor = GetStaticAccessor(info, assembly);
		return builder.BuildCall2(LLVMTypeRef.CreateFunction(OpaquePointer, []), accessor, Array.Empty<LLVMValueRef>(),
			info.Symbol.Name);
	}
	
	private LLVMTypeRef RegistryType => LLVMTypeRef.CreateFunction(OpaquePointer,
		[OpaquePointer, MapTypeSymbol(NativeSymbols.UIntSize), OpaquePointer]);
	
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
		substitution = outer;
		
		var initial = current.Module.AddGlobal(value.TypeOf, $"{info.MangledName}.initial");
		initial.Initializer = value;
		initial.IsGlobalConstant = true;
		initial.Linkage = LLVMLinkage.LLVMPrivateLinkage;
		
		var name = GetOrCreateStringGlobal(Encoding.UTF8.GetBytes(info.MangledName + "\0"));
		var size = LLVMValueRef.CreateConstInt(MapTypeSymbol(NativeSymbols.UIntSize),
			_targetData.ABISizeOfType(value.TypeOf));
		
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
		var found = builder.BuildCall2(RegistryType, GetStaticRegistry(assembly), new[] { name, size, initial },
			"found");
		
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
		var nodeType = _context.GetStructType([OpaquePointer, OpaquePointer, OpaquePointer], false);
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
		
		var allocateType = LLVMTypeRef.CreateFunction(OpaquePointer, [sizeType]);
		var allocate = current.Module.GetNamedFunction("malloc");
		if (allocate.Handle == IntPtr.Zero)
			allocate = current.Module.AddFunction("malloc", allocateType);
		
		var copyType = LLVMTypeRef.CreateFunction(_context.VoidType,
			[OpaquePointer, OpaquePointer, sizeType, _context.Int1Type]);
		
		var copy = GetIntrinsic($"llvm.memcpy.p0.p0.i{sizeType.IntWidth}", copyType);
		var key = registry.GetParam(0);
		var size = registry.GetParam(1);
		var initial = registry.GetParam(2);
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
		var measure = _context.AppendBasicBlock(registry, "measure");
		var create = _context.AppendBasicBlock(registry, "create");
		var release = _context.AppendBasicBlock(registry, "release");
		
		using var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(entry);
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
		builder.BuildCondBr(builder.BuildIsNull(node, "end"), measure, compare);
		
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
		
		builder.PositionAtEnd(measure);
		var length = builder.BuildPhi(sizeType, "length");
		var character = builder.BuildLoad2(byteType,
			builder.BuildGEP2(byteType, key, new[] { length }, "character_slot"),
			"character");
		
		var counted = builder.BuildAdd(length, one, "counted");
		builder.BuildCondBr(builder.BuildIsNull(character, "terminator"), create, measure);
		length.AddIncoming([zero, counted], [visit, measure], 2);
		
		builder.PositionAtEnd(create);
		var keyCopy = builder.BuildCall2(allocateType, allocate, new[] { counted }, "key_copy");
		builder.BuildCall2(copyType, copy, [keyCopy, key, counted, LLVMValueRef.CreateConstInt(_context.Int1Type, 0)]);
		var bytes = builder.BuildSelect(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, size, zero, "empty"), one, size,
			"bytes");
		
		var created = builder.BuildCall2(allocateType, allocate, new[] { bytes }, "created");
		builder.BuildCall2(copyType, copy, [created, initial, size, LLVMValueRef.CreateConstInt(_context.Int1Type, 0)]);
		var entrySize = LLVMValueRef.CreateConstInt(sizeType, _targetData.ABISizeOfType(nodeType));
		var added = builder.BuildCall2(allocateType, allocate, new[] { entrySize }, "added");
		builder.BuildStore(keyCopy, builder.BuildStructGEP2(nodeType, added, 0, "added_key"));
		builder.BuildStore(created, builder.BuildStructGEP2(nodeType, added, 1, "added_storage"));
		builder.BuildStore(builder.BuildLoad2(OpaquePointer, head, "previous"),
			builder.BuildStructGEP2(nodeType, added, 2, "added_next"));
		
		builder.BuildStore(added, head);
		builder.BuildBr(release);
		
		builder.PositionAtEnd(release);
		var result = builder.BuildPhi(OpaquePointer, "result");
		result.AddIncoming([storage, created], [found, create], 2);
		var unlock = builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int32Type, 0), guard);
		unlock.Alignment = 4;
		LLVM.SetOrdering(unlock, LLVMAtomicOrdering.LLVMAtomicOrderingRelease);
		builder.BuildRet(result);
	}
}