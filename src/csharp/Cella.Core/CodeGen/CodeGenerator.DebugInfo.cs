using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private const uint DwarfBoolean = 0x02;
	private const uint DwarfFloat = 0x04;
	private const uint DwarfSigned = 0x05;
	private const uint DwarfUnsigned = 0x07;
	private const uint DwarfUtf = 0x10;
	private const uint DwarfStructure = 0x13;
	private const uint DwarfUnion = 0x17;
	private const uint DwarfReference = 0x10;
	
	private readonly Dictionary<int, LLVMMetadataRef> _lexicalBlocks = [];
	private readonly List<VariableDeclaration> _declarations = [];
	private Dictionary<BasicBlock, ImmutableStack<int>> blockScopes = [];
	private ImmutableStack<int> openScopes = ImmutableStack<int>.Empty;
	private LLVMMetadataRef currentSubprogram;
	private LLVMMetadataRef currentScope;
	
	private readonly record struct VariableDeclaration
	(
		LLVMValueRef Storage,
		LLVMMetadataRef Variable,
		LLVMMetadataRef Location
	);
	
	private LLVMOpaqueDIBuilder* DiBuilder => (LLVMOpaqueDIBuilder*)current.DiBuilder.Handle;
	
	private bool UsesCodeView => TargetTriple.Contains("windows");
	
	private void StartDebugInfo(LoweredModule module)
	{
		AddModuleFlag("Debug Info Version", LLVM.DebugMetadataVersion());
		if (UsesCodeView)
			AddModuleFlag("CodeView", 1);
		else
			AddModuleFlag("Dwarf Version", 5);
		
		var path = module.Files.Select(static file => file.Symbol.FullPath).Order(StringComparer.Ordinal)
			.FirstOrDefault() ?? module.Symbol.Name;
		
		current.DebugFile = GetDebugFile(path);
		current.DiBuilder.CreateCompileUnit(LLVMDWARFSourceLanguage.LLVMDWARFSourceLanguageC, current.DebugFile,
			"Cella", IsOptimized, "", 0, "", LLVMDWARFEmissionKind.LLVMDWARFEmissionFull, 0, 0, 0, "", "");
	}
	
	private int IsOptimized => _config.OptimizeMode == OptimizeMode.Release ? 1 : 0;
	
	private void AddModuleFlag(string name, uint value)
	{
		using var key = new MarshaledString(name);
		var constant = LLVMValueRef.CreateConstInt(_context.Int32Type, value);
		LLVM.AddModuleFlag((LLVMOpaqueModule*)current.Module.Handle,
			LLVMModuleFlagBehavior.LLVMModuleFlagBehaviorWarning, key, (nuint)key.Length,
			LLVM.ValueAsMetadata((LLVMOpaqueValue*)constant.Handle));
	}
	
	private LLVMMetadataRef GetDebugFile(string path)
	{
		if (current.DebugFiles.TryGetValue(path, out var file))
			return file;
		
		file = current.DiBuilder.CreateFile(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "");
		current.DebugFiles[path] = file;
		return file;
	}
	
	private static bool HasSource(SourceLocation location) => !string.IsNullOrEmpty(location.Source.FilePath);
	
	private static uint GetLine(SourceLocation location) =>
		HasSource(location) ? (uint)location.GetLineColumn().Line : 0u;
	
	private LLVMMetadataRef GetDebugFile(SourceLocation location) =>
		HasSource(location) ? GetDebugFile(location.Source.FilePath) : current.DebugFile;
	
	private LLVMMetadataRef DescribeFunction(LLVMValueRef function, IReadOnlyList<string> name,
		SourceLocation location, LLVMDIFlags flags, LLVMMetadataRef[] signature)
	{
		var file = GetDebugFile(location);
		var line = GetLine(location);
		var type = current.DiBuilder.CreateSubroutineType(file, signature, LLVMDIFlags.LLVMDIFlagZero);
		var isLocal = function.Linkage is LLVMLinkage.LLVMInternalLinkage or LLVMLinkage.LLVMPrivateLinkage ? 1 : 0;
		var scope = GetNamespace(name.Take(name.Count - 1));
		var subprogram = current.DiBuilder.CreateFunction(scope.Handle != nint.Zero ? scope : file, name[^1], "",
			file, line, type, isLocal, 1, line, flags, IsOptimized);
		
		LLVM.SetSubprogram((LLVMOpaqueValue*)function.Handle, (LLVMOpaqueMetadata*)subprogram.Handle);
		return subprogram;
	}
	
	private LLVMMetadataRef GetNamespace(IEnumerable<string> parts)
	{
		LLVMMetadataRef scope = default;
		var path = "";
		foreach (var part in parts)
		{
			path += $"{part}\n";
			if (!current.DebugNamespaces.TryGetValue(path, out var next))
			{
				using var name = new MarshaledString(part);
				current.DebugNamespaces[path] = next = LLVM.DIBuilderCreateNameSpace(DiBuilder,
					(LLVMOpaqueMetadata*)scope.Handle, name, (nuint)name.Length, 0);
			}
			
			scope = next;
		}
		
		return scope;
	}
	
	private LLVMBuilderRef CreateGeneratedBuilder(LLVMValueRef function, string name, SourceLocation location) =>
		CreateGeneratedBuilder(function, [name], location);
	
	private LLVMBuilderRef CreateGeneratedBuilder(LLVMValueRef function, IReadOnlyList<string> name,
		SourceLocation location)
	{
		var builder = current.Module.Context.CreateBuilder();
		builder.PositionAtEnd(_context.AppendBasicBlock(function, "entry"));
		if (!_config.DebugInfo)
			return builder;
		
		var subprogram = DescribeFunction(function, name, location, LLVMDIFlags.LLVMDIFlagArtificial, []);
		SetDebugLocation(builder, GetLine(location), 0u, subprogram);
		return builder;
	}
	
	private void StartFunctionDebugInfo(LoweredFunction function, FunctionInfo info, LLVMValueRef value)
	{
		var signature = info.Signature;
		LLVMMetadataRef[] types =
		[
			GetDebugType(signature.ReturnType),
			..signature.ParameterTypes.Select((_, i) => GetDebugType(signature.GetDeclaredType(i)))
		];
		
		var flags = signature.ReturnType is NeverType ? LLVMDIFlags.LLVMDIFlagNoReturn : LLVMDIFlags.LLVMDIFlagZero;
		currentSubprogram = DescribeFunction(value, GetDisplayName(info), info.Symbol.Definition, flags, types);
		currentScope = currentSubprogram;
		_lexicalBlocks.Clear();
		_declarations.Clear();
		blockScopes = FindBlockScopes(function);
	}
	
	private void FinishFunctionDebugInfo(LLVMValueRef branch)
	{
		foreach (var (storage, variable, location) in _declarations)
			LLVM.DIBuilderInsertDeclareRecordBefore(DiBuilder, (LLVMOpaqueValue*)storage.Handle,
				(LLVMOpaqueMetadata*)variable.Handle, CreateEmptyExpression(), (LLVMOpaqueMetadata*)location.Handle,
				(LLVMOpaqueValue*)branch.Handle);
		
		_declarations.Clear();
		currentScope = default;
		currentSubprogram = default;
	}
	
	private LLVMOpaqueMetadata* CreateEmptyExpression() => LLVM.DIBuilderCreateExpression(DiBuilder, null, 0);
	
	private static Dictionary<BasicBlock, ImmutableStack<int>> FindBlockScopes(LoweredFunction function)
	{
		var scopes = new Dictionary<BasicBlock, ImmutableStack<int>> { [function.Blocks[0]] = [] };
		var pending = new Queue<BasicBlock>([function.Blocks[0]]);
		while (pending.TryDequeue(out var block))
		{
			var open = block.Instructions.Aggregate(scopes[block], static (open, instruction) => instruction switch
			{
				BeginScopeInstruction begin => open.Push(begin.ScopeId),
				EndScopeInstruction end when !open.IsEmpty && open.Peek() == end.ScopeId => open.Pop(),
				_ => open
			});
			
			BasicBlock[] targets = block.Terminator switch
			{
				BranchTerminator branch => [branch.Target],
				ConditionalBranchTerminator branch => [branch.TrueTarget, branch.FalseTarget],
				SwitchTerminator branch => branch.Targets,
				_ => []
			};
			
			foreach (var target in targets)
			{
				if (scopes.TryAdd(target, open))
					pending.Enqueue(target);
			}
		}
		
		return scopes;
	}
	
	private void EnterBlock(BasicBlock block)
	{
		if (!_config.DebugInfo)
			return;
		
		openScopes = blockScopes.GetValueOrDefault(block, []);
		currentScope = GetOpenScope();
	}
	
	private LLVMMetadataRef GetOpenScope() =>
		!openScopes.IsEmpty && _lexicalBlocks.TryGetValue(openScopes.Peek(), out var block) ? block : currentSubprogram;
	
	private void BeginScope(BeginScopeInstruction instruction)
	{
		if (!_config.DebugInfo)
			return;
		
		if (!_lexicalBlocks.ContainsKey(instruction.ScopeId))
		{
			var location = instruction.SourceLocation;
			var (line, column) = HasSource(location) ? location.GetLineColumn() : (0, 0);
			_lexicalBlocks[instruction.ScopeId] = LLVM.DIBuilderCreateLexicalBlock(DiBuilder,
				(LLVMOpaqueMetadata*)currentScope.Handle, (LLVMOpaqueMetadata*)GetDebugFile(location).Handle,
				(uint)line, UsesCodeView ? 0u : (uint)column);
		}
		
		openScopes = openScopes.Push(instruction.ScopeId);
		currentScope = GetOpenScope();
	}
	
	private void EndScope(EndScopeInstruction instruction)
	{
		if (!_config.DebugInfo || openScopes.IsEmpty || openScopes.Peek() != instruction.ScopeId)
			return;
		
		openScopes = openScopes.Pop();
		currentScope = GetOpenScope();
	}
	
	private static bool IsSourceName(string name) => name is [var first, ..] && (char.IsLetter(first) || first == '_');
	
	private void DeclareParameter(ParameterSymbol parameter, int index, FunctionSignature signature,
		LLVMValueRef storage, LLVMBuilderRef builder)
	{
		if (!_config.DebugInfo || !IsSourceName(parameter.Name))
			return;
		
		var declared = signature.GetDeclaredType(index);
		var type = GetDebugType(declared);
		if (signature.GetMode(index) == ParameterMode.Mut)
			type = CreateReferenceType(type);
		
		if (storage.IsAAllocaInst.Handle == nint.Zero)
		{
			var slot = BuildEntryAlloca(builder, storage.TypeOf, parameter.Name);
			builder.BuildStore(storage, slot);
			storage = slot;
			type = Substitute(declared) is DynType
				? GetDebugType(_typePool.GetPointerType(declared))
				: CreateReferenceType(type);
		}
		
		var line = GetLine(parameter.Definition);
		using var name = new MarshaledString(parameter.Name);
		var variable = LLVM.DIBuilderCreateParameterVariable(DiBuilder, (LLVMOpaqueMetadata*)currentSubprogram.Handle,
			name, (nuint)name.Length, (uint)index + 1, (LLVMOpaqueMetadata*)GetDebugFile(parameter.Definition).Handle,
			line, (LLVMOpaqueMetadata*)type.Handle, 1, LLVMDIFlags.LLVMDIFlagZero);
		
		_declarations.Add(new(storage, variable, CreateDebugLocation(line, currentSubprogram)));
	}
	
	private void DeclareLocal(LocalVarInstruction instruction, LLVMValueRef storage)
	{
		var local = instruction.Symbol;
		if (!_config.DebugInfo || !IsSourceName(local.Name))
			return;
		
		var type = local is { Captured: not null, Type: PointerType pointer }
			? CreateReferenceType(GetDebugType(pointer.BaseType))
			: GetDebugType(local.Type);
		
		var location = local.Identifier.SourceLocation;
		var line = GetLine(location);
		var scope = _lexicalBlocks.GetValueOrDefault(instruction.ScopeId, currentSubprogram);
		using var name = new MarshaledString(local.Name);
		var variable = LLVM.DIBuilderCreateAutoVariable(DiBuilder, (LLVMOpaqueMetadata*)scope.Handle, name,
			(nuint)name.Length, (LLVMOpaqueMetadata*)GetDebugFile(location).Handle, line,
			(LLVMOpaqueMetadata*)type.Handle, 1, LLVMDIFlags.LLVMDIFlagZero, 0);
		
		_declarations.Add(new(storage, variable, CreateDebugLocation(line, scope)));
	}
	
	private void DescribeGlobal(GlobalInfo info, LLVMValueRef global)
	{
		var location = info.Symbol.Syntax.SourceLocation;
		var file = (LLVMOpaqueMetadata*)GetDebugFile(location).Handle;
		IEnumerable<string> scopes =
		[
			..info.File.Module.ModuleName.Parts.Select(static part => part.Text),
			..info.Symbol.ContainingType is { } owner ? [owner.Name] : Array.Empty<string>()
		];
		
		var scope = GetNamespace(scopes);
		using var name = new MarshaledString(info.Symbol.Name);
		var isLocal = global.Linkage is LLVMLinkage.LLVMInternalLinkage or LLVMLinkage.LLVMPrivateLinkage ? 1 : 0;
		var expression = LLVM.DIBuilderCreateGlobalVariableExpression(DiBuilder,
			scope.Handle != nint.Zero ? (LLVMOpaqueMetadata*)scope.Handle : file, name, (nuint)name.Length,
			null, 0, file, GetLine(location),
			(LLVMOpaqueMetadata*)GetDebugType(info.Type).Handle, isLocal, CreateEmptyExpression(), null, 0);
		
		using var kind = new MarshaledString("dbg");
		LLVM.GlobalSetMetadata((LLVMOpaqueValue*)global.Handle,
			LLVM.GetMDKindIDInContext((LLVMOpaqueContext*)current.Module.Context.Handle, kind, (uint)kind.Length),
			expression);
	}
	
	private LLVMMetadataRef GetScope(LLVMBuilderRef builder)
	{
		var function = builder.InsertBlock.Parent;
		return currentScope.Handle != nint.Zero && function.Handle == currentFunction.FunctionValue.Handle
			? currentScope
			: LLVM.GetSubprogram((LLVMOpaqueValue*)function.Handle);
	}
	
	private void SetLocation(LLVMBuilderRef builder, SourceLocation location)
	{
		if (!_config.DebugInfo || !HasSource(location))
			return;
		
		var scope = GetScope(builder);
		if (scope.Handle == nint.Zero)
			return;
		
		var file = GetDebugFile(location.Source.FilePath);
		if (LLVM.DIScopeGetFile((LLVMOpaqueMetadata*)scope.Handle) != (LLVMOpaqueMetadata*)file.Handle)
			scope = LLVM.DIBuilderCreateLexicalBlockFile(DiBuilder, (LLVMOpaqueMetadata*)scope.Handle,
				(LLVMOpaqueMetadata*)file.Handle, 0);
		
		var (line, column) = location.GetLineColumn();
		SetDebugLocation(builder, (uint)line, UsesCodeView ? 0u : (uint)column, scope);
	}
	
	private LLVMMetadataRef CreateDebugLocation(uint line, LLVMMetadataRef scope) =>
		LLVM.DIBuilderCreateDebugLocation((LLVMOpaqueContext*)current.Module.Context.Handle, line, 0,
			(LLVMOpaqueMetadata*)scope.Handle, null);
	
	private void SetDebugLocation(LLVMBuilderRef builder, uint line, uint column, LLVMMetadataRef scope) =>
		LLVM.SetCurrentDebugLocation2((LLVMOpaqueBuilder*)builder.Handle,
			LLVM.DIBuilderCreateDebugLocation((LLVMOpaqueContext*)current.Module.Context.Handle, line, column,
				(LLVMOpaqueMetadata*)scope.Handle, null));
	
	private LLVMValueRef EmitAt(SourceLocation location, LLVMBuilderRef builder, Func<LLVMValueRef> emit)
	{
		if (!_config.DebugInfo)
			return emit();
		
		var previous = LLVM.GetCurrentDebugLocation2((LLVMOpaqueBuilder*)builder.Handle);
		SetLocation(builder, location);
		var value = emit();
		LLVM.SetCurrentDebugLocation2((LLVMOpaqueBuilder*)builder.Handle, previous);
		return value;
	}
	
	private string[] GetDisplayName(FunctionInfo function)
	{
		if (function.Symbol.QualifiedName.IsDefaultOrEmpty)
			return [function.MangledName ?? function.Symbol.Name];
		
		var map = function.TypeArguments.IsDefaultOrEmpty
			? []
			: TypePool.CreateMap(function.Symbol.TypeParameters, function.TypeArguments);
		
		return [..function.Symbol.QualifiedName.Select(Describe)];
		
		string Describe(NamePart part) =>
			!part.Combined.IsEmpty ? string.Join("+", part.Combined.Select(Describe))
			: part.Arguments.IsEmpty ? part.Name
			: $"{part.Name}[{string.Join(", ", part.Arguments.Select(argument =>
				Substitute(_typePool.Substitute(argument, map)).Name))}]";
	}
	
	private string[] GetAdapterName(FunctionInfo function) =>
		GetDisplayName(function) is [.. var scopes, var name] ? [..scopes, $"{name} (adapter)"] : [];
	
	private string DescribeDropGlue(TypeSymbol type) => $"{GetDebugTypeName(type)} (drop)";
	
	private LLVMMetadataRef GetDebugType(TypeSymbol type)
	{
		type = Substitute(type);
		if (current.DebugTypes.TryGetValue(type, out var existing))
			return existing;
		
		if (type is not (RecordSymbol or EnumSymbol { IsMatch: false }))
			return current.DebugTypes[type] = CreateDebugType(type, default);
		
		var placeholder = CreatePlaceholder(type);
		current.DebugTypes[type] = placeholder;
		var created = CreateDebugType(type, placeholder);
		LLVM.MetadataReplaceAllUsesWith((LLVMOpaqueMetadata*)placeholder.Handle, (LLVMOpaqueMetadata*)created.Handle);
		return current.DebugTypes[type] = created;
	}
	
	private LLVMMetadataRef CreateDebugType(TypeSymbol type, LLVMMetadataRef scope) => type switch
	{
		_ when type == NativeSymbols.Void || type == NativeSymbols.Never => default,
		_ when type == NativeSymbols.Bool => CreateBasicType(type, DwarfBoolean),
		_ when type == NativeSymbols.Char => CreateBasicType(type, DwarfUtf),
		_ when type == NativeSymbols.Str => CreateStructType(type, scope,
			[("length", NativeSymbols.UIntSize), ("data", _typePool.GetPointerType(NativeSymbols.UInt8))]),
		_ when type == NativeSymbols.CStr => CreatePointerType(GetDebugType(NativeSymbols.UInt8)),
		_ when type == NativeSymbols.VoidPtr => CreatePointerType(default),
		IntegerType integer => CreateBasicType(type, integer.IsSigned ? DwarfSigned : DwarfUnsigned),
		FloatType => CreateBasicType(type, DwarfFloat),
		_ when TypePool.IsFatPointer(type) => CreateStructType(type, scope,
			[("data", NativeSymbols.VoidPtr), ("table", NativeSymbols.VoidPtr)]),
		PointerType pointer => CreatePointerType(GetDebugType(pointer.BaseType)),
		BorrowType borrow => CreateReferenceType(GetDebugType(borrow.Target)),
		ArrayType array => CreateArrayType(array),
		FunctionType { IsExternal: true } function => CreatePointerType(CreateSignatureType(function)),
		FunctionType function => CreateStructType(type, scope,
			[("code", CreatePointerType(CreateSignatureType(function))), ("env", GetDebugType(NativeSymbols.VoidPtr))]),
		ClosureType closure => CreateStructType(type, scope,
		[
			..closure.CaptureTypes.Select((capture, i) => (closure.Function.Symbol.Captures[i].Name, capture))
		]),
		FStrType => CreateStructType(type, scope,
			[("holes", NativeSymbols.UIntSize), ("texts", NativeSymbols.VoidPtr), ("values", NativeSymbols.VoidPtr)]),
		EnumSymbol { IsMatch: true } enumType => CreateMatchEnumType(enumType),
		EnumSymbol enumType => CreateEnumType(enumType, scope),
		RecordSymbol record => CreateRecordType(record, scope),
		_ => CreateUnspecifiedType(type)
	};
	
	private string GetDebugTypeName(TypeSymbol type) =>
		type is NamedTypeSymbol named && _modules.FindModule(named.Definition) is { } module
			? $"{module.ModuleName.Text}.{type.Name}"
			: type.Name;
	
	private (LLVMMetadataRef File, uint Line) GetDeclarationSite(TypeSymbol type) => type switch
	{
		RecordSymbol { Definition: RecordSymbol record } => (GetDebugFile(record.Node.Identifier.SourceLocation),
			GetLine(record.Node.Identifier.SourceLocation)),
		EnumSymbol { Definition: EnumSymbol enumType } => (GetDebugFile(enumType.Node.Identifier.SourceLocation),
			GetLine(enumType.Node.Identifier.SourceLocation)),
		_ => (current.DebugFile, 0u)
	};
	
	private ulong GetSizeInBits(LLVMTypeRef type) => _targetData.ABISizeOfType(type) * 8;
	private uint GetAlignInBits(LLVMTypeRef type) => _targetData.ABIAlignmentOfType(type) * 8;
	
	private LLVMMetadataRef CreateBasicType(TypeSymbol type, uint encoding)
	{
		using var name = new MarshaledString(type.Name);
		return LLVM.DIBuilderCreateBasicType(DiBuilder, name, (nuint)name.Length,
			GetSizeInBits(MapTypeSymbol(type)), encoding, LLVMDIFlags.LLVMDIFlagZero);
	}
	
	private LLVMMetadataRef CreateUnspecifiedType(TypeSymbol type)
	{
		using var name = new MarshaledString(type.Name);
		return LLVM.DIBuilderCreateUnspecifiedType(DiBuilder, name, (nuint)name.Length);
	}
	
	private LLVMMetadataRef CreatePointerType(LLVMMetadataRef pointee) =>
		LLVM.DIBuilderCreatePointerType(DiBuilder, (LLVMOpaqueMetadata*)pointee.Handle, _pointerSize * 8,
			_pointerSize * 8, 0, null, 0);
	
	private LLVMMetadataRef CreateReferenceType(LLVMMetadataRef target) =>
		LLVM.DIBuilderCreateReferenceType(DiBuilder, DwarfReference, (LLVMOpaqueMetadata*)target.Handle);
	
	private LLVMMetadataRef CreateSignatureType(FunctionType function) => current.DiBuilder.CreateSubroutineType(
		current.DebugFile,
		[
			GetDebugType(function.ReturnType),
			..function.ParameterTypes.Select((_, i) => GetDebugType(function.GetDeclaredType(i)))
		],
		LLVMDIFlags.LLVMDIFlagZero);
	
	private LLVMMetadataRef CreateArrayType(ArrayType array)
	{
		var mapped = MapTypeSymbol(array);
		var subrange = LLVM.DIBuilderGetOrCreateSubrange(DiBuilder, 0,
			array.Length > long.MaxValue ? long.MaxValue : (long)array.Length);
		
		return LLVM.DIBuilderCreateArrayType(DiBuilder, GetSizeInBits(mapped), GetAlignInBits(mapped),
			(LLVMOpaqueMetadata*)GetDebugType(array.ElementType).Handle, &subrange, 1);
	}
	
	private LLVMMetadataRef CreateMember(LLVMMetadataRef scope, string name, LLVMMetadataRef type,
		LLVMTypeRef mapped, ulong offset)
	{
		using var memberName = new MarshaledString(name);
		return LLVM.DIBuilderCreateMemberType(DiBuilder, (LLVMOpaqueMetadata*)scope.Handle, memberName,
			(nuint)memberName.Length, (LLVMOpaqueMetadata*)current.DebugFile.Handle, 0, GetSizeInBits(mapped),
			GetAlignInBits(mapped), offset * 8, LLVMDIFlags.LLVMDIFlagZero, (LLVMOpaqueMetadata*)type.Handle);
	}
	
	private LLVMMetadataRef CreateMember(LLVMMetadataRef scope, string name, TypeSymbol type, ulong offset) =>
		CreateMember(scope, name, GetDebugType(type), MapTypeSymbol(type), offset);
	
	private LLVMMetadataRef CreateStructType(TypeSymbol type, LLVMMetadataRef scope,
		IReadOnlyList<(string Name, TypeSymbol Type)> fields)
	{
		var mapped = MapTypeSymbol(type);
		return CreateCompositeType(type, DwarfStructure, mapped,
		[
			..fields.Select((field, i) => CreateMember(scope, field.Name, field.Type,
				_targetData.OffsetOfElement(mapped, (uint)i)))
		]);
	}
	
	private LLVMMetadataRef CreateStructType(TypeSymbol type, LLVMMetadataRef scope,
		IReadOnlyList<(string Name, LLVMMetadataRef Type)> fields)
	{
		var mapped = MapTypeSymbol(type);
		return CreateCompositeType(type, DwarfStructure, mapped,
		[
			..fields.Select((field, i) => CreateMember(scope, field.Name, field.Type,
				mapped.StructGetTypeAtIndex((uint)i),
				_targetData.OffsetOfElement(mapped, (uint)i)))
		]);
	}
	
	private LLVMMetadataRef CreateCompositeType(TypeSymbol type, uint tag, LLVMTypeRef mapped,
		LLVMMetadataRef[] members, string? name = null, string? identifier = null)
	{
		var (file, line) = GetDeclarationSite(type);
		using var typeName = new MarshaledString(name ?? GetDebugTypeName(type));
		using var uniqueId = new MarshaledString(identifier ?? Mangling.MangleTypeName(type, _modules));
		fixed (LLVMMetadataRef* elements = members)
		{
			return tag == DwarfUnion
				? LLVM.DIBuilderCreateUnionType(DiBuilder, (LLVMOpaqueMetadata*)file.Handle, typeName,
					(nuint)typeName.Length, (LLVMOpaqueMetadata*)file.Handle, line, GetSizeInBits(mapped),
					GetAlignInBits(mapped), LLVMDIFlags.LLVMDIFlagZero, (LLVMOpaqueMetadata**)elements,
					(uint)members.Length, 0, uniqueId, (nuint)uniqueId.Length)
				: LLVM.DIBuilderCreateStructType(DiBuilder, (LLVMOpaqueMetadata*)file.Handle, typeName,
					(nuint)typeName.Length, (LLVMOpaqueMetadata*)file.Handle, line, GetSizeInBits(mapped),
					GetAlignInBits(mapped), LLVMDIFlags.LLVMDIFlagZero, null, (LLVMOpaqueMetadata**)elements,
					(uint)members.Length, 0, null, uniqueId, (nuint)uniqueId.Length);
		}
	}
	
	private LLVMMetadataRef CreatePlaceholder(TypeSymbol type)
	{
		var mapped = MapTypeSymbol(type);
		var (file, line) = GetDeclarationSite(type);
		using var typeName = new MarshaledString(GetDebugTypeName(type));
		using var uniqueId = new MarshaledString(Mangling.MangleTypeName(type, _modules));
		return LLVM.DIBuilderCreateReplaceableCompositeType(DiBuilder,
			type is RecordSymbol { IsUnion: true } ? DwarfUnion : DwarfStructure, typeName, (nuint)typeName.Length,
			(LLVMOpaqueMetadata*)file.Handle, (LLVMOpaqueMetadata*)file.Handle, line, 0, GetSizeInBits(mapped),
			GetAlignInBits(mapped), LLVMDIFlags.LLVMDIFlagFwdDecl, uniqueId, (nuint)uniqueId.Length);
	}
	
	private LLVMMetadataRef CreateRecordType(RecordSymbol record, LLVMMetadataRef scope)
	{
		var mapped = MapTypeSymbol(record);
		var fields = _typePool.GetMembers(record).OfType<FieldSymbol>().ToList();
		return CreateCompositeType(record, record.IsUnion ? DwarfUnion : DwarfStructure, mapped,
		[
			..fields.Select((field, i) => CreateMember(scope, field.Name, _typePool.GetTypeOfMember(field),
				record.IsUnion ? 0 : _targetData.OffsetOfElement(mapped, (uint)i)))
		]);
	}
	
	private LLVMMetadataRef CreateEnumType(EnumSymbol enumType, LLVMMetadataRef scope)
	{
		var tagType = _typePool.GetTagType(enumType);
		var cases = enumType.Cases.Select(enumCase => (enumCase.Name, _typePool.GetCaseValue(enumType, enumCase)));
		if (!enumType.HasPayload)
			return CreateEnumerationType(enumType, GetDebugTypeName(enumType), tagType, cases);
		
		var mapped = MapTypeSymbol(enumType);
		var payloadOffset = _targetData.OffsetOfElement(mapped, 1);
		var tag = CreateEnumerationType(enumType, $"{GetDebugTypeName(enumType)}.case", tagType, cases);
		return CreateCompositeType(enumType, DwarfStructure, mapped,
		[
			CreateMember(scope, "case", tag, MapTypeSymbol(tagType), 0),
			..enumType.Cases.Where(static enumCase => enumCase.Fields.Length > 0).Select(enumCase =>
				CreateMember(scope, enumCase.Name, CreateCaseType(enumType, enumCase), GetPayloadType(enumCase),
					payloadOffset))
		]);
	}
	
	private LLVMMetadataRef CreateCaseType(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		var mapped = GetPayloadType(enumCase);
		return CreateCompositeType(enumType, DwarfStructure, mapped,
			[
				..enumCase.Fields.Select((field, i) => CreateMember(current.DebugFile, field.Name,
					_typePool.GetTypeOfMember(field), _targetData.OffsetOfElement(mapped, (uint)i)))
			], $"{GetDebugTypeName(enumType)}.{enumCase.Name}",
			$"{Mangling.MangleTypeName(enumType, _modules)}.{enumCase.Name}");
	}
	
	private LLVMMetadataRef CreateMatchEnumType(EnumSymbol enumType)
	{
		var matched = _typePool.GetMatchedType(enumType);
		if (matched is not IntegerType integer)
			return current.DiBuilder.CreateTypedef(GetDebugType(matched), GetDebugTypeName(enumType),
				GetDeclarationSite(enumType).File, GetDeclarationSite(enumType).Line, current.DebugFile, 0);
		
		var cases = enumType.Cases.SelectMany(enumCase => _typePool.GetMatchValues(enumType, enumCase)
			.Select(value => (enumCase.Name, value)));
		
		return CreateEnumerationType(enumType, GetDebugTypeName(enumType), integer, cases);
	}
	
	private LLVMMetadataRef CreateEnumerationType(EnumSymbol enumType, string name, IntegerType tagType,
		IEnumerable<(string Name, BigInteger Value)> cases)
	{
		var mapped = MapTypeSymbol(tagType);
		LLVMMetadataRef[] enumerators =
			[..cases.Select(enumCase => CreateEnumerator(enumCase.Name, enumCase.Value, tagType, mapped.IntWidth))];
		
		var (file, line) = GetDeclarationSite(enumType);
		using var typeName = new MarshaledString(name);
		fixed (LLVMMetadataRef* elements = enumerators)
		{
			return LLVM.DIBuilderCreateEnumerationType(DiBuilder, (LLVMOpaqueMetadata*)file.Handle, typeName,
				(nuint)typeName.Length, (LLVMOpaqueMetadata*)file.Handle, line, GetSizeInBits(mapped),
				GetAlignInBits(mapped), (LLVMOpaqueMetadata**)elements, (uint)enumerators.Length,
				(LLVMOpaqueMetadata*)GetDebugType(tagType).Handle);
		}
	}
	
	private LLVMMetadataRef CreateEnumerator(string name, BigInteger value, IntegerType tagType, uint bits)
	{
		using var enumeratorName = new MarshaledString(name);
		if (value >= long.MinValue && value <= ulong.MaxValue)
		{
			var isUnsigned = value > long.MaxValue;
			return LLVM.DIBuilderCreateEnumerator(DiBuilder, enumeratorName, (nuint)enumeratorName.Length,
				isUnsigned ? (long)(ulong)value : (long)value, isUnsigned ? 1 : 0);
		}
		
		var pattern = value & ((BigInteger.One << (int)bits) - 1);
		var words = new ulong[(bits + 63) / 64];
		for (var i = 0; i < words.Length; i++)
			words[i] = (ulong)((pattern >> (64 * i)) & ulong.MaxValue);
		
		fixed (ulong* data = words)
			return LLVM.DIBuilderCreateEnumeratorOfArbitraryPrecision(DiBuilder, enumeratorName,
				(nuint)enumeratorName.Length, bits, data, tagType.IsSigned ? 0 : 1);
	}
}