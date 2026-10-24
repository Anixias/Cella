using System.Collections.Immutable;
using Cella.Core.Binding.Operations;
using Cella.Core.Collections;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public void CacheConformances(bool isEnabled)
	{
		conformanceCache = isEnabled ? new() : null;
		memberBlockCache = isEnabled ? new() : null;
		ConformanceVersion++;
	}
	
	public bool CachesConformances => conformanceCache is not null;
	
	public int ConformanceVersion { get; private set; }
	
	private void InvalidateConformanceCache()
	{
		conformanceCache?.Clear();
		memberBlockCache?.Clear();
		_conformanceInstances.Clear();
		ConformanceVersion++;
	}
	
	public bool IsSettled => _blanketBoundsInProgress.Count == 0 && _blanketLookupsInProgress.Count == 0 &&
	                         _instancesInProgress.Count == 0;
	
	private IReadOnlyList<T> Memoize<T>(Dictionary<TypeSymbol, IReadOnlyList<T>>? cache, TypeSymbol type,
		Func<TypeSymbol, IEnumerable<T>> compute)
	{
		if (cache is null)
			return [..compute(type)];
		
		if (cache.TryGetValue(type, out var cached))
			return cached;
		
		var isSettled = IsSettled;
		IReadOnlyList<T> computed = [..compute(type)];
		if (isSettled && IsSettled)
			cache[type] = computed;
		
		return computed;
	}
	
	public void AddConformance(Conformance conformance)
	{
		InvalidateConformanceCache();
		_conformances.Add(conformance);
		if (conformance.Target is TypeParameterSymbol)
			_blanketConformances.Add(conformance);
		else if (conformance.Target is ArrayType)
			_arrayConformances.Add(conformance);
		else if (IsTypedPointer(conformance.Target))
			_pointerConformances.Add(conformance);
		else
			_conformancesByTarget.GetOrAdd(conformance.Target).Add(conformance);
	}
	
	private IEnumerable<Conformance> GetConformanceCandidates(TypeSymbol type) =>
		type is TypeParameterSymbol ? [] : GetExplicitCandidates(type).Concat(GetBlanketCandidates(type));
	
	private IEnumerable<Conformance> GetExplicitCandidates(TypeSymbol type) => Expand(type switch
	{
		ArrayType => _arrayConformances,
		_ when IsTypedPointer(type) => _pointerConformances,
		_ => _conformancesByTarget.GetValueOrDefault(type.OriginalDefinition) ?? []
	}, type);
	
	private IEnumerable<Conformance> GetBlanketCandidates(TypeSymbol type) => Expand(_blanketConformances, type);
	
	private IEnumerable<Conformance> Expand(IEnumerable<Conformance> conformances, TypeSymbol type) =>
		conformances.SelectMany(conformance =>
			conformance.IsTemplate ? GetInstances(conformance, type) : [conformance]);
	
	private IReadOnlyList<Conformance> GetInstances(Conformance template, TypeSymbol type)
	{
		if (type is TypeParameterSymbol)
			return [];
		
		if (_conformanceInstances.TryGetValue((template, type), out var cached))
			return cached;
		
		if (!_instancesInProgress.Add((template, type)))
			return [];
		
		IReadOnlyList<Conformance> instances;
		try
		{
			instances = [..CreateInstances(template, type)];
		}
		finally
		{
			_instancesInProgress.Remove((template, type));
		}
		
		if (IsSettled)
			_conformanceInstances[(template, type)] = instances;
		
		return instances;
	}
	
	private IEnumerable<Conformance> CreateInstances(Conformance template, TypeSymbol type)
	{
		var impl = template.Impl!;
		ImmutableArray<TypeSymbol>? arguments = template.Target is TypeParameterSymbol
			? [type]
			: HasShape(template.Target, type)
				? BindBlock(impl, GetShapeArguments(type))
				: null;
		
		if (arguments is not { } bound)
			return [];
		
		return FindFreeBindings(impl, bound)
			.Select(binding => impl.FreeParameters.Select(parameter => binding.GetValueOrDefault(parameter)).ToList())
			.Where(values => values.All(static value => value is not null))
			.Select(static values => values.Select(static value => value!).ToImmutableArray())
			.DistinctBy(static values => string.Join(",", values.Select(static value => value.Name)))
			.Select(values => new Conformance(template.Trait, template.Arguments, template.Target,
				[..template.Parameters, ..impl.FreeParameters], impl, template.Location)
			{
				Template = template,
				FreeArguments = values,
				IsImplied = template.IsImplied
			});
	}
	
	public IEnumerable<Conformance> FindConformances(TypeSymbol type) => type is TypeParameterSymbol
		? []
		: Memoize(conformanceCache, type, ComputeConformances);
	
	private IEnumerable<Conformance> ComputeConformances(TypeSymbol type)
	{
		var explicitOnes = GetExplicitCandidates(type).Where(conformance => Matches(conformance, type)).ToList();
		var blankets = GetBlanketCandidates(type).Where(blanket => Matches(blanket, type)).ToList();
		return explicitOnes.Concat(blankets.Where(blanket =>
			!explicitOnes.Any(conformance => Overrides(conformance, blanket, type)) &&
			!blankets.Any(other => Overrides(other, blanket, type) && IsMoreConstrained(other, blanket))));
	}
	
	public bool IsMoreConstrained(Conformance blanket, Conformance other) =>
		blanket.Parameters is [var parameter] && other.Parameters is [var otherParameter] &&
		Satisfies(other.Parameters, [parameter]) && !Satisfies(blanket.Parameters, [otherParameter]);
	
	private bool Overrides(Conformance conformance, Conformance blanket, TypeSymbol type) =>
		conformance.Trait == blanket.Trait &&
		GetConformanceArguments(conformance, type).SequenceEqual(GetConformanceArguments(blanket, type));
	
	private IEnumerable<TraitType> GetBlanketTraits(TypeParameterSymbol parameter)
	{
		if (_blanketConformances.Count == 0 || parameter.IsValue || parameter.IsTrait ||
		    !_blanketBoundsInProgress.Add(parameter))
			return [];
		
		try
		{
			return _blanketConformances
				.Where(blanket => Satisfies(blanket.Parameters, [parameter]))
				.Select(blanket => GetTraitType(blanket.Trait, GetConformanceArguments(blanket, parameter)))
				.ToList();
		}
		finally
		{
			_blanketBoundsInProgress.Remove(parameter);
		}
	}
	
	public TypeSymbol GetTypeConstantType(TypeConstantUse use)
	{
		return GlobalTypeOf?.Invoke(use.Constant) is { } declared
			? Substitute(declared, GetTypeConstantMap(use))
			: NativeSymbols.Invalid;
	}
	
	public TypeParameterSymbol GetConstantProjection(TypeConstantUse use)
	{
		if (_constantProjections.TryGetValue(use, out var existing))
			return existing;
		
		var projection = new TypeParameterSymbol($"{use.Owner.Name}.{use.Constant.Name}", [])
		{
			IsValue = true,
			ValueType = GetTypeConstantType(use),
			ConstantOwner = use.Owner,
			ConstantTrait = use.Trait,
			Constant = use.Constant
		};
		
		_constantProjections[use] = projection;
		return projection;
	}
	
	public TypeSymbol SubstituteParameter(TypeParameterSymbol parameter,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		if (parameter is not { ConstantOwner: { } owner, ConstantTrait: var trait, Constant: { } constant })
			return map.GetValueOrDefault(parameter, parameter);
		
		var use = new TypeConstantUse(owner, trait, constant);
		var substituted = Substitute(use, map);
		if (ContainsTypeParameters(substituted))
			return substituted == use ? parameter : GetConstantProjection(substituted);
		
		return TypeConstantValue?.Invoke(substituted) is Constants.IntegerConstant { Value: var value }
			? GetValueArgument(value)
			: NativeSymbols.Invalid;
	}
	
	public Dictionary<TypeParameterSymbol, TypeSymbol> GetConformanceMap(Conformance conformance, TypeSymbol type) =>
		CreateMap(conformance.Parameters, GetTargetArguments(conformance, type));
	
	private ImmutableArray<TypeSymbol> GetTargetArguments(Conformance conformance, TypeSymbol type) =>
	[
		..conformance.Target is TypeParameterSymbol ? [type] : BindArguments(conformance.Impl, GetShapeArguments(type)),
		..conformance.FreeArguments
	];
	
	private ImmutableArray<TypeSymbol> BindArguments(ImplSymbol? impl, ImmutableArray<TypeSymbol> arguments) =>
		impl is not null && BindBlock(impl, arguments) is { } bound ? bound : arguments;
	
	public void SetBounds(TypeParameterSymbol parameter, ImmutableArray<TraitType> traits)
	{
		InvalidateConformanceCache();
		_bounds[parameter] = traits;
	}
	
	public ImmutableArray<TraitType> GetDeclaredBounds(TypeParameterSymbol parameter) =>
		_bounds.GetValueOrDefault(parameter, []);
	
	public ImmutableArray<TraitType> GetBounds(TypeParameterSymbol parameter) =>
		AddRequiredTraits([..GetDeclaredBounds(parameter).Concat(GetBlanketTraits(parameter)).Distinct()], parameter);
	
	public void SetParameterBounds(TypeParameterSymbol parameter, ImmutableArray<TypeParameterSymbol> traits)
	{
		InvalidateConformanceCache();
		_parameterBounds[parameter] = traits;
	}
	
	public void SetFunctionBounds(TypeParameterSymbol parameter, ImmutableArray<FunctionType> functions)
	{
		InvalidateConformanceCache();
		_functionBounds[parameter] = functions;
	}
	
	public ImmutableArray<FunctionType> GetFunctionBounds(TypeParameterSymbol parameter) =>
		_functionBounds.GetValueOrDefault(parameter, []);
	
	public ImmutableArray<FunctionType> GetCallableSignatures(TypeSymbol type) => type switch
	{
		ClosureType closure => [closure.Signature],
		FunctionType function => [function],
		TypeParameterSymbol parameter => GetFunctionBounds(parameter),
		DynType { Function: { } function } => [function],
		DynType { Instance: { } trait } => GetDynCalls(trait),
		_ => []
	};
	
	public bool IsCallableAs(TypeSymbol type, FunctionType function) => type is InvalidType ||
	                                                                    GetCallableSignatures(type).Any(signature =>
		                                                                    FunctionType.SignatureStandsIn(signature,
			                                                                    function) ||
		                                                                    type is FunctionType or ClosureType &&
		                                                                    CopiesForImm(signature, function));
	
	public ImmutableArray<TypeParameterSymbol> GetParameterBounds(TypeParameterSymbol parameter) =>
		_parameterBounds.GetValueOrDefault(parameter, []);
	
	public void SetConstructorBounds(TypeParameterSymbol parameter, ImmutableArray<FunctionInfo> constructors)
	{
		InvalidateConformanceCache();
		_constructorBounds[parameter] = constructors;
	}
	
	public ImmutableArray<FunctionInfo> GetConstructorBounds(TypeParameterSymbol parameter) =>
		_constructorBounds.GetValueOrDefault(parameter, []);
	
	public void SetRequiredTraits(TraitSymbol trait, ImmutableArray<TraitType> required)
	{
		InvalidateConformanceCache();
		_requiredTraits[trait] = required;
	}
	
	public ImmutableArray<TraitType> GetRequiredTraits(TraitSymbol trait) =>
		_requiredTraits.GetValueOrDefault(trait, []);
	
	public IEnumerable<TraitType> GetRequiredTraits(TraitType trait, TypeSymbol self)
	{
		var map = CreateTraitMap(trait, self);
		return GetRequiredTraits(trait.Trait)
			.Select(required => SubstituteTrait(required, map))
			.Concat(GetParameterBounds(trait.Trait.Self).Select(bound => map.GetValueOrDefault(bound))
				.OfType<TraitType>());
	}
	
	private ImmutableArray<TraitType> AddRequiredTraits(ImmutableArray<TraitType> traits, TypeSymbol self)
	{
		if (traits.All(trait => GetRequiredTraits(trait.Trait).IsEmpty && GetParameterBounds(trait.Trait.Self).IsEmpty))
			return traits;
		
		var result = new OrderedSet<TraitType>();
		var pending = new Queue<TraitType>(traits);
		while (pending.TryDequeue(out var trait))
		{
			if (!result.Add(trait))
				continue;
			
			foreach (var required in GetRequiredTraits(trait, self))
				pending.Enqueue(required);
		}
		
		return [..result];
	}
	
	public ImmutableArray<TraitType> GetDynRequirements(TraitSymbol trait) =>
		[..GetRequiredTraits(trait).Where(required => !FindTypeParameters(required).Contains(trait.Self))];
	
	public int CountDynRequirements(TraitSymbol trait) =>
		GetDynRequirements(trait).Length + GetParameterBounds(trait.Self).Length;
	
	public ImmutableArray<TraitType?> GetDynRequirements(TraitType trait)
	{
		var map = CreateMap(trait.Trait.TypeParameters, trait.Arguments);
		return
		[
			..GetDynRequirements(trait.Trait).Select(required => SubstituteTrait(required, map)),
			..GetParameterBounds(trait.Trait.Self).Select(bound => map.GetValueOrDefault(bound) as TraitType)
		];
	}
	
	public ImmutableArray<FunctionType> GetDynCalls(TraitSymbol trait) =>
		[..GetFunctionBounds(trait.Self).Where(function => !FindTypeParameters(function).Contains(trait.Self))];
	
	public ImmutableArray<FunctionType> GetDynCalls(TraitType trait)
	{
		var map = CreateMap(trait.Trait.TypeParameters, trait.Arguments);
		return [..GetDynCalls(trait.Trait).Select(function => SubstituteFunctionType(function, map))];
	}
	
	public IEnumerable<TraitType> GetDynTraits(TraitType trait) =>
		WalkDynTraits(trait).Select(static entry => entry.Trait);
	
	public ImmutableArray<TraitType>? FindDynPath(TraitType from, TraitType to) => WalkDynTraits(from)
		.Where(entry => entry.Trait == to)
		.Select(static entry => (ImmutableArray<TraitType>?)entry.Path)
		.FirstOrDefault();
	
	private IEnumerable<(TraitType Trait, ImmutableArray<TraitType> Path)> WalkDynTraits(TraitType trait)
	{
		var visited = new HashSet<TraitType> { trait };
		var pending = new Queue<(TraitType Trait, ImmutableArray<TraitType> Path)>([(trait, [])]);
		while (pending.TryDequeue(out var entry))
		{
			yield return entry;
			foreach (var required in GetDynRequirements(entry.Trait).OfType<TraitType>())
			{
				if (visited.Add(required))
					pending.Enqueue((required, [..entry.Path, required]));
			}
		}
	}
	
	public bool Conforms(TypeSymbol type, TraitType trait) => type switch
	{
		InvalidType => true,
		_ when trait.Arguments.Any(static argument => argument is InvalidType) => true,
		TypeParameterSymbol parameter => GetBounds(parameter).Contains(trait),
		_ => FindConformance(type, trait) is not null
	};
	
	public bool Conforms(TypeSymbol type, TypeParameterSymbol trait) => type switch
	{
		InvalidType => true,
		TypeParameterSymbol parameter => GetParameterBounds(parameter).Contains(trait),
		_ => false
	};
	
	public bool Conforms(TypeSymbol type, DynType dyn) => dyn switch
	{
		{ Instance: { } trait } => Conforms(type, trait),
		{ Function: { } function } => IsCallableAs(type, function),
		_ => Conforms(type, dyn.Parameter!)
	};
	
	public Conformance? FindConformance(TypeSymbol type, TraitType trait)
	{
		if (type is TypeParameterSymbol)
			return null;
		
		if (GetExplicitCandidates(type).FirstOrDefault(conformance => Provides(conformance, type, trait)) is { } found)
			return found;
		
		if (!_blanketLookupsInProgress.Add((type, trait.Trait)))
			return null;
		
		try
		{
			var blankets = GetBlanketCandidates(type).Where(blanket => Provides(blanket, type, trait)).ToList();
			return blankets.FirstOrDefault(blanket => !blankets.Any(other => IsMoreConstrained(other, blanket)));
		}
		finally
		{
			_blanketLookupsInProgress.Remove((type, trait.Trait));
		}
	}
	
	public bool HasConformance(TypeSymbol type, TraitType trait, Conformance? except = null) =>
		type is TypeParameterSymbol parameter
			? AddRequiredTraits(GetDeclaredBounds(parameter), parameter).Contains(trait) ||
			  _blanketConformances.Any(blanket => blanket != except && blanket.Trait == trait.Trait &&
			                                      Satisfies(blanket.Parameters, [parameter]) &&
			                                      GetConformanceArguments(blanket, parameter)
				                                      .SequenceEqual(trait.Arguments))
			: GetConformanceCandidates(type).Any(conformance => conformance != except &&
			                                                    Provides(conformance, type, trait));
	
	private bool Provides(Conformance conformance, TypeSymbol type, TraitType trait) =>
		conformance.Trait == trait.Trait && Matches(conformance, type) &&
		GetConformanceArguments(conformance, type).SequenceEqual(trait.Arguments);
	
	public ImmutableArray<TypeSymbol> GetConformanceArguments(Conformance conformance, TypeSymbol type)
	{
		if (conformance.Parameters.IsEmpty || conformance.Arguments.IsEmpty)
			return conformance.Arguments;
		
		var map = CreateMap(conformance.Parameters, GetTargetArguments(conformance, type));
		return [..conformance.Arguments.Select(argument => Substitute(argument, map))];
	}
	
	public TraitType GetConformanceTrait(Conformance conformance, TypeSymbol type) =>
		GetTraitType(conformance.Trait, GetConformanceArguments(conformance, type));
	
	private bool Matches(Conformance conformance, TypeSymbol type) => !conformance.IsTemplate &&
	                                                                  (conformance.Target is TypeParameterSymbol
		                                                                  ? type is not TypeParameterSymbol &&
		                                                                    Satisfies(conformance.Parameters,
			                                                                    [type, ..conformance.FreeArguments])
		                                                                  : type is not TypeParameterSymbol &&
		                                                                    HasShape(conformance.Target, type) &&
		                                                                    (conformance.Impl is null
			                                                                    ? GetShapeArguments(type)
			                                                                    : BindBlock(conformance.Impl,
				                                                                    GetShapeArguments(type)))
		                                                                    is { } arguments &&
		                                                                    Satisfies(conformance.Parameters,
		                                                                    [
			                                                                    ..arguments, ..conformance.FreeArguments
		                                                                    ]));
	
	private static bool HasShape(TypeSymbol target, TypeSymbol type) =>
		target is ArrayType ? type is ArrayType
		: IsTypedPointer(target) ? IsTypedPointer(type)
		: target == type.OriginalDefinition;
	
	private static bool IsTypedPointer(TypeSymbol type) => type is PointerType && type != NativeSymbols.VoidPtr;
	
	public ImmutableArray<TypeSymbol>? BindBlock(ImplSymbol impl, ImmutableArray<TypeSymbol> arguments)
	{
		if (impl.TargetArguments.Length != arguments.Length)
			return null;
		
		if (impl.NestedParameters.IsEmpty)
			return impl.TargetArguments.Zip(arguments)
				.All(static pair => pair.First is TypeParameterSymbol || pair.First == pair.Second)
				? arguments
				: null;
		
		var variables = new OrderedSet<TypeParameterSymbol>(impl.NestedParameters);
		var bindings = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		for (var i = 0; i < arguments.Length; i++)
		{
			if (impl.TargetArguments[i] is TypeParameterSymbol slot && !variables.Contains(slot))
				continue;
			
			if (!TryUnify(impl.TargetArguments[i], arguments[i], variables, bindings))
				return null;
		}
		
		return
		[
			..arguments,
			..impl.NestedParameters.Select((parameter, i) => impl.NestedArguments[i] is TypeParameterSymbol
				? bindings.GetValueOrDefault(parameter, parameter)
				: impl.NestedArguments[i])
		];
	}
	
	private ImmutableArray<TypeSymbol> GetShapeArguments(TypeSymbol type) => type switch
	{
		ArrayType array => [array.ElementType, GetLengthArgument(array)],
		PointerType { BaseType: var baseType } when IsTypedPointer(type) => [baseType],
		_ => type.TypeArguments
	};
	
	private bool Satisfies(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TypeSymbol> arguments) =>
		arguments.Length == parameters.Length && FindViolation(parameters, arguments) is null;
	
	private DiagnosticText? FindViolation(ImmutableArray<TypeParameterSymbol> parameters,
		ImmutableArray<TypeSymbol> arguments, string? owner = null)
	{
		var map = CreateMap(parameters, arguments);
		return parameters.Select((parameter, i) => FindConstraintViolation(parameter, arguments[i], map, owner))
			.FirstOrDefault(static violation => violation is not null);
	}
	
	private bool SatisfiesBlock(ImplSymbol impl, ImmutableArray<TypeSymbol> arguments) =>
		arguments.Length == impl.TypeParameters.Length && FindBlockViolation(impl, arguments) is null;
	
	private DiagnosticText? FindBlockViolation(ImplSymbol impl, ImmutableArray<TypeSymbol> arguments,
		string? owner = null)
	{
		if (impl.FreeParameters.IsEmpty)
			return FindViolation(impl.TypeParameters, arguments, owner);
		
		ImmutableArray<TypeParameterSymbol> parameters = [..impl.TypeParameters, ..impl.FreeParameters];
		DiagnosticText? first = null;
		foreach (var binding in FindFreeBindings(impl, arguments))
		{
			var violation = FindViolation(parameters,
				[
					..arguments,
					..impl.FreeParameters.Select(parameter => binding.GetValueOrDefault(parameter, parameter))
				],
				owner);
			
			if (violation is null)
				return null;
			
			first ??= violation;
		}
		
		return first ?? FindViolation(parameters, [..arguments, ..impl.FreeParameters], owner);
	}
	
	public IReadOnlyList<Dictionary<TypeParameterSymbol, TypeSymbol>> FindFreeBindings(ImplSymbol impl,
		ImmutableArray<TypeSymbol> arguments)
	{
		var map = CreateMap(impl.TypeParameters, arguments);
		var variables = new OrderedSet<TypeParameterSymbol>(impl.FreeParameters);
		var requirements = impl.TypeParameters.Concat(impl.FreeParameters)
			.SelectMany(parameter => GetDeclaredBounds(parameter)
				.Where(bound => FindTypeParameters(bound).Any(variables.Contains))
				.Select(bound => (Subject: (TypeSymbol)parameter, Bound: SubstituteTrait(bound, map))))
			.ToList();
		
		var results = new List<Dictionary<TypeParameterSymbol, TypeSymbol>>();
		BindFrom(0, []);
		return results;
		
		void BindFrom(int index, Dictionary<TypeParameterSymbol, TypeSymbol> bindings)
		{
			if (index == requirements.Count)
			{
				results.Add(bindings);
				return;
			}
			
			var (subject, bound) = requirements[index];
			subject = Substitute(Substitute(subject, map), bindings);
			if (subject is TypeParameterSymbol unbound && variables.Contains(unbound))
			{
				BindFrom(index + 1, bindings);
				return;
			}
			
			foreach (var candidate in GetTraitTypes(subject, bound.Trait))
			{
				var attempt = new Dictionary<TypeParameterSymbol, TypeSymbol>(bindings);
				if (TryUnify(bound, candidate, variables, attempt))
					BindFrom(index + 1, attempt);
			}
		}
	}
	
	private IEnumerable<TraitType> GetTraitTypes(TypeSymbol type, TraitSymbol trait) => type switch
	{
		InvalidType => [],
		TypeParameterSymbol parameter => GetBounds(parameter).Where(bound => bound.Trait == trait),
		_ => FindConformances(type)
			.Where(conformance => conformance.Trait == trait)
			.Select(conformance => GetConformanceTrait(conformance, type))
	};
	
	public void AddMemberBlock(ImplSymbol impl, TypeSymbol target)
	{
		InvalidateConformanceCache();
		_memberBlocks.Add((impl, target));
	}
	
	public IEnumerable<ImplSymbol> FindMemberBlocks(TypeSymbol type) =>
		Memoize(memberBlockCache, type, ComputeMemberBlocks);
	
	private IEnumerable<ImplSymbol> ComputeMemberBlocks(TypeSymbol type) => _memberBlocks
		.Where(block => block.Target is TypeParameterSymbol
			? Satisfies(block.Impl.TypeParameters, [type])
			: type is not TypeParameterSymbol && HasShape(block.Target, type) &&
			  BindBlock(block.Impl, GetShapeArguments(type)) is { } arguments &&
			  SatisfiesBlock(block.Impl, arguments))
		.Select(static block => block.Impl);
	
	public TypeSymbol? GetMemberBlockTarget(ImplSymbol impl) =>
		_memberBlocks.FirstOrDefault(block => block.Impl == impl).Target;
	
	public DiagnosticText? FindBlockViolation(TypeSymbol type, string name) => type is TypeParameterSymbol
		? null
		: _memberBlocks
			.Select(static block => (block.Target, block.Impl))
			.Concat(_conformances
				.Where(static conformance => conformance.Impl is not null)
				.Select(static conformance => (Target: conformance.Target.OriginalDefinition, Impl: conformance.Impl!)))
			.Where(block => HasShape(block.Target, type) &&
			                BindBlock(block.Impl, GetShapeArguments(type)) is not null &&
			                (block.Impl.Functions.Any(method => method.Name == name) ||
			                 block.Impl.Properties.Any(property => property.Name == name)))
			.Select(block =>
				FindBlockViolation(block.Impl, BindBlock(block.Impl, GetShapeArguments(type))!.Value, name))
			.FirstOrDefault(static violation => violation is not null);
	
	public IEnumerable<FunctionSymbol> GetWitnessFunctions() => _conformances
		.SelectMany(static conformance => conformance.Witnesses.Values)
		.OfType<FunctionWitness>()
		.Select(static witness => witness.Function);
	
	public IEnumerable<FunctionSymbol> GetWitnessFunctions(TypeSymbol definition) => _conformances
		.Where(conformance => conformance.Target.OriginalDefinition == definition)
		.SelectMany(static conformance => conformance.Witnesses.Values)
		.OfType<FunctionWitness>()
		.Select(static witness => witness.Function);
	
	public IEnumerable<ImplSymbol> GetMemberBlocks(TypeSymbol definition) => _memberBlocks
		.Where(block => block.Target == definition)
		.Select(static block => block.Impl);
	
	public IReadOnlyList<(ImplSymbol Impl, TypeSymbol Target)> MemberBlocks => _memberBlocks;
	
	public IReadOnlyList<Conformance> Conformances => _conformances;
	
	public Witness? FindWitness(TypeSymbol self, FunctionSymbol requirement,
		ImmutableArray<TypeSymbol> traitArguments) =>
		requirement.Trait is { } trait && FindConformance(self, GetTraitType(trait, traitArguments)) is { } conformance
			? conformance.Witnesses.GetValueOrDefault(requirement)
			: null;
	
	public ImmutableArray<TypeSymbol> GetRefinedArguments(FunctionSymbol function, TypeSymbol self,
		ImmutableArray<TypeSymbol> traitArguments)
	{
		var map = CreateMap(function.Trait!.TypeParameters, traitArguments);
		map[function.Trait.Self] = self;
		return [..function.RefinedArguments.Select(argument => Substitute(argument, map))];
	}
	
	private ImmutableArray<TypeSymbol>? FindRefiningArguments(TypeSymbol self, FunctionSymbol witness,
		ImmutableArray<TypeSymbol> requirementArguments) => FindConformances(self)
		.Where(conformance => conformance.Trait == witness.Trait)
		.Select(conformance => GetConformanceTrait(conformance, self).Arguments)
		.Where(arguments => GetRefinedArguments(witness, self, arguments).SequenceEqual(requirementArguments))
		.Select(static arguments => (ImmutableArray<TypeSymbol>?)arguments)
		.FirstOrDefault();
	
	public NativeWitness? FindNativeWitness(FunctionSymbol requirement, FunctionSignature expected, TypeSymbol self)
	{
		if (self is not (PrimitiveType or EnumSymbol or PointerType) || requirement.Syntax is not FunctionNode node ||
		    node.Identifier.Type is TokenType.Identifier or TokenType.KeywordAs or TokenType.KeywordNew)
			return null;
		
		if (node.Receiver is not null && expected.ParameterTypes.Length == 2)
			return expected.GetMode(0) == ParameterMode.Mut && OperatorRegistry
				.GetBinaryCandidates(node.Identifier.Type)
				.OfType<OperationImpl>()
				.FirstOrDefault(candidate => candidate.ReturnType == self && candidate.ParameterTypes[0] == self &&
				                             candidate.ParameterTypes[1] == expected.ParameterTypes[1]) is { } compound
				? new NativeWitness(compound, true)
				: null;
		
		var candidates = node.Receiver is null
			? OperatorRegistry.GetBinaryCandidates(node.Identifier.Type)
			: OperatorRegistry.GetUnaryCandidates(node.Identifier.Type);
		
		return candidates.OfType<OperationImpl>().FirstOrDefault(candidate =>
			candidate.ReturnType == expected.ReturnType &&
			candidate.ParameterTypes.SequenceEqual(expected.ParameterTypes)) is { } native
			? new NativeWitness(native)
			: null;
	}
	
	public static ImmutableArray<TypeSymbol> GetTraitArguments(FunctionInfo function) =>
		function.Symbol.Trait is { TypeParameters.Length: > 0 and var count } &&
		!function.TypeArguments.IsDefaultOrEmpty
			? function.TypeArguments.Slice(1, count)
			: [];
	
	public ImmutableArray<TypeSymbol> GetWitnessArguments(TypeSymbol self, FunctionSymbol witness,
		ImmutableArray<TypeSymbol> traitArguments, IEnumerable<TypeSymbol> declared, TraitSymbol? requirement = null)
	{
		if (witness.Trait is { } trait && requirement is not null && trait != requirement &&
		    FindRefiningArguments(self, witness, traitArguments.IsDefault ? [] : traitArguments) is { } refining)
			return [self, ..refining, ..declared];
		
		if (witness.Trait is { } own)
			return traitArguments.IsDefault
				? [self, ..own.TypeParameters, ..declared]
				: [self, ..traitArguments, ..declared];
		
		if (witness.Impl is { } blanket && GetMemberBlockTarget(blanket) is TypeParameterSymbol)
			return [self, ..declared];
		
		ImmutableArray<TypeSymbol> outer = witness.Impl is { Node.Traits.IsEmpty: false } traitImpl
			? [..traitImpl.FreeParameters]
			: [];
		
		if (witness.Impl is null || FindConformance(self, witness.Impl, traitArguments) is not { } conformance)
			return [..BindArguments(witness.Impl, GetShapeArguments(self)), ..outer, ..declared];
		
		var map = CreateMap(conformance.Parameters, GetTargetArguments(conformance, self));
		return
		[
			..witness.Impl.TypeParameters.Select(parameter => map[parameter]),
			..outer.Select(parameter => map.GetValueOrDefault((TypeParameterSymbol)parameter, parameter)),
			..declared
		];
	}
	
	private Conformance? FindConformance(TypeSymbol self, ImplSymbol impl, ImmutableArray<TypeSymbol> traitArguments)
	{
		var matching = GetConformanceCandidates(self)
			.Where(conformance => conformance.Impl == impl && Matches(conformance, self))
			.ToList();
		
		return traitArguments.IsDefaultOrEmpty
			? matching.FirstOrDefault()
			: matching.FirstOrDefault(conformance =>
				  GetConformanceArguments(conformance, self).SequenceEqual(traitArguments)) ??
			  matching.FirstOrDefault();
	}
	
	public IReadOnlyList<FunctionInfo> GetConstructors(TypeSymbol type)
	{
		return type is TypeParameterSymbol parameter
			? GetParameterConstructors(parameter)
			: FindConstructors(type, false);
	}
	
	public IReadOnlyList<FunctionInfo> GetDeclaredConstructors(TypeSymbol type) => type is TypeParameterSymbol
		? GetConstructors(type)
		: FindConstructors(type, true);
	
	public IEnumerable<(FunctionInfo Constructor, DiagnosticText Violation)> FindUnmetConstructors(TypeSymbol type) =>
		type is TypeParameterSymbol
			? []
			: GetBlockConstructors(type)
				.Select(block => (block.Info, Arguments: BindArguments(block.Impl, GetShapeArguments(type)),
					block.Impl))
				.Select(block => (block.Info, block.Arguments,
					Violation: FindViolation(block.Impl.TypeParameters, block.Arguments, "new")))
				.Where(static block => block.Violation is not null)
				.Select(block => (InstantiateFunction(block.Info, block.Arguments), block.Violation!));
	
	private IReadOnlyList<FunctionInfo> GetBodyConstructors(TypeSymbol type)
	{
		Complete(type);
		return _constructors.TryGetValue(type, out var list) ? list : [];
	}
	
	private IReadOnlyList<FunctionInfo> FindConstructors(TypeSymbol type, bool includesUnmet)
	{
		var declared = GetBodyConstructors(type);
		if (_blockConstructors.Count == 0)
			return declared;
		
		List<FunctionInfo> blocks =
		[
			..GetBlockConstructors(type)
				.Select(block => (block.Impl, block.Info,
					Arguments: BindArguments(block.Impl, GetShapeArguments(type))))
				.Where(block => includesUnmet || Satisfies(block.Impl.TypeParameters, block.Arguments))
				.Select(block => InstantiateFunction(block.Info, block.Arguments))
		];
		
		return blocks.Count == 0 ? declared : [..declared, ..blocks];
	}
	
	private IEnumerable<(ImplSymbol Impl, FunctionInfo Info)> GetBlockConstructors(TypeSymbol type) =>
		_blockConstructors.Where(block =>
			IsBlockOf(block.Impl, type) && BindBlock(block.Impl, GetShapeArguments(type)) is not null);
	
	private bool IsBlockOf(ImplSymbol impl, TypeSymbol type) =>
		GetMemberBlockTarget(impl) is { } target and not TypeParameterSymbol &&
		target.OriginalDefinition == type.OriginalDefinition;
	
	private List<FunctionInfo> GetParameterConstructors(TypeParameterSymbol parameter)
	{
		var constructors = new List<FunctionInfo>();
		var traitConstructors = GetBounds(parameter).SelectMany(bound => GetTraitConstructors(bound.Trait)
			.Select(constructor => InstantiateFunction(constructor, [parameter, ..bound.Arguments])));
		
		foreach (var constructor in GetConstructorBounds(parameter).Concat(traitConstructors))
		{
			if (!constructors.Any(existing => MatchesConstructor(existing.Signature, constructor.Signature)))
				constructors.Add(constructor);
		}
		
		return constructors;
	}
	
	private IEnumerable<FunctionInfo> GetTraitConstructors(TraitSymbol trait)
	{
		TraitCompleter?.Invoke(trait);
		return _traitConstructors.TryGetValue(trait, out var constructors) ? constructors : [];
	}
	
	public static bool MatchesConstructor(FunctionSignature candidate, FunctionSignature expected,
		bool allowsStandIns = false) => candidate.HasVariadicParameter == expected.HasVariadicParameter &&
		                                ParametersMatch(candidate, expected, allowsStandIns);
	
	public static bool ParametersMatch(FunctionSignature candidate, FunctionSignature expected,
		bool allowsStandIns) =>
		candidate.ParameterTypes.Length == expected.ParameterTypes.Length &&
		Enumerable.Range(0, expected.ParameterTypes.Length).All(i => allowsStandIns
			? candidate.GetMode(i).CanStandInOrAdapt(candidate.ParameterTypes[i], expected.GetMode(i),
				expected.ParameterTypes[i])
			: candidate.ParameterTypes[i] == expected.ParameterTypes[i] && candidate.GetMode(i) == expected.GetMode(i));
	
	public bool ParametersMatchOrCopy(FunctionSignature candidate, FunctionSignature expected, bool allowsStandIns) =>
		ParametersMatch(candidate, expected, allowsStandIns) || allowsStandIns &&
		candidate.ParameterTypes.Length == expected.ParameterTypes.Length &&
		Enumerable.Range(0, expected.ParameterTypes.Length).All(i =>
			candidate.GetMode(i).CanStandInOrAdapt(candidate.ParameterTypes[i], expected.GetMode(i),
				expected.ParameterTypes[i]) ||
			CopiesForImm(candidate.GetMode(i), candidate.ParameterTypes[i], expected.GetMode(i),
				expected.ParameterTypes[i]));
	
	public bool CopiesForImm(ParameterMode mode, TypeSymbol type, ParameterMode expectedMode,
		TypeSymbol expectedType) =>
		mode == ParameterMode.Value && expectedMode == ParameterMode.Imm &&
		(type == expectedType || type == GetPassedType(expectedType, mode)) && IsCopy(type);
	
	public bool CopiesForImm(FunctionType source, FunctionType target) => !target.IsExternal &&
	                                                                      source.IsVariadic == target.IsVariadic &&
	                                                                      source.HasVariadicParameter ==
	                                                                      target.HasVariadicParameter &&
	                                                                      FunctionType.ReturnStandsIn(source, target) &&
	                                                                      source.ParameterTypes.Length ==
	                                                                      target.ParameterTypes.Length &&
	                                                                      Enumerable.Range(0,
		                                                                      source.ParameterTypes.Length).All(i =>
		                                                                      source.ParameterModes[i]
			                                                                      .CanStandIn(source.ParameterTypes[i],
				                                                                      target.ParameterModes[i],
				                                                                      target.ParameterTypes[i]) ||
		                                                                      CopiesForImm(source.ParameterModes[i],
			                                                                      source.ParameterTypes[i],
			                                                                      target.ParameterModes[i],
			                                                                      target.ParameterTypes[i]));
	
	public static string DescribeConstructorConstraint(string parameters) =>
		parameters.Length == 0 ? "new" : $"new({parameters})";
	
	public static string DescribeParameters(FunctionSignature constructor) => string.Join(", ",
		Enumerable.Range(1, constructor.ParameterTypes.Length - 1)
			.Select(i => constructor.GetMode(i).Describe(constructor.ParameterTypes[i])));
}