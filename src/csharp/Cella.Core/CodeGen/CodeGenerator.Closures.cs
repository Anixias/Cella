using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private LLVMValueRef EmitFunctionReference(FunctionInfo function, TypeSymbol type)
	{
		function = SubstituteFunction(function);
		if (type is FunctionType { IsExternal: false })
		{
			var environment = LLVMValueRef.CreateConstNull(OpaquePointer);
			return _context.GetConstStruct([
				GetClosureThunk(function, (FunctionType)Substitute(type)),
				environment
			], false);
		}
		
		return function.Symbol.IsExternal ? GetFunctionValue(function) : GetExternalThunk(function);
	}
	
	private LLVMValueRef EmitClosure(ClosureValue closure, LLVMBuilderRef builder)
	{
		var environmentType = LLVMTypeRef.CreateArray(OpaquePointer, (uint)closure.Captures.Length);
		var environment = BuildEntryAlloca(builder, environmentType, "environment");
		for (var i = 0; i < closure.Captures.Length; i++)
		{
			var index = LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)i);
			var slot = builder.BuildGEP2(environmentType, environment, new[] { Int32Zero, index }, "capture");
			builder.BuildStore(EmitValue(closure.Captures[i], builder), slot);
		}
		
		var value = builder.BuildInsertValue(MapTypeSymbol(closure.Type).Undef, GetFunctionValue(closure.Function), 0,
			"closure");
		
		return builder.BuildInsertValue(value, environment, 1, "closure");
	}
	
	private LLVMValueRef EmitOwnClosure(OwnClosureValue closure, LLVMBuilderRef builder)
	{
		var closureType = MapTypeSymbol(closure.Type);
		if (IsLarge(closure.Type))
		{
			var slot = BuildEntryAlloca(builder, closureType, "closure");
			for (var i = 0; i < closure.Captures.Length; i++)
				StoreValue(builder, closure.Captures[i].Type, EmitValue(closure.Captures[i], builder),
					builder.BuildStructGEP2(closureType, slot, (uint)i, "capture"));
			
			return slot;
		}
		
		var value = closureType.Undef;
		for (var i = 0; i < closure.Captures.Length; i++)
		{
			var capture = LoadSsa(builder, closure.Captures[i].Type, EmitValue(closure.Captures[i], builder));
			value = builder.BuildInsertValue(value, capture, (uint)i, "closure");
		}
		
		return value;
	}
	
	private LLVMValueRef EmitClosureField(ClosureFieldValue capture, LLVMBuilderRef builder)
	{
		var environmentType = _context.GetStructType([..capture.Fields.Select(MapTypeSymbol)], false);
		return builder.BuildStructGEP2(environmentType, currentFunction.FunctionValue.GetParam(0), (uint)capture.Index,
			"capture");
	}
	
	private LLVMValueRef EmitEnvironment(EnvironmentValue capture, LLVMBuilderRef builder)
	{
		var index = LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)capture.Index);
		var slot = builder.BuildGEP2(OpaquePointer, currentFunction.FunctionValue.GetParam(0), new[] { index },
			"capture");
		
		return builder.BuildLoad2(OpaquePointer, slot, "capture");
	}
	
	private LLVMValueRef Int32Zero => LLVMValueRef.CreateConstInt(_context.Int32Type, 0);
	
	private LLVMValueRef GetClosureThunk(FunctionInfo function, FunctionType type)
	{
		GetFunctionValue(function);
		var target = current.Functions[function];
		var actual = function.Signature;
		var adapts = FunctionType.CanAdapt(actual.ReturnType, type.ReturnType) || actual.ParameterTypes
			.Where((parameter, i) => FunctionType.CanAdapt(type.ParameterTypes[i], parameter) ||
			                         TakesCopy(type.ParameterModes[i], actual.GetMode(i)))
			.Any();
		
		var name = adapts
			? $"{target.FunctionValue.Name}$fun${Mangling.MangleTypeName(type, _modules)}"
			: $"{target.FunctionValue.Name}$fun";
		
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var parameterTypes = adapts
			? MapCallParameters(type).ToArray()
			: actual.ParameterTypes.Select((parameter, i) => MapParameterType(parameter, actual.GetMode(i))).ToArray();
		
		var returnType = adapts ? type.ReturnType : actual.ReturnType;
		var thunk = current.Module.AddFunction(name,
			CreateInternalFunctionType(returnType, [OpaquePointer], parameterTypes));
		
		thunk.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = CreateGeneratedBuilder(thunk, GetAdapterName(function), function.Symbol.Definition);
		var args = parameterTypes
			.Select((_, i) => AdaptFunction(type.ParameterTypes[i], actual.ParameterTypes[i],
				ReceiveCopy(type.ParameterModes[i], actual, i, thunk.GetParam((uint)i + 1), builder), builder))
			.ToList();
		
		var result = target.CSignature is { } signature
			? EmitCCall(signature, target.FunctionType, target.FunctionValue, actual.ParameterTypes, actual.ReturnType,
				args, builder)
			: BuildInternalCall(builder, target.FunctionType, target.FunctionValue, actual.ReturnType, [], args);
		
		EmitThunkReturn(thunk, returnType, actual.ReturnType,
			AdaptFunction(actual.ReturnType, type.ReturnType, result, builder), builder);
		
		return thunk;
	}
	
	private LLVMValueRef AdaptFunction(TypeSymbol from, TypeSymbol to, LLVMValueRef value, LLVMBuilderRef builder) =>
		Substitute(from) is FunctionType source && Substitute(to) is var target && FunctionType.CanAdapt(source, target)
			? BuildFunctionValue(MapTypeSymbol(target), GetExternalCallThunk(source), value, builder)
			: value;
	
	private LLVMValueRef ReceiveCopy(ParameterMode passed, FunctionSignature actual, int index, LLVMValueRef value,
		LLVMBuilderRef builder) => TakesCopy(passed, actual.GetMode(index))
		? LoadValue(builder, actual.ParameterTypes[index], value, "argument")
		: value;
	
	private LLVMValueRef GetExternalThunk(FunctionInfo function)
	{
		GetFunctionValue(function);
		var target = current.Functions[function];
		var name = $"{target.FunctionValue.Name}$ext";
		var existing = current.Module.GetNamedFunction(name);
		if (existing.Handle != IntPtr.Zero)
			return existing;
		
		var signature = ClassifyC(function.Signature.ParameterTypes, function.Signature.ReturnType);
		var thunk = current.Module.AddFunction(name, signature.CreateFunctionType(false));
		var isIndirect = signature.Return.Kind == CPassKind.Indirect;
		if (isIndirect)
			AddSretAttribute(thunk, signature.Return.Type, false);
		
		AddCAttributes(thunk, signature, false);
		if (target.FunctionValue.DLLStorageClass == LLVMDLLStorageClass.LLVMDLLImportStorageClass)
		{
			Import(thunk, function.Symbol);
			return thunk;
		}
		
		ShareDefinition(thunk, IsExported(function.Symbol));
		using var builder = CreateGeneratedBuilder(thunk, GetAdapterName(function), function.Symbol.Definition);
		var first = isIndirect ? 1u : 0u;
		var args = function.Signature.ParameterTypes
			.Select((type, i) => ReceiveCArgument(signature.Parameters[i], type, thunk.GetParam(first + (uint)i),
				builder))
			.ToArray();
		
		var returnType = function.Signature.ReturnType;
		var result = BuildInternalCall(builder, target.FunctionType, target.FunctionValue, returnType, [], args);
		if (target.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
		{
			builder.BuildRetVoid();
		}
		else if (isIndirect)
		{
			StoreValue(builder, returnType, result, thunk.GetParam(0));
			builder.BuildRetVoid();
		}
		else
		{
			builder.BuildRet(PassCArgument(signature.Return, returnType, result, builder));
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
			value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
		}
		else if (!IsDefinedHere(function.Symbol))
		{
			Import(value, function.Symbol);
		}
		else if (function.Symbol.Visibility != Visibility.Public)
		{
			value.Visibility = LLVMVisibility.LLVMHiddenVisibility;
		}
		
		return value;
	}
	
	private bool ExportsAdapter(FunctionInfo function) =>
		_inputs.Exports.Contains(function.Symbol) &&
		function.Symbol is { Kind: FunctionKind.Free, IsExternal: false, Captures.IsEmpty: true } &&
		!IsOpenGeneric(function) &&
		function.Signature.ParameterModes.All(static mode => mode == ParameterMode.ReadOnly) &&
		function.Signature.ParameterTypes.Append(function.Signature.ReturnType).All(_typePool.IsExtCompatible);
	
	private bool IsDefinedHere(FunctionSymbol symbol)
	{
		while (symbol.ContainingFunction is { } outer)
			symbol = outer.Symbol;
		
		return _assemblySymbol.SignatureTable.Functions.ContainsKey(symbol);
	}
}