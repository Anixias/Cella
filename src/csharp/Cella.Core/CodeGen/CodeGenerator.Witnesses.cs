using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Lowering;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private Witness? FindWitness(FunctionInfo function)
	{
		if (function.Symbol.Trait is null && function.Symbol.Syntax is not ConstructorConstraintNode ||
		    function.TypeArguments.IsDefaultOrEmpty || TypePool.ContainsTypeParameters(function.TypeArguments[0]))
			return null;
		
		var self = function.TypeArguments[0];
		var witness = function.Symbol.Trait is null
			? _typePool.FindConstructorWitness(self, function.Signature)
			: _typePool.FindWitness(self, function.Symbol, TypePool.GetTraitArguments(function));
		
		if (function.Symbol.Trait is not null && witness is null or FunctionWitness { Function.Trait: not null } &&
		    _typePool.FindNativeWitness(function.Symbol, function.Signature, self) is { } native)
			return native;
		
		return witness is FunctionWitness { Function: var target } && target == function.Symbol ? null : witness;
	}
	
	private LLVMValueRef BuildWitness(FunctionInfo function, Witness witness)
	{
		var definition = _typePool.GetGenericDefinition(function.Symbol);
		var name = Mangling.MangleInstantiation(definition.MangledBase ?? function.Symbol.Name, function.Signature,
			function.TypeArguments, _modules);
		
		var thunk = CreateFunction(current.Module, function, name);
		var value = thunk.FunctionValue;
		value.Linkage = LLVMLinkage.LLVMInternalLinkage;
		
		using var builder = CreateGeneratedBuilder(value, GetAdapterName(function), function.Symbol.Definition);
		var parameters = function.Signature.ParameterTypes.Select((_, i) => value.GetParam((uint)i)).ToList();
		var result = witness switch
		{
			FunctionWitness target => CallWitness(function, target, parameters, builder),
			NativeWitness native => EmitNativeWitness(function, native, parameters,
				function.Symbol.Syntax.SourceLocation, builder),
			MemberwiseWitness => EmitConstruction(function, parameters, true, builder),
			DefaultWitness => EmitConstruction(function, parameters, false, builder),
			ConversionWitness conversion => EmitConversionWitness(function, conversion.Conversion, parameters,
				function.Symbol.Syntax.SourceLocation, builder),
			SelfConversionWitness conversion => EmitSelfConversionWitness(function, conversion.Conversion, parameters,
				function.Symbol.Syntax.SourceLocation, builder),
			_ => throw new InvalidOperationException()
		};
		
		if (thunk.ResultParameter is { } resultParameter)
		{
			StoreValue(builder, function.Signature.ReturnType, result, value.GetParam(resultParameter));
			builder.BuildRetVoid();
		}
		else if (thunk.ReturnType.Kind == LLVMTypeKind.LLVMVoidTypeKind)
		{
			builder.BuildRetVoid();
		}
		else
		{
			builder.BuildRet(result);
		}
		
		return value;
	}
	
	private LLVMValueRef BuildNativeConstructor(FunctionInfo function, NativeConstructorNode native)
	{
		var value = CreateFunction(current.Module, function).FunctionValue;
		value.Linkage = LLVMLinkage.LLVMInternalLinkage;
		using var builder = CreateGeneratedBuilder(value, GetDisplayName(function), SourceLocation.None);
		var result = native.Intrinsic switch
		{
			NativeMemberIntrinsic.StrNew => builder.BuildInsertValue(
				builder.BuildInsertValue(MapTypeSymbol(NativeSymbols.Str).Undef, value.GetParam(2), StrLengthSlot,
					"str"), value.GetParam(1), StrDataSlot, "str"),
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
			declared, function.Symbol.Trait);
		
		var target = _typePool.InstantiateFunction(witness.Info, arguments);
		GetFunctionValue(target);
		var callee = current.Functions[target];
		var direct = function.Signature.HasVariadicParameter
			? parameters.Count
			: Math.Min(parameters.Count, target.Signature.FixedCount);
		
		var args = parameters
			.Take(direct)
			.Select((parameter, i) => PassToWitness(function, target, i, parameter, builder))
			.ToList();
		
		var cleanups = new List<Action>();
		if (!function.Signature.HasVariadicParameter)
			args.AddRange(EmitOmittedArguments(target.Signature, direct, parameters[direct..], cleanups, builder));
		
		var result = callee.CSignature is { } signature
			? EmitCCall(signature, callee.FunctionType, callee.FunctionValue, target.Signature.ParameterTypes,
				target.Signature.ReturnType, args, builder)
			: BuildInternalCall(builder, callee.FunctionType, callee.FunctionValue, target.Signature.ReturnType, [],
				args);
		
		foreach (var cleanup in cleanups)
			cleanup();
		
		return AdaptFunction(target.Signature.ReturnType, function.Signature.ReturnType, result, builder);
	}
	
	private LLVMValueRef PassToWitness(FunctionInfo function, FunctionInfo target, int index, LLVMValueRef value,
		LLVMBuilderRef builder)
	{
		var fromPointer = TypePool.PassesByPointer(function, index);
		var toPointer = TypePool.PassesByPointer(target, index);
		var from = function.Signature.ParameterTypes[index];
		var to = target.Signature.ParameterTypes[index];
		if (fromPointer && !toPointer)
			value = LoadValue(builder, from, value, "argument");
		
		value = AdaptFunction(from, to, value, builder);
		return !fromPointer && toPointer ? SpillValue(builder, to, value, "argument") : value;
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
			var value = Lowerer.LowerBinaryOperation(_typePool, place, operation, operands[1], location);
			
			EmitValue(new AssignValue(operation.ReturnType, place, value, location), builder);
			return default;
		}
		
		if (operands is not [var single])
			return EmitValue(Lowerer.LowerBinaryOperation(_typePool, operands[0], operation, operands[1], location),
				builder);
		
		Value operand = operation.ParameterTypes[0] is EnumSymbol enumType
			? new EnumTagValue(_typePool.GetTagType(enumType), single, location)
			: single;
		
		return EmitValue(new UnaryOpValue(operation.ReturnType, operand,
			OperationMapping.ToUnaryOperation(operation.Op), location), builder);
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
			Value value = new VariableValue(BindWitnessParameter(function, i + 1, parameters[i + 1], builder),
				location);
			
			if (value.Type is FunctionType source && fieldType is FunctionType target &&
			    FunctionType.CanAdapt(source, target))
				value = new ConversionValue(value, new ExternalFunctionConversion(source, target), location);
			
			var field = new AccessValue(fieldType, self, fields[i], location);
			EmitValue(new AssignValue(fieldType, field, value, location), builder);
		}
		
		return default;
	}
	
	private LLVMValueRef EmitConversionWitness(FunctionInfo function, Conversion conversion,
		List<LLVMValueRef> parameters, SourceLocation location, LLVMBuilderRef builder)
	{
		var type = function.TypeArguments[0];
		var self = new UnaryOpValue(type, new VariableValue(BindWitnessParameter(function, 0, parameters[0], builder),
			location), UnaryOperation.Dereference, location);
		
		var value = new VariableValue(BindWitnessParameter(function, 1, parameters[1], builder), location);
		Value converted = conversion is FunctionConversion { Function: var target }
			? new CallValue(target, [PassConversionSource(target, value, location)], location)
			: new ConversionValue(value, conversion, location);
		
		EmitValue(new AssignValue(type, self, converted, location), builder);
		if (function.DeclaredSignature.GetMode(1) == ParameterMode.Value &&
		    conversion is FunctionConversion { Function: var lending } &&
		    lending.DeclaredSignature.GetMode(0) != ParameterMode.Value)
			EmitDrop(value, builder);
		
		return default;
	}
	
	private LLVMValueRef EmitSelfConversionWitness(FunctionInfo function, Conversion conversion,
		List<LLVMValueRef> parameters, SourceLocation location, LLVMBuilderRef builder)
	{
		var source = new VariableValue(BindWitnessParameter(function, 0, parameters[0], builder), location);
		return EmitValue(new ConversionValue(source, conversion, location), builder);
	}
	
	private Value PassConversionSource(FunctionInfo conversion, VariableValue source, SourceLocation location)
	{
		return TypePool.PassesByPointer(conversion, 0)
			? new UnaryOpValue(_typePool.GetPointerType(source.Type), source, UnaryOperation.AddressOf, location)
			: source;
	}
	
	private VariableInfo BindWitnessParameter(FunctionInfo function, int index, LLVMValueRef value,
		LLVMBuilderRef builder)
	{
		var type = function.Signature.ParameterTypes[index];
		var info = new VariableInfo(
			new ParameterSymbol($"${index}", function.Symbol.Syntax.SourceLocation) { IsNamed = false }, type);
		
		if (TypePool.PassesByPointer(function, index))
		{
			current.Variables[info] = value;
			return info;
		}
		
		current.Variables[info] = SpillValue(builder, type, value, info.Symbol.Name);
		return info;
	}
	
	private LLVMValueRef Instantiate(FunctionInfo function)
	{
		var definition = _typePool.GetGenericDefinition(function.Symbol);
		var name = Mangling.MangleInstantiation(definition.MangledBase ?? function.Symbol.Name, function.Signature,
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
}