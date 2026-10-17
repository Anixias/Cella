using System.Collections.Immutable;
using Cella.Core.Collections;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public void AddConformance(Conformance conformance)
	{
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
		type is TypeParameterSymbol ? [] : GetExplicitCandidates(type).Concat(_blanketConformances);
	
	private IReadOnlyList<Conformance> GetExplicitCandidates(TypeSymbol type) => type switch
	{
		ArrayType => _arrayConformances,
		_ when IsTypedPointer(type) => _pointerConformances,
		_ => _conformancesByTarget.GetValueOrDefault(type.OriginalDefinition) ?? []
	};
	
	public IEnumerable<Conformance> FindConformances(TypeSymbol type)
	{
		if (type is TypeParameterSymbol)
			return [];
		
		var explicitOnes = GetExplicitCandidates(type).Where(conformance => Matches(conformance, type)).ToList();
		var blankets = _blanketConformances.Where(blanket => Matches(blanket, type)).ToList();
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
	
	private ImmutableArray<TypeSymbol> GetTargetArguments(Conformance conformance, TypeSymbol type) =>
		conformance.Target is TypeParameterSymbol ? [type] : GetShapeArguments(type);
	
	public void SetBounds(TypeParameterSymbol parameter, ImmutableArray<TraitType> traits) =>
		_bounds[parameter] = traits;
	
	public ImmutableArray<TraitType> GetDeclaredBounds(TypeParameterSymbol parameter) =>
		_bounds.GetValueOrDefault(parameter, []);
	
	public ImmutableArray<TraitType> GetBounds(TypeParameterSymbol parameter) =>
		AddRequiredTraits([..GetDeclaredBounds(parameter).Concat(GetBlanketTraits(parameter)).Distinct()], parameter);
	
	public void SetParameterBounds(TypeParameterSymbol parameter, ImmutableArray<TypeParameterSymbol> traits) =>
		_parameterBounds[parameter] = traits;
	
	public void SetFunctionBounds(TypeParameterSymbol parameter, ImmutableArray<FunctionType> functions) =>
		_functionBounds[parameter] = functions;
	
	public ImmutableArray<FunctionType> GetFunctionBounds(TypeParameterSymbol parameter) =>
		_functionBounds.GetValueOrDefault(parameter, []);
	
	public ImmutableArray<FunctionType> GetCallableSignatures(TypeSymbol type) => type switch
	{
		ClosureType closure => [closure.Signature],
		FunctionType function => [function],
		TypeParameterSymbol parameter => GetFunctionBounds(parameter),
		DynType { Function: { } function } => [function],
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
	
	public void SetConstructorBounds(TypeParameterSymbol parameter, ImmutableArray<FunctionInfo> constructors) =>
		_constructorBounds[parameter] = constructors;
	
	public ImmutableArray<FunctionInfo> GetConstructorBounds(TypeParameterSymbol parameter) =>
		_constructorBounds.GetValueOrDefault(parameter, []);
	
	public void SetRequiredTraits(TraitSymbol trait, ImmutableArray<TraitType> required) =>
		_requiredTraits[trait] = required;
	
	public ImmutableArray<TraitType> GetRequiredTraits(TraitSymbol trait) =>
		_requiredTraits.GetValueOrDefault(trait, []);
	
	public IEnumerable<TraitType> GetRequiredTraits(TraitType trait, TypeSymbol self)
	{
		var map = CreateMap(trait.Trait.TypeParameters, trait.Arguments);
		map[trait.Trait.Self] = self;
		return GetRequiredTraits(trait.Trait).Select(required => SubstituteTrait(required, map));
	}
	
	private ImmutableArray<TraitType> AddRequiredTraits(ImmutableArray<TraitType> traits, TypeSymbol self)
	{
		if (traits.All(trait => GetRequiredTraits(trait.Trait).IsEmpty))
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
	
	public ImmutableArray<TraitType> GetDynRequirements(TraitType trait)
	{
		var map = CreateMap(trait.Trait.TypeParameters, trait.Arguments);
		return [..GetDynRequirements(trait.Trait).Select(required => SubstituteTrait(required, map))];
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
			foreach (var required in GetDynRequirements(entry.Trait))
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
			var blankets = _blanketConformances.Where(blanket => Provides(blanket, type, trait)).ToList();
			return blankets.FirstOrDefault(blanket => !blankets.Any(other => IsMoreConstrained(other, blanket)));
		}
		finally
		{
			_blanketLookupsInProgress.Remove((type, trait.Trait));
		}
	}
	
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
	
	private bool Matches(Conformance conformance, TypeSymbol type) => conformance.Target is TypeParameterSymbol
		? type is not TypeParameterSymbol && Satisfies(conformance.Parameters, [type])
		: type is not TypeParameterSymbol &&
		  (conformance.Target is ArrayType ? type is ArrayType
			  : IsTypedPointer(conformance.Target) ? IsTypedPointer(type)
			  : conformance.Target == type.OriginalDefinition) &&
		  Fits(conformance.Impl, GetShapeArguments(type)) &&
		  Satisfies(conformance.Parameters, GetShapeArguments(type));
	
	private static bool IsTypedPointer(TypeSymbol type) => type is PointerType && type != NativeSymbols.VoidPtr;
	
	private static bool Fits(ImplSymbol? impl, ImmutableArray<TypeSymbol> arguments) => impl is null ||
		impl.TargetArguments.Length == arguments.Length && impl.TargetArguments
			.Zip(arguments)
			.All(static pair => pair.First is TypeParameterSymbol || pair.First == pair.Second);
	
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
	
	public void AddMemberBlock(ImplSymbol impl, TypeSymbol target) => _memberBlocks.Add((impl, target));
	
	public IEnumerable<ImplSymbol> FindMemberBlocks(TypeSymbol type) => _memberBlocks
		.Where(block => block.Target is TypeParameterSymbol
			? Satisfies(block.Impl.TypeParameters, [type])
			: type is not TypeParameterSymbol && block.Target == type.OriginalDefinition &&
			  Fits(block.Impl, type.TypeArguments) && Satisfies(block.Impl.TypeParameters, type.TypeArguments))
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
			.Where(block => block.Target == type.OriginalDefinition &&
			                block.Impl.TypeParameters.Length == type.TypeArguments.Length &&
			                Fits(block.Impl, type.TypeArguments) &&
			                (block.Impl.Functions.Any(method => method.Name == name) ||
			                 block.Impl.Properties.Any(property => property.Name == name)))
			.Select(block => FindViolation(block.Impl.TypeParameters, type.TypeArguments, name))
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
	
	public static ImmutableArray<TypeSymbol> GetTraitArguments(FunctionInfo function) =>
		function.Symbol.Trait is { TypeParameters.Length: > 0 and var count } &&
		!function.TypeArguments.IsDefaultOrEmpty
			? function.TypeArguments.Slice(1, count)
			: [];
	
	public ImmutableArray<TypeSymbol> GetWitnessArguments(TypeSymbol self, FunctionSymbol witness,
		ImmutableArray<TypeSymbol> traitArguments, IEnumerable<TypeSymbol> declared)
	{
		if (witness.Trait is { } trait)
			return traitArguments.IsDefault
				? [self, ..trait.TypeParameters, ..declared]
				: [self, ..traitArguments, ..declared];
		
		if (witness.Impl is { } blanket && GetMemberBlockTarget(blanket) is TypeParameterSymbol)
			return [self, ..declared];
		
		if (witness.Impl is null || FindConformance(self, witness.Impl) is not { } conformance)
			return [..self.TypeArguments, ..declared];
		
		var map = CreateMap(conformance.Parameters, GetTargetArguments(conformance, self));
		return [..witness.Impl.TypeParameters.Select(parameter => map[parameter]), ..declared];
	}
	
	private Conformance? FindConformance(TypeSymbol self, ImplSymbol impl) =>
		GetConformanceCandidates(self)
			.FirstOrDefault(conformance => conformance.Impl == impl && Matches(conformance, self));
	
	public IReadOnlyList<FunctionInfo> GetConstructors(TypeSymbol type)
	{
		if (type is TypeParameterSymbol parameter)
			return GetParameterConstructors(parameter);
		
		Complete(type);
		return _constructors.TryGetValue(type, out var list) ? list : [];
	}
	
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
		mode == ParameterMode.ReadOnly && expectedMode == ParameterMode.Imm &&
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