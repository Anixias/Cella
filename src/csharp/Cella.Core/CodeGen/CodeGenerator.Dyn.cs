using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private static DynType? FindDynDispatch(FunctionInfo function) =>
		function.Symbol.Trait is not null && !function.TypeArguments.IsDefaultOrEmpty &&
		function.TypeArguments[0] is DynType dyn
			? dyn
			: null;
	
	private LLVMValueRef EmitDynCall(FunctionInfo info, DynType dyn, CallValue v, LLVMBuilderRef builder)
	{
		var args = v.Arguments.Select((argument, i) => i == 0 && CopiesReceiver(info)
			? EmitAddress(argument, builder)
			: EmitValue(argument, builder)).ToArray();
		
		var trait = _typePool.GetTraitType(info.Symbol.Trait!, TypePool.GetTraitArguments(info));
		var table = EmitDynPath(dyn.Instance!, trait, builder.BuildExtractValue(args[0], 1, "table"), builder);
		args[0] = builder.BuildExtractValue(args[0], 0, "object");
		var slot = (uint)(_typePool.GetDynSlot(trait.Trait, info.Symbol) + DynTableHeader.Length);
		var entry = builder.BuildStructGEP2(GetDynTableType(trait.Trait), table, slot, "entry");
		var method = builder.BuildLoad2(OpaquePointer, entry, "method");
		var parameterTypes = MapParameterTypes(info, type => type is DynType ? OpaquePointer : MapValueType(type));
		parameterTypes[0] = OpaquePointer;
		var functionType = CreateInternalFunctionType(info.Signature.ReturnType, [], parameterTypes);
		return BuildInternalCall(builder, functionType, method, info.Signature.ReturnType, [], args);
	}
	
	private LLVMValueRef EmitDynPath(TraitType from, TraitType to, LLVMValueRef table, LLVMBuilderRef builder)
	{
		var owner = from;
		foreach (var next in _typePool.FindDynPath(from, to)!.Value)
		{
			var members = _typePool.FindDynMembers(owner.Trait)!.Value.Length;
			var slot = (uint)(DynTableHeader.Length + members + _typePool.GetDynRequirements(owner).IndexOf(next));
			var entry = builder.BuildStructGEP2(GetDynTableType(owner.Trait), table, slot, "entry");
			table = builder.BuildLoad2(OpaquePointer, entry, "table");
			owner = next;
		}
		
		return table;
	}
	
	private LLVMTypeRef[] DynTableHeader =>
	[
		_context.GetIntType(128), OpaquePointer, MapTypeSymbol(NativeSymbols.UIntSize),
		MapTypeSymbol(NativeSymbols.UIntSize)
	];
	
	private LLVMTypeRef GetDynTableType(TraitSymbol trait)
	{
		var entries = _typePool.FindDynMembers(trait)!.Value.Length + _typePool.CountDynRequirements(trait) +
		              _typePool.GetDynCalls(trait).Length;
		
		return _context.GetStructType([..DynTableHeader, ..Enumerable.Repeat(OpaquePointer, entries)], false);
	}
	
	private void EmitDynDrop(LLVMValueRef pointer, LLVMBuilderRef builder)
	{
		var table = builder.BuildExtractValue(pointer, 1, "table");
		var entry = builder.BuildStructGEP2(_context.GetStructType(DynTableHeader, false), table, 1, "entry");
		var glue = builder.BuildLoad2(OpaquePointer, entry, "drop");
		builder.BuildCall2(DropGlueType, glue, [builder.BuildExtractValue(pointer, 0, "object")]);
	}
	
	private LLVMValueRef EmitDynLayout(DynLayoutConversion conversion, ConversionValue v, LLVMBuilderRef builder)
	{
		var table = builder.BuildExtractValue(EmitValue(v.Source, builder), 1, "table");
		var header = _context.GetStructType(DynTableHeader, false);
		var entry = builder.BuildStructGEP2(header, table, conversion.IsAlignment ? 3u : 2u, "entry");
		return builder.BuildLoad2(MapTypeSymbol(NativeSymbols.UIntSize), entry, "layout");
	}
	
	private LLVMValueRef GetObjectDropGlue(TypeSymbol objectType)
	{
		if (_typePool.NeedsDrop(objectType))
			return GetDropGlue(objectType);
		
		var existing = current.Module.GetNamedFunction("drop$");
		if (existing.Handle != nint.Zero)
			return existing;
		
		var glue = current.Module.AddFunction("drop$", DropGlueType);
		glue.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = CreateGeneratedBuilder(glue, glue.Name, SourceLocation.None);
		builder.BuildRetVoid();
		return glue;
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
		var target = GetDynTarget(Substitute(conversion.To));
		var objectType = Substitute(conversion.ObjectType);
		if (target.Function is { } function)
			return GetFunctionDynTable(target, function, objectType);
		
		var trait = target.Instance!;
		return GetDynTable(trait, objectType,
			declared.Parameter is null ? conversion.Members : GetDynMemberInfos(trait, objectType));
	}
	
	private LLVMTypeRef FunctionDynTableType => _context.GetStructType([..DynTableHeader, OpaquePointer], false);
	
	private LLVMValueRef GetFunctionDynTable(DynType dyn, FunctionType function, TypeSymbol objectType)
	{
		var name = Mangling.MangleInstantiation($"?{Mangling.MangleTypeName(dyn, _modules)}", [objectType], _modules);
		var existing = current.Module.GetNamedGlobal(name);
		if (existing.Handle != nint.Zero)
			return existing;
		
		var call = GetDynCallEntry(function, objectType);
		var size = _typePool.SizeTable.GetSize(objectType);
		var table = current.Module.AddGlobal(FunctionDynTableType, name);
		table.Initializer = _context.GetConstStruct(
		[
			EmitTypeId(objectType), GetObjectDropGlue(objectType),
			EmitSizeConstant((size.CountBits(_pointerSize * 8) + 7) / 8, true),
			EmitSizeConstant(new BigInteger((size.CountAlignmentBits(_pointerSize * 8) + 7) / 8), true),
			call
		], false);
		
		table.IsGlobalConstant = true;
		table.Linkage = LLVMLinkage.LLVMInternalLinkage;
		return table;
	}
	
	private LLVMValueRef GetDynCallEntry(FunctionType function, TypeSymbol objectType) => objectType switch
	{
		ClosureType closure when TakesCopies(closure.Signature, function) => GetClosureCallThunk(function, closure),
		ClosureType closure => GetFunctionValue(closure.Function),
		FunctionType stored => GetStoredCallThunk(function, stored),
		_ => throw new InvalidOperationException()
	};
	
	private uint GetDynCallSlot(TraitType trait, FunctionType function)
	{
		var calls = _typePool.GetDynCalls(trait);
		var index = calls.IndexOf(function);
		if (index < 0)
			index = calls.Select(_typePool.GetRefFunctionType).ToImmutableArray().IndexOf(function);
		
		if (index < 0)
			throw new InvalidOperationException($"'{trait.Name}' has no call slot for '{function.Name}'");
		
		return (uint)(DynTableHeader.Length + _typePool.FindDynMembers(trait.Trait)!.Value.Length +
		              _typePool.CountDynRequirements(trait.Trait) + index);
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
		if (existing.Handle != nint.Zero)
			return existing;
		
		LLVMValueRef[] entries =
		[
			..members.Select(GetDynEntry),
			.._typePool.GetDynRequirements(trait).Select(required => required is null
				? LLVMValueRef.CreateConstNull(OpaquePointer)
				: GetDynTable(required, objectType, GetDynMemberInfos(required, objectType))),
			.._typePool.GetDynCalls(trait).Select(function => GetDynCallEntry(function, objectType))
		];
		
		var size = _typePool.SizeTable.GetSize(objectType);
		var table = current.Module.AddGlobal(GetDynTableType(trait.Trait), name);
		table.Initializer = _context.GetConstStruct(
		[
			EmitTypeId(objectType), GetObjectDropGlue(objectType),
			EmitSizeConstant((size.CountBits(_pointerSize * 8) + 7) / 8, true),
			EmitSizeConstant(new BigInteger((size.CountAlignmentBits(_pointerSize * 8) + 7) / 8), true),
			..entries
		], false);
		
		table.IsGlobalConstant = true;
		table.Linkage = LLVMLinkage.LLVMInternalLinkage;
		return table;
	}
	
	private static bool CopiesReceiver(FunctionInfo member) =>
		member.Signature.ParameterTypes.Length > 0 && member.Signature.GetMode(0) == ParameterMode.Value;
	
	private LLVMValueRef GetDynEntry(FunctionInfo member)
	{
		var function = GetFunctionValue(member);
		if (!CopiesReceiver(member))
			return function;
		
		var name = $"dyn${function.Name}";
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != nint.Zero)
			return existing;
		
		var returnType = member.Signature.ReturnType;
		var parameterTypes = MapParameterTypes(member);
		parameterTypes[0] = OpaquePointer;
		var thunk = current.Module.AddFunction(name, CreateInternalFunctionType(returnType, [], parameterTypes));
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = CreateGeneratedBuilder(thunk, name, SourceLocation.None);
		var args = parameterTypes.Select((_, i) => thunk.GetParam((uint)i)).ToArray();
		args[0] = LoadValue(builder, member.Signature.ParameterTypes[0], args[0], "self");
		var result = BuildInternalCall(builder, current.Functions[member].FunctionType, function, returnType, [], args);
		EmitThunkReturn(thunk, returnType, returnType, result, builder);
		return thunk;
	}
	
	private LLVMValueRef EmitTypeId(TypeSymbol type)
	{
		var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Mangling.MangleTypeName(type, _modules)));
		return LLVMValueRef.CreateConstIntOfArbitraryPrecision(_context.GetIntType(128),
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
		var id = builder.BuildLoad2(_context.GetIntType(128), table, "type");
		return builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, id, EmitTypeId(Substitute(conversion.Tested)), "is");
	}
}