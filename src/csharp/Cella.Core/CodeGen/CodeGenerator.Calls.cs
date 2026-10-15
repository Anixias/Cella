using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed unsafe partial class CodeGenerator
{
	private LLVMValueRef EmitCall(CallValue v, LLVMBuilderRef builder)
	{
		var info = SubstituteFunction(v.Function);
		if (!IsActive(info.Symbol))
			return info.Signature.ReturnType == NativeSymbols.Void
				? default
				: EmitZeroed(builder, info.Signature.ReturnType, "erased");
		
		if (FindDynDispatch(info) is { } dyn)
			return EmitDynCall(info, dyn, v, builder);
		
		switch (FindWitness(info))
		{
			case NativeWitness native:
				return EmitNativeWitness(info, native, [..v.Arguments.Select(a => EmitValue(a, builder))],
					v.SourceLocation, builder);
			
			case ConversionWitness conversion:
				return EmitConversionWitness(info, conversion.Conversion,
					[..v.Arguments.Select(a => EmitValue(a, builder))], v.SourceLocation, builder);
		}
		
		GetFunctionValue(info);
		var function = current.Functions[info];
		var args = v.Arguments.Select(a => EmitValue(a, builder)).ToList();
		if (function.CSignature is not { } signature)
			return BuildInternalCall(builder, function.FunctionType, function.FunctionValue, info.Signature.ReturnType,
				[], args);
		
		return EmitCCall(signature, function.FunctionType, function.FunctionValue, info.Signature.ParameterTypes,
			info.Signature.ReturnType, args, builder);
	}
	
	private LLVMValueRef EmitIndirectCall(IndirectCallValue v, LLVMBuilderRef builder)
	{
		var functionType = (FunctionType)Substitute(v.FunctionType);
		var target = EmitValue(v.Target, builder);
		var args = v.Arguments.Select(a => EmitValue(a, builder)).ToList();
		
		var returnType = MapTypeSymbol(functionType.ReturnType);
		if (!functionType.IsExternal)
		{
			var parameterTypes = functionType.ParameterTypes
				.Select((type, i) => MapParameterType(type, functionType.ParameterModes[i]));
			
			var codeType = CreateInternalFunctionType(functionType.ReturnType, [OpaquePointer], parameterTypes);
			var code = builder.BuildExtractValue(target, 0, "code");
			var environment = builder.BuildExtractValue(target, 1, "env");
			return BuildInternalCall(builder, codeType, code, functionType.ReturnType, [environment], args);
		}
		
		var signature = _cAbi.Classify(functionType.ParameterTypes.Select(MapTypeSymbol), returnType);
		return EmitCCall(signature, signature.CreateFunctionType(functionType.IsVariadic), target,
			functionType.ParameterTypes, functionType.ReturnType, args, builder);
	}
	
	private LLVMValueRef EmitCCall(CSignature signature, LLVMTypeRef functionType, LLVMValueRef callee,
		IReadOnlyList<TypeSymbol> parameterTypes, TypeSymbol returnType, List<LLVMValueRef> args,
		LLVMBuilderRef builder)
	{
		for (var i = 0; i < signature.Parameters.Length; i++)
			args[i] = PassCArgument(signature.Parameters[i], parameterTypes[i], args[i], builder);
		
		var returnSlot = default(LLVMValueRef);
		if (signature.Return.Kind == CPassKind.Indirect)
		{
			returnSlot = BuildIndirectSlot(builder, signature.Return.Type);
			args.Insert(0, returnSlot);
		}
		
		var result = builder.BuildCall2(functionType, callee, args.ToArray());
		AddBoolExtensions(result, signature, true);
		switch (signature.Return.Kind)
		{
			case CPassKind.Coerced:
			{
				var slot = BuildEntryAlloca(builder, signature.Return.Type, "coerce");
				builder.BuildStore(result, slot);
				return IsLarge(returnType) ? slot : builder.BuildLoad2(MapTypeSymbol(returnType), slot);
			}
			
			case CPassKind.Indirect:
				AddSretAttribute(result, signature.Return.Type, true);
				return IsLarge(returnType) ? returnSlot : builder.BuildLoad2(signature.Return.Type, returnSlot);
			
			default:
				return result;
		}
	}
	
	private LLVMValueRef PassCArgument(CPass pass, TypeSymbol type, LLVMValueRef value, LLVMBuilderRef builder)
	{
		switch (pass.Kind)
		{
			case CPassKind.Coerced:
			{
				var slot = BuildEntryAlloca(builder, pass.Type, "coerce");
				StoreValue(builder, type, value, slot);
				return builder.BuildLoad2(pass.Type, slot);
			}
			
			case CPassKind.Indirect:
			{
				var copy = BuildIndirectSlot(builder, pass.Type);
				StoreValue(builder, type, value, copy);
				return copy;
			}
			
			default:
				return value;
		}
	}
	
	private LLVMValueRef ReceiveCArgument(CPass pass, TypeSymbol type, LLVMValueRef value, LLVMBuilderRef builder)
	{
		switch (pass.Kind)
		{
			case CPassKind.Coerced:
			{
				var slot = BuildEntryAlloca(builder, pass.Type, "coerce");
				builder.BuildStore(value, slot);
				return IsLarge(type) ? slot : builder.BuildLoad2(MapTypeSymbol(type), slot);
			}
			
			case CPassKind.Indirect:
				return IsLarge(type) ? value : builder.BuildLoad2(MapTypeSymbol(type), value);
			
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
	
	private static LLVMValueRef BuildFunctionValue(LLVMTypeRef type, LLVMValueRef code, LLVMValueRef environment,
		LLVMBuilderRef builder)
	{
		var value = builder.BuildInsertValue(type.Undef, code, 0, "function");
		return builder.BuildInsertValue(value, environment, 1, "function");
	}
	
	private LLVMValueRef GetExternalCallThunk(FunctionType external) =>
		GetCallThunk($"call${Mangling.MangleTypeName(external, _modules)}", external, (builder, environment, args) =>
			EmitExternalCall(external, environment, args, builder));
	
	private LLVMValueRef GetStoredCallThunk(FunctionType signature, FunctionType stored) => GetCallThunk(
		$"call${Mangling.MangleTypeName(signature, _modules)}${Mangling.MangleTypeName(stored, _modules)}", signature,
		(builder, environment, args) =>
		{
			var copied = LoadCopies(signature, stored, args, builder);
			if (stored.IsExternal)
				return EmitExternalCall(stored, builder.BuildLoad2(OpaquePointer, environment, "function"), copied,
					builder);
			
			var function = builder.BuildLoad2(MapTypeSymbol(stored), environment, "function");
			var codeType = CreateInternalFunctionType(stored.ReturnType, [OpaquePointer], MapCallParameters(stored));
			return BuildInternalCall(builder, codeType, builder.BuildExtractValue(function, 0, "code"),
				stored.ReturnType, [builder.BuildExtractValue(function, 1, "env")], copied);
		});
	
	private LLVMValueRef GetClosureCallThunk(FunctionType signature, ClosureType closure)
	{
		var code = GetFunctionValue(closure.Function);
		var codeType = CreateInternalFunctionType(closure.Signature.ReturnType, [OpaquePointer],
			MapCallParameters(closure.Signature));
		
		return GetCallThunk($"call${Mangling.MangleTypeName(signature, _modules)}${code.Name}", signature,
			(builder, environment, args) => BuildInternalCall(builder, codeType, code, closure.Signature.ReturnType,
				[environment], LoadCopies(signature, closure.Signature, args, builder)));
	}
	
	private static bool TakesCopies(FunctionType declared, FunctionType passed) => declared.ParameterModes
		.Where((mode, i) => i < passed.ParameterModes.Length && TakesCopy(passed.ParameterModes[i], mode))
		.Any();
	
	private static bool TakesCopy(ParameterMode passed, ParameterMode declared) =>
		TypePool.PassesByPointer(passed) && !TypePool.PassesByPointer(declared);
	
	private LLVMValueRef[] LoadCopies(FunctionType passed, FunctionType declared, LLVMValueRef[] args,
		LLVMBuilderRef builder) =>
	[
		..args.Select((arg, i) => i < declared.ParameterTypes.Length &&
		                          TakesCopy(passed.ParameterModes[i], declared.ParameterModes[i])
			? LoadValue(builder, declared.ParameterTypes[i], arg, "argument")
			: arg)
	];
	
	private LLVMValueRef GetCallThunk(string name, FunctionType signature,
		Func<LLVMBuilderRef, LLVMValueRef, LLVMValueRef[], LLVMValueRef> emitCall)
	{
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		LLVMTypeRef[] parameterTypes = [..MapCallParameters(signature)];
		var thunk = current.Module.AddFunction(name,
			CreateInternalFunctionType(signature.ReturnType, [OpaquePointer], parameterTypes));
		
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = CreateGeneratedBuilder(thunk, name, SourceLocation.None);
		var first = IsLarge(signature.ReturnType) ? 2u : 1u;
		var result = emitCall(builder, thunk.GetParam(0),
			[..parameterTypes.Select((_, i) => thunk.GetParam((uint)i + first))]);
		
		EmitThunkReturn(thunk, signature.ReturnType, result, 1, builder);
		return thunk;
	}
	
	private IEnumerable<LLVMTypeRef> MapCallParameters(FunctionType signature) =>
		signature.ParameterTypes.Select((type, i) => MapParameterType(type, signature.ParameterModes[i]));
	
	private LLVMValueRef EmitExternalCall(FunctionType external, LLVMValueRef function, LLVMValueRef[] args,
		LLVMBuilderRef builder)
	{
		var returnType = MapTypeSymbol(external.ReturnType);
		var signature = _cAbi.Classify(external.ParameterTypes.Select(MapTypeSymbol), returnType);
		return EmitCCall(signature, signature.CreateFunctionType(false), function, external.ParameterTypes,
			external.ReturnType, [..args], builder);
	}
}