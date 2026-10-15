using Cella.Core.Binding;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private LLVMFunctionInfo CreateFunction(LLVMModuleRef llvmModule, FunctionInfo function, string? name = null)
	{
		if (current.Functions.TryGetValue(function, out var existing))
			return existing;
		
		var signature = function.Signature;
		var symbol = function.Symbol;
		var returnType = MapTypeSymbol(signature.ReturnType);
		var paramLlvmTypes = MapParameterTypes(function);
		
		CSignature? cSignature = symbol.IsExternal
			? _cAbi.Classify(MapParameterTypes(function, MapTypeSymbol), returnType)
			: null;
		
		var leading = symbol.Captures.IsEmpty ? 0 : 1;
		var functionType = cSignature?.CreateFunctionType(signature.IsVariadic) ??
		                   CreateInternalFunctionType(signature.ReturnType, paramLlvmTypes.Take(leading),
			                   paramLlvmTypes.Skip(leading), signature.IsVariadic);
		
		name ??= function.MangledName ?? (symbol.Kind == FunctionKind.External ? symbol.ExternalName : null) ??
			symbol.Name;
		
		var declared = symbol.Kind == FunctionKind.External ? FindNamedFunction(llvmModule, name) : default;
		var functionValue = declared.Handle != IntPtr.Zero ? declared : llvmModule.AddFunction(name, functionType);
		var isFunction = functionValue.IsAFunction.Handle != IntPtr.Zero;
		if (isFunction && cSignature is { Return: { Kind: CPassKind.Indirect } sret })
			AddSretAttribute(functionValue, sret.Type, false);
		
		if (isFunction && cSignature is { } boolSignature)
			AddBoolExtensions(functionValue, boolSignature, false);
		
		if (isFunction && signature.ReturnType is NeverType)
			AddFunctionAttribute(functionValue, "noreturn");
		
		var functionInfo = new LLVMFunctionInfo(functionValue, functionType, returnType, cSignature,
			cSignature is null && IsLarge(signature.ReturnType) ? (uint)leading : null);
		
		current.Functions.Add(function, functionInfo);
		return functionInfo;
	}
	
	private void BuildEntryPoint(LLVMFunctionInfo entryPoint, FunctionSignature signature, SourceLocation location)
	{
		var takesArguments = !signature.ParameterTypes.IsEmpty;
		var isWide = takesArguments && TargetTriple.Contains("windows");
		var main = current.Module.AddFunction(isWide ? "wmain" : "main", LLVMTypeRef.CreateFunction(_context.Int32Type,
			takesArguments ? [_context.Int32Type, OpaquePointer] : []));
		
		main.Linkage = LLVMLinkage.LLVMExternalLinkage;
		using var builder = CreateGeneratedBuilder(main, main.Name, location);
		
		LLVMValueRef[] arguments = [];
		Action? cleanup = null;
		if (takesArguments)
		{
			var (strings, count) = EmitArguments(main, isWide, builder);
			if (signature.ParameterTypes.Length == 2)
				arguments = [strings, count];
			else
				arguments = [EmitArgumentsValue(signature, strings, count, builder, out cleanup)];
		}
		
		var result = builder.BuildCall2(entryPoint.FunctionType, entryPoint.FunctionValue, arguments);
		cleanup?.Invoke();
		builder.BuildRet(entryPoint.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind
			? LLVMValueRef.CreateConstInt(_context.Int32Type, 0)
			: result);
	}
	
	private (LLVMValueRef Strings, LLVMValueRef Count) EmitArguments(LLVMValueRef main, bool isWide,
		LLVMBuilderRef builder)
	{
		var sizeType = MapTypeSymbol(NativeSymbols.UIntSize);
		var strType = MapTypeSymbol(NativeSymbols.Str);
		var count = builder.BuildZExt(main.GetParam(0), sizeType, "count");
		var strings = builder.BuildArrayAlloca(strType, count, "arguments");
		var entry = builder.InsertBlock;
		var header = _context.AppendBasicBlock(main, "argument");
		var body = _context.AppendBasicBlock(main, "argument.body");
		var done = _context.AppendBasicBlock(main, "arguments.done");
		builder.BuildBr(header);
		
		builder.PositionAtEnd(header);
		var index = builder.BuildPhi(sizeType, "index");
		builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, index, count, "more"), body, done);
		
		builder.PositionAtEnd(body);
		var source = builder.BuildLoad2(OpaquePointer,
			builder.BuildGEP2(OpaquePointer, main.GetParam(1), new[] { index }, "source.addr"), "source");
		
		var (data, length) = isWide ? EmitUtf8Argument(source, builder) : EmitByteArgument(source, builder);
		var text = builder.BuildInsertValue(strType.Undef, length, 0, "text");
		text = builder.BuildInsertValue(text, data, 1, "text");
		builder.BuildStore(text, builder.BuildGEP2(strType, strings, new[] { index }, "text.addr"));
		var next = builder.BuildAdd(index, LLVMValueRef.CreateConstInt(sizeType, 1), "next");
		builder.BuildBr(header);
		
		index.AddIncoming([LLVMValueRef.CreateConstInt(sizeType, 0), next], [entry, builder.InsertBlock], 2);
		builder.PositionAtEnd(done);
		return (strings, count);
	}
	
	private (LLVMValueRef Data, LLVMValueRef Length) EmitByteArgument(LLVMValueRef source, LLVMBuilderRef builder)
	{
		var sizeType = MapTypeSymbol(NativeSymbols.UIntSize);
		var lengthType = LLVMTypeRef.CreateFunction(sizeType, [OpaquePointer]);
		return (source, builder.BuildCall2(lengthType, GetCFunction("strlen", lengthType), new[] { source }, "length"));
	}
	
	private (LLVMValueRef Data, LLVMValueRef Length) EmitUtf8Argument(LLVMValueRef source, LLVMBuilderRef builder)
	{
		var int32 = _context.Int32Type;
		var convertType = LLVMTypeRef.CreateFunction(int32,
			[int32, int32, OpaquePointer, int32, OpaquePointer, int32, OpaquePointer, OpaquePointer]);
		
		var convert = GetCFunction("WideCharToMultiByte", convertType);
		var utf8 = LLVMValueRef.CreateConstInt(int32, 65001);
		var zero = LLVMValueRef.CreateConstInt(int32, 0);
		var terminated = LLVMValueRef.CreateConstInt(int32, unchecked((ulong)-1), true);
		var none = LLVMValueRef.CreateConstNull(OpaquePointer);
		var size = builder.BuildCall2(convertType, convert,
			new[] { utf8, zero, source, terminated, none, zero, none, none }, "size");
		
		var data = builder.BuildArrayAlloca(_context.Int8Type, size, "data");
		builder.BuildCall2(convertType, convert, [utf8, zero, source, terminated, data, size, none, none]);
		var length = builder.BuildSub(size, LLVMValueRef.CreateConstInt(int32, 1), "length");
		return (data, builder.BuildZExt(length, MapTypeSymbol(NativeSymbols.UIntSize), "length"));
	}
	
	private LLVMValueRef EmitArgumentsValue(FunctionSignature signature, LLVMValueRef strings, LLVMValueRef count,
		LLVMBuilderRef builder, out Action? cleanup)
	{
		var type = signature.GetDeclaredType(0);
		var mode = signature.GetMode(0);
		var slot = SpillValue(builder, type, EmitZeroed(builder, type, "arguments"), "arguments");
		switch (_typePool.FindArgumentsWitness(type))
		{
			case FunctionWitness { Info: var constructor }:
			{
				GetFunctionValue(constructor);
				var function = current.Functions[constructor];
				builder.BuildCall2(function.FunctionType, function.FunctionValue, [slot, strings, count]);
				break;
			}
			
			case MemberwiseWitness:
			{
				var mapped = MapTypeSymbol(type);
				builder.BuildStore(strings, builder.BuildStructGEP2(mapped, slot, 0, "data"));
				builder.BuildStore(count, builder.BuildStructGEP2(mapped, slot, 1, "length"));
				break;
			}
		}
		
		cleanup = mode == ParameterMode.Own ? null : () => EmitDropCall(type, slot, builder);
		return TypePool.PassesByPointer(mode) ? slot : LoadValue(builder, type, slot, "arguments");
	}
	
	private void BuildFunction(LoweredFunction function, FunctionInfo info)
	{
		currentFunction = current.Functions[info];
		var functionValue = currentFunction.FunctionValue;
		if (_config.DebugInfo)
			StartFunctionDebugInfo(function, info, functionValue);
		
		var allocaBlock = _context.AppendBasicBlock(functionValue, "allocas");
		
		// Create blocks
		var blockMap = new Dictionary<BasicBlock, LLVMBasicBlockRef>();
		foreach (var block in function.Blocks)
			blockMap.Add(block, _context.AppendBasicBlock(functionValue, block.Label));
		
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
			EnterBlock(block);
			
			// First block allocates stack variables for parameters
			if (i == 0)
			{
				SetLocation(builder, info.Symbol.Definition);
				var parameters = function.Info.Symbol.Parameters;
				var signature = currentFunction.CSignature;
				var firstParameter = (signature?.Return.Kind == CPassKind.Indirect ? 1u : 0u) +
				                     (function.Info.Symbol.Captures.IsEmpty ? 0u : 1u) +
				                     (currentFunction.ResultParameter is null ? 0u : 1u);
				
				for (var p = 0; p < parameters.Length; p++)
				{
					var paramSymbol = parameters[p];
					var paramType = function.Info.Signature.ParameterTypes[p];
					var paramInfo = new VariableInfo(paramSymbol, paramType);
					
					var paramLlvmValue = functionValue.GetParam(firstParameter + (uint)p);
					if (!TypePool.PassesByPointer(function.Info, p))
					{
						if (signature is { Parameters: var passes })
							paramLlvmValue = ReceiveCArgument(passes[p], paramType, paramLlvmValue, builder);
						
						paramLlvmValue = SpillValue(builder, paramType, paramLlvmValue, paramSymbol.Name);
					}
					
					current.Variables[paramInfo] = paramLlvmValue;
					DeclareParameter(paramSymbol, p, function.Info.Signature, paramLlvmValue, builder);
				}
			}
			
			foreach (var instruction in block.Instructions)
				EmitInstruction(builder, instruction);
			
			EmitTerminator(builder, block.Terminator, blockMap);
		}
		
		builder.PositionAtEnd(allocaBlock);
		currentScope = currentSubprogram;
		SetLocation(builder, info.Symbol.Definition);
		var entryBranch = builder.BuildBr(blockMap[function.Blocks[0]]);
		if (_config.DebugInfo)
			FinishFunctionDebugInfo(entryBranch);
	}
	
	private static LLVMValueRef BuildEntryAlloca(LLVMBuilderRef builder, LLVMTypeRef type, string name)
	{
		using var allocaBuilder = type.Context.CreateBuilder();
		var entry = builder.InsertBlock.Parent.EntryBasicBlock;
		if (entry.Terminator is { Handle: not 0 } terminator)
			allocaBuilder.PositionBefore(terminator);
		else
			allocaBuilder.PositionAtEnd(entry);
		
		return allocaBuilder.BuildAlloca(type, name);
	}
	
	private bool IsLarge(TypeSymbol type)
	{
		type = Substitute(type);
		if (_largeTypes.TryGetValue(type, out var isLarge))
			return isLarge;
		
		isLarge = type is RecordSymbol or ArrayType or EnumSymbol { IsMatch: false } or ClosureType &&
		          MapTypeSymbol(type) is
			          { Kind: LLVMTypeKind.LLVMStructTypeKind or LLVMTypeKind.LLVMArrayTypeKind } mapped &&
		          _targetData.ABISizeOfType(mapped) > LargeValueBytes;
		
		_largeTypes[type] = isLarge;
		return isLarge;
	}
	
	private LLVMTypeRef MapValueType(TypeSymbol type) => IsLarge(type) ? OpaquePointer : MapTypeSymbol(type);
	
	private LLVMValueRef LoadValue(LLVMBuilderRef builder, TypeSymbol type, LLVMValueRef address, string name)
	{
		var mapped = MapTypeSymbol(type);
		if (!IsLarge(type))
			return builder.BuildLoad2(mapped, address, name);
		
		var copy = BuildTemporarySlot(builder, mapped, name);
		CopyValue(builder, mapped, copy, address);
		return copy;
	}
	
	private void StoreValue(LLVMBuilderRef builder, TypeSymbol type, LLVMValueRef value, LLVMValueRef address)
	{
		if (IsLarge(type))
			CopyValue(builder, MapTypeSymbol(type), address, value);
		else if (_targetData.StoreSizeOfType(MapTypeSymbol(type)) > 0)
			builder.BuildStore(value, address);
	}
	
	private LLVMValueRef SpillValue(LLVMBuilderRef builder, TypeSymbol type, LLVMValueRef value, string name)
	{
		if (IsLarge(type))
			return value;
		
		var slot = BuildEntryAlloca(builder, MapTypeSymbol(type), name);
		builder.BuildStore(value, slot);
		return slot;
	}
	
	private LLVMValueRef LoadSsa(LLVMBuilderRef builder, TypeSymbol type, LLVMValueRef value) =>
		IsLarge(type) ? builder.BuildLoad2(MapTypeSymbol(type), value, "value") : value;
	
	private LLVMValueRef EmitZeroed(LLVMBuilderRef builder, TypeSymbol type, string name)
	{
		var mapped = MapTypeSymbol(type);
		if (!IsLarge(type))
			return LLVMValueRef.CreateConstNull(mapped);
		
		var slot = BuildTemporarySlot(builder, mapped, name);
		EmitZeroFill(builder, mapped, slot);
		return slot;
	}
	
	private void EmitZeroFill(LLVMBuilderRef builder, LLVMTypeRef type, LLVMValueRef destination) =>
		builder.BuildMemSet(destination, LLVMValueRef.CreateConstInt(_context.Int8Type, 0), EmitByteCount(type),
			_targetData.ABIAlignmentOfType(type));
	
	private LLVMValueRef BuildTemporarySlot(LLVMBuilderRef builder, LLVMTypeRef type, string name)
	{
		var function = builder.InsertBlock.Parent;
		if (!_temporaryIndices.TryGetValue(function, out var index))
			return BuildEntryAlloca(builder, type, name);
		
		_temporaryIndices[function] = index + 1;
		if (!_temporarySlots.TryGetValue((function, type, index), out var slot))
			_temporarySlots[(function, type, index)] = slot = BuildEntryAlloca(builder, type, name);
		
		return slot;
	}
	
	private void CopyValue(LLVMBuilderRef builder, LLVMTypeRef type, LLVMValueRef destination, LLVMValueRef source)
	{
		var alignment = _targetData.ABIAlignmentOfType(type);
		builder.BuildMemCpy(destination, alignment, source, alignment, EmitByteCount(type));
	}
	
	private LLVMValueRef EmitByteCount(LLVMTypeRef type) =>
		LLVMValueRef.CreateConstInt(MapTypeSymbol(NativeSymbols.UIntSize), _targetData.ABISizeOfType(type));
	
	private LLVMValueRef ExtractField(LLVMBuilderRef builder, TypeSymbol aggregate, LLVMValueRef value, uint index,
		TypeSymbol field, string name)
	{
		if (!IsLarge(aggregate))
			return builder.BuildExtractValue(value, index, name);
		
		var address = builder.BuildStructGEP2(MapTypeSymbol(aggregate), value, index, name + ".addr");
		return LoadValue(builder, field, address, name);
	}
	
	private LLVMTypeRef CreateInternalFunctionType(TypeSymbol returnType, IEnumerable<LLVMTypeRef> leading,
		IEnumerable<LLVMTypeRef> parameters, bool isVariadic = false) => IsLarge(returnType)
		? LLVMTypeRef.CreateFunction(_context.VoidType, [..leading, OpaquePointer, ..parameters], isVariadic)
		: LLVMTypeRef.CreateFunction(MapTypeSymbol(returnType), [..leading, ..parameters], isVariadic);
	
	private LLVMValueRef BuildInternalCall(LLVMBuilderRef builder, LLVMTypeRef functionType, LLVMValueRef callee,
		TypeSymbol returnType, IEnumerable<LLVMValueRef> leading, IEnumerable<LLVMValueRef> args)
	{
		if (!IsLarge(returnType))
			return builder.BuildCall2(functionType, callee, [..leading, ..args]);
		
		var slot = BuildEntryAlloca(builder, MapTypeSymbol(returnType), "result");
		builder.BuildCall2(functionType, callee, [..leading, slot, ..args]);
		return slot;
	}
	
	private void EmitThunkReturn(LLVMValueRef thunk, TypeSymbol type, LLVMValueRef result, uint resultParameter,
		LLVMBuilderRef builder)
	{
		if (IsLarge(type))
		{
			StoreValue(builder, type, result, thunk.GetParam(resultParameter));
			builder.BuildRetVoid();
		}
		else if (MapTypeSymbol(type).Kind == LLVMTypeKind.LLVMVoidTypeKind)
		{
			builder.BuildRetVoid();
		}
		else
		{
			builder.BuildRet(result);
		}
	}
}
