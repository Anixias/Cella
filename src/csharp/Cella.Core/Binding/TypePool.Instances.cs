using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Collections;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	private void Complete(TypeSymbol type)
	{
		if (type is NamedTypeSymbol { IsGenericInstance: true } instance)
			CompleteInstance(instance);
		else if (type is NamedTypeSymbol)
			TypeCompleter?.Invoke(type);
	}
	
	public void CompleteGenericInstance(TypeSymbol type)
	{
		if (type is not NamedTypeSymbol { IsGenericInstance: true } instance)
			return;
		
		InvalidateConformanceCache();
		CompleteInstance(instance);
	}
	
	public TypeSymbol Instantiate(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments)
	{
		if (typeArguments.Any(static argument => argument is InvalidType))
			return NativeSymbols.Invalid;
		
		if (typeArguments.SequenceEqual(definition.TypeParameters))
			return definition;
		
		var instances = _instances.GetOrAdd(definition);
		if (instances.Find(instance => instance.TypeArguments.SequenceEqual(typeArguments)) is { } existing)
			return existing;
		
		var statics = definition.StaticFields
			.Select(static field => new GlobalSymbol(field.Syntax, field.Visibility))
			.ToImmutableArray();
		
		var shown = CountShownArguments(definition.TypeParameters, typeArguments);
		NamedTypeSymbol created = definition switch
		{
			RecordSymbol record => new RecordSymbol(record, typeArguments, record.Members.Select(CopyMember), shown)
			{
				StaticFields = statics
			},
			EnumSymbol enumType => new EnumSymbol(enumType, typeArguments, enumType.Cases.Select(CopyCase), shown)
			{
				StaticFields = statics
			},
			_ => throw new InvalidOperationException()
		};
		
		foreach (var field in statics)
			field.ContainingType = created;
		
		instances.Add(created);
		return created;
	}
	
	private static MemberSymbol CopyMember(MemberSymbol member) =>
		member is FieldSymbol field ? CopyField(field) : member;
	
	private static EnumCaseSymbol CopyCase(EnumCaseSymbol enumCase) =>
		new(enumCase.Node, enumCase.Index, enumCase.Fields.Select(CopyField));
	
	private static FieldSymbol CopyField(FieldSymbol field) => new(field.Name, field.Node, field.IsMutable);
	
	private void CompleteInstance(NamedTypeSymbol instance)
	{
		if (!_completedInstances.Add(instance))
			return;
		
		var map = CreateMap(instance.Definition.TypeParameters, instance.TypeArguments);
		switch (instance)
		{
			case RecordSymbol record:
				CompleteRecordInstance(record, (RecordSymbol)record.Definition, map);
				break;
			
			case EnumSymbol enumType:
				CompleteEnumInstance(enumType, (EnumSymbol)enumType.Definition, map);
				break;
		}
	}
	
	private void CompleteRecordInstance(RecordSymbol instance, RecordSymbol definition,
		Dictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var fields = instance.Members
			.OfType<FieldSymbol>()
			.DistinctBy(static field => field.Name)
			.ToDictionary(static field => field.Name);
		
		foreach (var field in GetMembers(definition).OfType<FieldSymbol>())
			RegisterMember(instance, fields[field.Name], Substitute(GetTypeOfMember(field), map));
		
		RegisterRecord(instance);
		foreach (var constructor in GetBodyConstructors(definition))
			AddConstructor(instance, InstantiateFunction(constructor, instance.TypeArguments));
		
		if (GetDestructor(definition) is { } destructor)
			SetDestructor(instance, InstantiateFunction(destructor, instance.TypeArguments));
	}
	
	private void CompleteEnumInstance(EnumSymbol instance, EnumSymbol definition,
		Dictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		for (var i = 0; i < definition.Cases.Length; i++)
		{
			var payloadTypes = GetPayloadTypes(definition, definition.Cases[i]);
			for (var j = 0; j < payloadTypes.Length; j++)
				RegisterPayloadField(instance.Cases[i].Fields[j], Substitute(payloadTypes[j], map));
		}
		
		if (definition.IsMatch)
		{
			RegisterMatchEnum(instance, Substitute(GetMatchedType(definition), map),
				[..definition.Cases.Select(enumCase => GetMatchValues(definition, enumCase))]);
			
			return;
		}
		
		RegisterEnum(instance, GetTagType(definition),
			[..definition.Cases.Select(enumCase => GetCaseValue(definition, enumCase))]);
		
		if (!instance.HasPayload)
			RegisterEnumConversions(instance);
	}
	
	public TypeSymbol? GetDefaultArgument(TypeParameterSymbol parameter) =>
		parameter.DefaultArgument ?? (parameter.DeclaresDefault ? DefaultResolver?.Invoke(parameter) : null);
	
	public int CountShownArguments(ImmutableArray<TypeParameterSymbol> parameters,
		ImmutableArray<TypeSymbol> arguments)
	{
		var map = CreateMap(parameters, arguments);
		var shown = arguments.Length;
		while (shown > 0 && shown <= parameters.Length && IsHiddenArgument(parameters, arguments, shown - 1, map))
			shown--;
		
		return shown;
	}
	
	private bool IsHiddenArgument(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TypeSymbol> arguments,
		int index, Dictionary<TypeParameterSymbol, TypeSymbol> map) =>
		GetDefaultArgument(parameters[index]) is { } fallback
			? Substitute(fallback, map) == arguments[index]
			: InferDerived(parameters, [..arguments.Take(index)]) == arguments[index];
	
	private TypeSymbol? InferDerived(ImmutableArray<TypeParameterSymbol> parameters, IReadOnlyList<TypeSymbol> known)
	{
		if (!parameters[known.Count].IsDerived)
			return null;
		
		var inferred = new TypeSymbol?[parameters.Length];
		for (var i = 0; i < known.Count; i++)
			inferred[i] = known[i];
		
		new TypeInference(this).InferFromBounds(parameters, inferred);
		return inferred[known.Count];
	}
	
	public static int CountRequiredArguments(ImmutableArray<TypeParameterSymbol> parameters) =>
		parameters.Count(static parameter => parameter is { DeclaresDefault: false, IsDerived: false });
	
	public ImmutableArray<TypeSymbol>? CompleteArguments(ImmutableArray<TypeParameterSymbol> parameters,
		IReadOnlyList<TypeSymbol> arguments, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? outer = null) =>
		CompleteArguments(parameters, arguments, outer, out _);
	
	public ImmutableArray<TypeSymbol>? CompleteArguments(ImmutableArray<TypeParameterSymbol> parameters,
		IReadOnlyList<TypeSymbol> arguments, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? outer,
		out TypeParameterSymbol? uninferred)
	{
		uninferred = null;
		if (arguments.Count > parameters.Length)
			return null;
		
		var completed = arguments.ToList();
		while (completed.Count < parameters.Length)
		{
			if ((FindPinnedArgument(parameters[completed.Count], outer) ?? InferDerived(parameters, completed)) is
			    { } inferred)
			{
				completed.Add(inferred);
				continue;
			}
			
			if (GetDefaultArgument(parameters[completed.Count]) is not { } fallback)
			{
				uninferred = parameters[completed.Count] is { IsDerived: true } derived &&
				             arguments.Count >= CountRequiredArguments(parameters)
					? derived
					: null;
				
				return null;
			}
			
			var map = CreateMap(parameters, completed);
			foreach (var (parameter, argument) in outer ?? new Dictionary<TypeParameterSymbol, TypeSymbol>())
				map[parameter] = argument;
			
			completed.Add(Substitute(fallback, map));
		}
		
		return [..completed];
	}
	
	public TypeSymbol? FindDefaultArgument(ImmutableArray<TypeParameterSymbol> parameters,
		IReadOnlyList<TypeSymbol?> arguments, int index)
	{
		if (GetDefaultArgument(parameters[index]) is not { } fallback)
			return null;
		
		var map = parameters.Take(index).Select((parameter, i) => (parameter, arguments[i] ?? NativeSymbols.Invalid))
			.ToDictionary(static pair => pair.parameter, static pair => pair.Item2);
		
		var argument = Substitute(fallback, map);
		return ContainsInvalid(argument) ? null : argument;
	}
	
	public static Dictionary<TypeParameterSymbol, TypeSymbol> CreateMap(
		IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<TypeSymbol> arguments) =>
		parameters.Zip(arguments).ToDictionary(static pair => pair.First, static pair => pair.Second);
	
	public static bool ContainsInvalid(TypeSymbol type) => type switch
	{
		InvalidType => true,
		NamedTypeSymbol named => named.TypeArguments.Any(ContainsInvalid),
		PointerType pointer => ContainsInvalid(pointer.BaseType),
		BorrowType borrow => ContainsInvalid(borrow.Target),
		ArrayType array => ContainsInvalid(array.ElementType),
		FStrType fstr => ContainsInvalid(fstr.Value),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(ContainsInvalid),
		_ => false
	};
	
	public static IEnumerable<TypeParameterSymbol> FindTypeParameters(TypeSymbol type) => type switch
	{
		TypeParameterSymbol parameter => [parameter],
		DynType { Parameter: { } parameter } => [parameter],
		DynType { Instance: { } trait } => FindTypeParameters(trait),
		DynType { Function: { } function } => FindTypeParameters(function),
		TraitType trait => trait.Arguments.SelectMany(FindTypeParameters),
		FStrType fstr => FindTypeParameters(fstr.Value),
		NamedTypeSymbol named => named.TypeArguments.SelectMany(FindTypeParameters),
		PointerType pointer => FindTypeParameters(pointer.BaseType),
		BorrowType borrow => FindTypeParameters(borrow.Target),
		ArrayType { LengthParameter: { } length } array => FindTypeParameters(array.ElementType).Append(length),
		ArrayType array => FindTypeParameters(array.ElementType),
		FunctionType { Lambda: { } lambda } function => function.ParameterTypes.Append(function.ReturnType)
			.Concat(GetLambdaArguments(lambda))
			.SelectMany(FindTypeParameters)
			.Distinct(),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).SelectMany(FindTypeParameters),
		ClosureType closure => closure.CaptureTypes
			.SelectMany(FindTypeParameters)
			.Concat(FindTypeParameters(closure.Signature))
			.Concat(GetLambdaArguments(closure.Function).SelectMany(FindTypeParameters))
			.Distinct(),
		_ => []
	};
	
	private static IEnumerable<TypeSymbol> GetLambdaArguments(FunctionInfo function) =>
		function.TypeArguments.IsDefaultOrEmpty ? function.Symbol.TypeParameters : function.TypeArguments;
	
	public static bool ContainsTypeParameters(TypeSymbol type) => type switch
	{
		TypeParameterSymbol or DynType { Parameter: not null } => true,
		DynType { Instance: { } trait } => ContainsTypeParameters(trait),
		DynType { Function: { } function } => ContainsTypeParameters(function),
		TraitType trait => trait.Arguments.Any(ContainsTypeParameters),
		FStrType fstr => ContainsTypeParameters(fstr.Value),
		NamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameters),
		PointerType pointer => ContainsTypeParameters(pointer.BaseType),
		BorrowType borrow => ContainsTypeParameters(borrow.Target),
		ArrayType array => array.LengthParameter is not null || ContainsTypeParameters(array.ElementType),
		FunctionType { Lambda: { } lambda } function => function.ParameterTypes.Append(function.ReturnType)
			.Concat(GetLambdaArguments(lambda))
			.Any(ContainsTypeParameters),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(ContainsTypeParameters),
		ClosureType closure => closure.CaptureTypes.Any(ContainsTypeParameters) ||
		                       ContainsTypeParameters(closure.Signature) ||
		                       GetLambdaArguments(closure.Function).Any(ContainsTypeParameters),
		_ => false
	};
	
	public TypeSymbol Substitute(TypeSymbol type, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		map.Count == 0 || !ContainsTypeParameters(type)
			? type
			: type switch
			{
				TypeParameterSymbol parameter => SubstituteParameter(parameter, map),
				DynType { Parameter: { } parameter } => map.GetValueOrDefault(parameter) switch
				{
					TraitType argument => GetDynType(argument),
					TypeParameterSymbol argument => GetDynType(argument),
					FunctionType argument => GetDynType(argument),
					InvalidType argument => argument,
					_ => type
				},
				DynType { Instance: { } trait } => GetDynType(SubstituteTrait(trait, map)),
				DynType { Function: { } function } => GetDynType(SubstituteFunctionType(function, map)),
				TraitType trait => SubstituteTrait(trait, map),
				FStrType fstr => Substitute(fstr.Value, map) switch
				{
					DynType dyn => GetFStrType(dyn),
					var substituted => substituted
				},
				NamedTypeSymbol named => Instantiate(named.Definition,
					[..named.TypeArguments.Select(argument => Substitute(argument, map))]),
				PointerType pointer => GetPointerType(Substitute(pointer.BaseType, map)),
				BorrowType borrow => GetBorrowType(Substitute(borrow.Target, map), borrow.IsMutable),
				ArrayType { LengthParameter: { } length } array =>
					GetArrayType(Substitute(array.ElementType, map), SubstituteParameter(length, map)),
				ArrayType array => GetArrayType(Substitute(array.ElementType, map), array.Length),
				FunctionType { Lambda: { } lambda, Shared: { } shared, LambdaName: { } name } => GetLambdaType(
					SubstituteFunctionType(shared, map), SubstituteLambda(lambda, map), name),
				FunctionType function => SubstituteFunctionType(function, map),
				ClosureType closure => SubstituteClosure(closure, map),
				_ => type
			};
	
	private ClosureType SubstituteClosure(ClosureType closure, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		return GetClosureType(SubstituteLambda(closure.Function, map),
			[..closure.CaptureTypes.Select(type => Substitute(type, map))],
			SubstituteFunctionType(closure.Signature, map), closure.DefinitionName);
	}
	
	private FunctionInfo SubstituteLambda(FunctionInfo function,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		function.Symbol.TypeParameters.IsEmpty
			? function
			: InstantiateFunction(function,
				[..GetLambdaArguments(function).Select(argument => Substitute(argument, map))]);
	
	public bool TryUnify(TypeSymbol first, TypeSymbol second, OrderedSet<TypeParameterSymbol> variables,
		Dictionary<TypeParameterSymbol, TypeSymbol> bindings)
	{
		first = Substitute(first, bindings);
		second = Substitute(second, bindings);
		if (first == second)
			return true;
		
		if (first is TypeParameterSymbol variable && variables.Contains(variable))
			return second is TypeParameterSymbol later && variables.IndexOf(later) > variables.IndexOf(variable)
				? Bind(later, first, bindings)
				: Bind(variable, second, bindings);
		
		if (second is TypeParameterSymbol other && variables.Contains(other))
			return Bind(other, first, bindings);
		
		return (first, second) switch
		{
			(NamedTypeSymbol a, NamedTypeSymbol b) => a.Definition == b.Definition &&
			                                          TryUnify(a.TypeArguments, b.TypeArguments, variables, bindings),
			(PointerType a, PointerType b) => TryUnify(a.BaseType, b.BaseType, variables, bindings),
			(BorrowType a, BorrowType b) => a.IsMutable == b.IsMutable &&
			                                TryUnify(a.Target, b.Target, variables, bindings),
			(ArrayType a, ArrayType b) => TryUnify(GetLengthArgument(a), GetLengthArgument(b), variables, bindings) &&
			                              TryUnify(a.ElementType, b.ElementType, variables, bindings),
			(FunctionType a, FunctionType b) => a.IsExternal == b.IsExternal && a.IsRef == b.IsRef &&
			                                    a.IsVariadic == b.IsVariadic &&
			                                    a.HasVariadicParameter == b.HasVariadicParameter &&
			                                    a.ParameterModes.SequenceEqual(b.ParameterModes) &&
			                                    TryUnify([..a.ParameterTypes, a.ReturnType],
				                                    [..b.ParameterTypes, b.ReturnType], variables, bindings),
			(TraitType a, TraitType b) => a.Trait == b.Trait && TryUnify(a.Arguments, b.Arguments, variables, bindings),
			(DynType { Instance: { } a }, DynType { Instance: { } b }) => TryUnify(a, b, variables, bindings),
			(DynType { Function: { } a }, DynType { Function: { } b }) => TryUnify(a, b, variables, bindings),
			(FStrType a, FStrType b) => TryUnify(a.Value, b.Value, variables, bindings),
			_ => false
		};
	}
	
	public FunctionType GetPlainFunctionType(FunctionType function) =>
		GetFunctionType(function.IsExternal, function.ParameterTypes, function.ParameterModes, function.ReturnType,
			false, function.IsVariadic, function.HasVariadicParameter);
	
	public FunctionType GetRefFunctionType(FunctionType function) =>
		GetFunctionType(false, function.ParameterTypes, function.ParameterModes, function.ReturnType, true,
			function.IsVariadic, function.HasVariadicParameter);
	
	private FunctionType SubstituteFunctionType(FunctionType function,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var (types, modes) = SubstituteParameters(function.ParameterTypes, function.ParameterModes, map);
		return GetFunctionType(function.IsExternal, types, modes, Substitute(function.ReturnType, map), function.IsRef,
			function.IsVariadic, function.HasVariadicParameter);
	}
	
	private (TypeSymbol[] Types, ParameterMode[] Modes) SubstituteParameters(ImmutableArray<TypeSymbol> types,
		IReadOnlyList<ParameterMode> modes, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var substitutedTypes = new TypeSymbol[types.Length];
		var substitutedModes = new ParameterMode[types.Length];
		for (var i = 0; i < types.Length; i++)
		{
			var mode = i < modes.Count ? modes[i] : ParameterMode.Value;
			var type = Substitute(types[i], map);
			if (mode == ParameterMode.Imm && types[i] is TypeParameterSymbol)
				type = GetPassedType(type, mode);
			
			substitutedTypes[i] = type;
			substitutedModes[i] = mode;
		}
		
		return (substitutedTypes, substitutedModes);
	}
	
	private bool TryUnify(ImmutableArray<TypeSymbol> first, ImmutableArray<TypeSymbol> second,
		OrderedSet<TypeParameterSymbol> variables, Dictionary<TypeParameterSymbol, TypeSymbol> bindings) =>
		first.Length == second.Length &&
		first.Zip(second).All(pair => TryUnify(pair.First, pair.Second, variables, bindings));
	
	private bool Bind(TypeParameterSymbol variable, TypeSymbol type,
		Dictionary<TypeParameterSymbol, TypeSymbol> bindings)
	{
		if (FindTypeParameters(type).Contains(variable))
			return false;
		
		var binding = new Dictionary<TypeParameterSymbol, TypeSymbol> { [variable] = type };
		foreach (var key in bindings.Keys.ToList())
			bindings[key] = Substitute(bindings[key], binding);
		
		bindings[variable] = type;
		return true;
	}
	
	public FunctionSignature SubstituteSignature(FunctionSignature signature,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var (types, modes) = SubstituteParameters(signature.ParameterTypes, signature.ParameterModes, map);
		return new(types, Substitute(signature.ReturnType, map), signature.IsVariadic, modes,
			signature.HasVariadicParameter, signature.Defaults);
	}
	
	public FunctionInfo InstantiateFunction(FunctionInfo function, ImmutableArray<TypeSymbol> typeArguments)
	{
		var parameters = function.Symbol.TypeParameters;
		if (parameters.IsEmpty || typeArguments.SequenceEqual(parameters))
			return function.TypeArguments.IsDefaultOrEmpty ? function : GetGenericDefinition(function.Symbol);
		
		var definition = function.TypeArguments.IsDefaultOrEmpty ? function : GetGenericDefinition(function.Symbol);
		_genericFunctions[definition.Symbol] = definition;
		var instances = _functionInstances.GetOrAdd(definition.Symbol);
		var index = instances.FindIndex(instance => instance.TypeArguments.SequenceEqual(typeArguments));
		if (index >= 0)
			return instances[index];
		
		var map = CreateMap(parameters, typeArguments);
		var signature = definition.Signature;
		var instantiated = definition with
		{
			MangledName = null,
			MangledBase = null,
			Signature = SubstituteSignature(signature, map),
			TypeArguments = typeArguments,
			Declared = signature
		};
		
		instances.Add(instantiated);
		return instantiated;
	}
	
	public FunctionInfo GetGenericDefinition(FunctionSymbol function) => _genericFunctions[function];
	
	public FunctionInfo SubstituteFunction(FunctionInfo function,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		if (function.Symbol.TypeParameters.IsEmpty)
			return function;
		
		IEnumerable<TypeSymbol> arguments = function.TypeArguments.IsDefaultOrEmpty
			? function.Symbol.TypeParameters
			: function.TypeArguments;
		
		return InstantiateFunction(function, [..arguments.Select(argument => Substitute(argument, map))]);
	}
	
	public Constant SubstituteConstant(Constant constant, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		map.Count == 0
			? constant
			: constant switch
			{
				FunctionConstant c => new FunctionConstant(SubstituteFunction(c.Function, map),
					Substitute(c.Type, map)),
				RecordConstant c => new RecordConstant(Substitute(c.Type, map),
					c.Fields.Select(field => SubstituteConstant(field, map))),
				ArrayConstant c => new ArrayConstant((ArrayType)Substitute(c.ArrayType, map),
					c.Elements.Select(element => SubstituteConstant(element, map))),
				EnumConstant c => new EnumConstant(Substitute(c.Type, map), c.Case,
					c.Payload.Select(value => SubstituteConstant(value, map))),
				IntegerConstant c => new IntegerConstant(Substitute(c.Type, map), c.Value),
				FloatConstant c => new FloatConstant(Substitute(c.Type, map), c.Value),
				NullConstant c => new NullConstant(Substitute(c.Type, map)),
				StringConstant c => new StringConstant(Substitute(c.Type, map), c.Value),
				EnumTagConstant c => new EnumTagConstant(Substitute(c.Type, map), c.Tag),
				ZeroConstant c => new ZeroConstant(Substitute(c.Type, map)),
				_ => constant
			};
	
	public Func<IResolvedExpressionNode, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>, Constant?>?
		FoldParametric { get; set; }
	
	public Func<IResolvedExpressionNode, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>, StaticFault?>?
		FindParametricFault { get; set; }
	
	public void RegisterGenericGlobal(GlobalInfo definition) => _genericGlobals[definition.Symbol] = definition;
	
	public GlobalInfo InstantiateGlobal(GlobalSymbol global, ModuleIndex? modules)
	{
		if (_globalInstances.TryGetValue(global, out var existing))
			return existing;
		
		var instance = (NamedTypeSymbol)global.ContainingType!;
		var definition = _genericGlobals[instance.Definition.GetStaticField(global.Name)!];
		var map = CreateMap(instance.Definition.TypeParameters, instance.TypeArguments);
		var type = Substitute(definition.Type, map);
		var folded = definition.Value is ParametricConstant parametric
			? FoldParametric?.Invoke(parametric.Value, map) is { } result and not InvalidConstant ? result : null
			: null;
		
		var instantiated = definition with
		{
			MangledName = Mangling.MangleInstantiation(definition.MangledName, instance.TypeArguments, modules),
			Symbol = global,
			Type = type,
			Value = definition.Value switch
			{
				ParametricConstant => folded ?? new ZeroConstant(type),
				{ } value => SubstituteConstant(value, map),
				null => null
			},
			Fault = definition.Value is ParametricConstant { Value: var expression } && folded is null
				? FindParametricFault?.Invoke(expression, map) ??
				  new StaticFault("constant overflow", expression.Syntax.SourceLocation)
				: null
		};
		
		_globalInstances[global] = instantiated;
		return instantiated;
	}
	
	public bool TryGetTypeOfMember(MemberSymbol member, [NotNullWhen(true)] out TypeSymbol? type)
	{
		if (member is TypedMemberSymbol m)
			return _memberTypes.TryGetValue(m, out type);
		
		type = null;
		return false;
	}
	
	public TypeSymbol GetTypeOfMember(TypedMemberSymbol member) => _memberTypes[member];
}