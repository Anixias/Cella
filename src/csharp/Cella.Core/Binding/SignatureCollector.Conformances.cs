using System.Collections.Immutable;
using Cella.Core.Binding.Operations;
using Cella.Core.Collections;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void CheckConformances()
	{
		foreach (var sameTrait in _localConformances.GroupBy(static c =>
			         (Target: c.Target is TypeParameterSymbol ? null : c.Target, c.Trait)))
			ReportOverlaps([..sameTrait]);
		
		foreach (var conformance in _localConformances)
		{
			var trait = DescribeTrait(conformance);
			if (conformance.Target is TypeParameterSymbol && !IsLocal(conformance.Trait))
				Diagnostics.Add(new(DiagnosticSeverity.Error, conformance.Location,
					$"'{trait}' is declared in another project"));
			else if (conformance.Impl is not null && !IsLocal(conformance.Trait) && !IsLocal(conformance.Target))
				Diagnostics.Add(new(DiagnosticSeverity.Error, conformance.Location,
					$"'{trait}' and '{conformance.Target.Name}' are both declared in other projects"));
			
			MatchRequirements(conformance);
			CheckRequiredTraits(conformance);
		}
		
		foreach (var impl in _impls)
			ReportUnmatchedMembers(impl);
	}
	
	private void CheckRequiredTraits(Conformance conformance)
	{
		var self = GetConformanceSelf(conformance);
		if (self is InvalidType)
			return;
		
		var trait = _typePool.GetTraitType(conformance.Trait, conformance.Arguments);
		foreach (var required in _typePool.GetRequiredTraits(trait, self))
		{
			if (!_typePool.Conforms(self, required))
				ReportConformance(conformance.Location,
					$"'{DiagnosticReporter.DescribeType(self)}' needs '{required.Name}'");
		}
	}
	
	private void ReportOverlaps(List<Conformance> conformances)
	{
		foreach (var conformance in conformances)
		{
			var overlap = conformances
				.Where(other => other != conformance && !AreRanked(conformance, other))
				.Select(other => FindOverlap(conformance, other))
				.FirstOrDefault(static found => found is not null);
			
			if (overlap is var (target, trait))
				Diagnostics.Add(new(DiagnosticSeverity.Error, conformance.Location,
					$"'{DiagnosticReporter.DescribeType(target)}' implements '{trait.Name}' more than once"));
		}
	}
	
	private bool AreRanked(Conformance conformance, Conformance other) =>
		conformance.Target is TypeParameterSymbol && other.Target is TypeParameterSymbol &&
		(_typePool.IsMoreConstrained(conformance, other) || _typePool.IsMoreConstrained(other, conformance));
	
	private (TypeSymbol Target, TraitType Trait)? FindOverlap(Conformance conformance, Conformance other)
	{
		var renamed = TypePool.CreateMap(other.Parameters, [..conformance.Parameters]);
		var bindings = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		var variables = new OrderedSet<TypeParameterSymbol>(conformance.Parameters);
		var pattern = GetPattern(conformance);
		var otherPattern = GetPattern(other);
		if (pattern.Length != otherPattern.Length)
			return null;
		
		for (var i = 0; i < pattern.Length; i++)
		{
			if (!_typePool.TryUnify(pattern[i], _typePool.Substitute(otherPattern[i], renamed), variables, bindings))
				return null;
		}
		
		for (var i = 0; i < conformance.Arguments.Length; i++)
		{
			var argument = _typePool.Substitute(other.Arguments[i], renamed);
			if (!_typePool.TryUnify(conformance.Arguments[i], argument, variables, bindings))
				return null;
		}
		
		var definition = conformance.Target as NamedTypeSymbol;
		var display = TypePool.CreateMap(conformance.Parameters, [..definition?.TypeParameters ?? []]);
		var declared = _typePool.GetTraitType(conformance.Trait, conformance.Arguments);
		var trait = _typePool.SubstituteTrait(_typePool.SubstituteTrait(declared, bindings), display);
		
		if (definition is null ||
		    bindings.Count == 0 && pattern.All(static argument => argument is TypeParameterSymbol))
			return (conformance.Target, trait);
		
		return (_typePool.Instantiate(definition,
		[
			..pattern.Select(argument => _typePool.Substitute(_typePool.Substitute(argument, bindings), display))
		]), trait);
	}
	
	private static ImmutableArray<TypeSymbol> GetPattern(Conformance conformance) =>
		conformance.Impl?.TargetArguments ?? [..conformance.Parameters];
	
	private string DescribeTrait(Conformance conformance) =>
		_typePool.GetTraitType(conformance.Trait, conformance.Arguments).Name;
	
	private TypeSymbol GetConformanceSelf(Conformance conformance) =>
		conformance.Impl is { } impl ? GetImplTarget(impl) : conformance.Target;
	
	private static Dictionary<TypeParameterSymbol, TypeSymbol> CreateTraitMap(Conformance conformance, TypeSymbol self)
	{
		var map = TypePool.CreateMap(conformance.Trait.TypeParameters, conformance.Arguments);
		map[conformance.Trait.Self] = self;
		return map;
	}
	
	private void MatchRequirements(Conformance conformance)
	{
		var self = GetConformanceSelf(conformance);
		if (self is InvalidType)
			return;
		
		var trait = DescribeTrait(conformance);
		var traitMap = CreateTraitMap(conformance, self);
		foreach (var (name, requirement) in GetRequirements(conformance.Trait))
		{
			var info = GetFunctionInfo(requirement);
			var candidates = FindCandidates(conformance, self, name, requirement).ToList();
			var matches = candidates
				.Where(candidate => SignaturesMatch(candidate.Signature, info.Signature, requirement,
					candidate.Function, traitMap))
				.ToList();
			
			if (matches.Count == 0)
				matches = candidates
					.Where(candidate => SignaturesMatch(candidate.Signature, info.Signature, requirement,
						candidate.Function, traitMap, true))
					.ToList();
			
			switch (matches.Count)
			{
				case 1:
					conformance.Witnesses[requirement] =
						new FunctionWitness(matches[0].Function, GetFunctionInfo(matches[0].Function));
					
					_witnessMembers.Add(matches[0].Function);
					ReportNarrowWitness(trait, self, name, matches[0].Function);
					if (matches[0].Function.Syntax is FunctionNode { When: not null })
						ReportConformance(GetFunctionLocation(matches[0].Function),
							$"Cannot implement '{trait}.{name}' with 'when' functions");
					
					break;
				
				case > 1:
					_witnessMembers.UnionWith(matches.Select(static match => match.Function));
					foreach (var match in matches)
						ReportConformance(GetFunctionLocation(match.Function),
							$"'{conformance.Target.Name}.{name}' is declared more than once with the same signature");
					
					break;
				
				case 0 when requirement.Syntax is FunctionNode { Body: not null }:
					conformance.Witnesses[requirement] = new FunctionWitness(requirement, info);
					foreach (var candidate in candidates.Where(candidate =>
						         conformance.Impl is { } impl &&
						         impl.Functions.Any(method => method.Function == candidate.Function) ||
						         SignaturesMatch(candidate.Signature, info.Signature, requirement, candidate.Function,
							         traitMap, true, true)))
					{
						_witnessMembers.Add(candidate.Function);
						ReportConformance(GetFunctionLocation(candidate.Function),
							$"'{name}' doesn't match '{trait}.{name}'");
					}
					
					break;
				
				case 0 when FindNativeWitness(requirement, info.Signature, traitMap, self) is { } native:
					conformance.Witnesses[requirement] = native;
					break;
				
				case 0 when FindSelfConversionWitness(requirement, info.Signature, traitMap, self) is { } conversion:
					conformance.Witnesses[requirement] = conversion;
					break;
				
				case 0 when candidates.Count > 0:
					foreach (var candidate in candidates)
					{
						_witnessMembers.Add(candidate.Function);
						ReportConformance(GetFunctionLocation(candidate.Function),
							$"'{name}' doesn't match '{trait}.{name}'");
					}
					
					break;
				
				case 0 when FindOtherKindMember(conformance, name, requirement) is { } other:
					ReportConformance(other, $"'{name}' doesn't match '{trait}.{name}'");
					break;
				
				default:
					ReportConformance(conformance.Location, DescribeMissingRequirement(conformance, trait, self, name,
						requirement));
					
					break;
			}
		}
		
		foreach (var constructor in conformance.Trait.Constructors)
			MatchConstructor(conformance, trait, traitMap, self, constructor);
	}
	
	private void ReportNarrowWitness(string trait, TypeSymbol self, string name, FunctionSymbol witness)
	{
		var floor = self is NamedTypeSymbol named
			? (Visibility)Math.Max((int)named.Definition.Visibility, (int)Visibility.Module)
			: Visibility.Public;
		
		if (witness.Visibility >= floor)
			return;
		
		var keyword = GetVisibilityKeyword(witness);
		var text = keyword?.Text ?? (witness.Visibility == Visibility.Private ? "pvt" : "mod");
		ReportConformance(keyword?.SourceLocation ?? GetFunctionLocation(witness),
			$"Cannot implement '{trait}.{name}' with '{text}' members");
	}
	
	private static Token? GetVisibilityKeyword(FunctionSymbol function) => function.Syntax switch
	{
		FunctionNode { Visibility: { } keyword } => keyword,
		ConstructorNode { Visibility: { } keyword } => keyword,
		_ => function.Property?.Node?.Visibility
	};
	
	private void ReportConformance(SourceLocation location, string message)
	{
		if (_conformanceErrors.Add((location, message)))
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
	}
	
	private static string DescribeMissingRequirement(Conformance conformance, string trait, TypeSymbol self,
		string name, FunctionSymbol requirement)
	{
		var described = DiagnosticReporter.DescribeType(self);
		if (requirement.Property is null || !HasProperty(conformance, name))
			return $"'{described}' needs '{trait}.{name}'";
		
		var accessor = GetAccessorKind(requirement) == "get" ? "getter" : "setter";
		return $"'{described}' needs a {accessor} for '{trait}.{name}'";
	}
	
	private SourceLocation? FindOtherKindMember(Conformance conformance, string name, FunctionSymbol requirement)
	{
		if (requirement.Property is not null && HasProperty(conformance, name))
			return null;
		
		if (requirement.Property is null &&
		    (conformance.Impl?.Properties.FirstOrDefault(property => property.Name == name) ??
		     conformance.Target.GetProperty(name)) is { Node: { } property })
			return property.SourceLocation;
		
		if (requirement.Property is not null &&
		    (conformance.Impl?.Functions.Where(function => function.Name == name) ?? [])
		    .Concat(conformance.Target.GetFunctions(name))
		    .FirstOrDefault() is { } method)
		{
			_witnessMembers.Add(method.Function);
			return GetFunctionLocation(method.Function);
		}
		
		return conformance.Target is RecordSymbol record &&
		       record.Members.OfType<FieldSymbol>().FirstOrDefault(field => field.Name == name) is { Node: { } field }
			? field.SourceLocation
			: null;
	}
	
	private static bool HasProperty(Conformance conformance, string name) =>
		conformance.Target.GetProperty(name) is not null ||
		conformance.Impl?.Properties.Any(property => property.Name == name) == true;
	
	private void MatchConstructor(Conformance conformance, string trait,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, TypeSymbol self, FunctionSymbol requirement)
	{
		var signature = GetFunctionInfo(requirement).Signature;
		var declared = _typePool.GetConstructors(self);
		var matches = declared
			.Where(constructor => SignaturesMatch(constructor.Signature, signature, requirement, constructor.Symbol,
				traitMap))
			.ToList();
		
		if (matches.Count == 0)
			matches = declared
				.Where(constructor => SignaturesMatch(constructor.Signature, signature, requirement, constructor.Symbol,
					traitMap, true))
				.ToList();
		
		if (matches.Count > 0)
		{
			conformance.Witnesses[requirement] = new FunctionWitness(matches[0].Symbol, matches[0]);
			ReportNarrowWitness(trait, self, "new", matches[0].Symbol);
			return;
		}
		
		var expected = _typePool.SubstituteSignature(signature, traitMap);
		var hidesConstruction = declared.Count > 0 && self is RecordSymbol;
		if (!hidesConstruction && _typePool.FindConstructionWitness(self, expected) is { } construction)
		{
			conformance.Witnesses[requirement] = construction;
			return;
		}
		
		if (_typePool.FindConversionWitness(self, expected) is { } conversion)
		{
			conformance.Witnesses[requirement] = conversion;
			return;
		}
		
		if (!hidesConstruction)
		{
			ReportConformance(conformance.Location, $"'{DiagnosticReporter.DescribeType(self)}' needs '{trait}.new'");
			return;
		}
		
		foreach (var constructor in declared)
			ReportConformance(constructor.Symbol.Syntax is ConstructorNode node
					? node.Keyword.SourceLocation
					: conformance.Location, $"'new' doesn't match '{trait}.new'");
	}
	
	private static IEnumerable<(string Name, FunctionSymbol Function)> GetRequirements(TraitSymbol trait) =>
	[
		..trait.Functions
			.Where(static method => method.Function.Kind != FunctionKind.Destructor &&
			                        method.Function.Visibility != Visibility.Private)
			.Select(static method => (method.Name, method.Function)),
		..trait.Properties.SelectMany(static property => new[] { property.Getter, property.Setter }
			.OfType<FunctionAccessor>()
			.Where(static accessor => accessor.Function.Visibility != Visibility.Private)
			.Select(accessor => (property.Name, accessor.Function)))
	];
	
	private static string GetAccessorKind(FunctionSymbol accessor) => ((FunctionNode)accessor.Syntax).Identifier.Text;
	
	private IEnumerable<(FunctionSymbol Function, FunctionSignature Signature)> FindCandidates(Conformance conformance,
		TypeSymbol self, string name, FunctionSymbol requirement)
	{
		var impl = conformance.Impl;
		IEnumerable<FunctionSymbol> blockMembers = [];
		IEnumerable<FunctionSymbol> typeMembers = [];
		if (requirement.Property is { } property)
		{
			var kind = GetAccessorKind(requirement);
			if (impl?.Properties.FirstOrDefault(p => p.Name == property.Name) is { } blockProperty)
				blockMembers = FindAccessor(blockProperty, kind);
			
			if (conformance.Target.GetProperty(property.Name) is { } typeProperty)
				typeMembers = FindAccessor(typeProperty, kind);
		}
		else
		{
			var memberName = name;
			if (impl is not null)
				blockMembers = impl.Functions.Where(m => m.Name == memberName).Select(static m => m.Function);
			
			typeMembers = conformance.Target.GetFunctions(memberName).Select(static m => m.Function);
		}
		
		foreach (var member in blockMembers)
			yield return (member, GetFunctionInfo(member).Signature);
		
		var map = conformance.Target is NamedTypeSymbol { IsGenericDefinition: true } definition && impl is not null
			? TypePool.CreateMap(definition.TypeParameters, self.TypeArguments)
			: [];
		
		foreach (var member in typeMembers)
		{
			var signature = GetFunctionInfo(member).Signature;
			yield return (member, map.Count == 0 ? signature : _typePool.SubstituteSignature(signature, map));
		}
	}
	
	private static IEnumerable<FunctionSymbol> FindAccessor(PropertySymbol property, string kind) =>
		new[] { property.Getter, property.Setter }
			.OfType<FunctionAccessor>()
			.Select(static accessor => accessor.Function)
			.Where(function => GetAccessorKind(function) == kind);
	
	private bool SignaturesMatch(FunctionSignature candidate, FunctionSignature requirement,
		FunctionSymbol requirementSymbol, FunctionSymbol candidateSymbol,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, bool allowsStandIns = false, bool ignoresReturn = false)
	{
		var declared = requirementSymbol.DeclaredTypeParameters;
		var own = candidateSymbol.DeclaredTypeParameters;
		if (declared.Length != own.Length)
			return false;
		
		var map = new Dictionary<TypeParameterSymbol, TypeSymbol>(traitMap);
		var toRequirement = new Dictionary<TypeParameterSymbol, TypeSymbol>();
		for (var i = 0; i < declared.Length; i++)
		{
			map[declared[i]] = own[i];
			toRequirement[own[i]] = declared[i];
		}
		
		for (var i = 0; i < declared.Length; i++)
		{
			if (!ConstraintsAllowed(own[i], declared[i], toRequirement, traitMap))
				return false;
		}
		
		var expected = _typePool.SubstituteSignature(requirement, map);
		return expected.IsVariadic == candidate.IsVariadic &&
		       expected.HasVariadicParameter == candidate.HasVariadicParameter &&
		       (ignoresReturn || expected.ReturnType == candidate.ReturnType ||
		        allowsStandIns && FunctionType.CanAdapt(candidate.ReturnType, expected.ReturnType)) &&
		       _typePool.ParametersMatchOrCopy(candidate, expected, allowsStandIns);
	}
	
	private bool ConstraintsAllowed(TypeParameterSymbol own, TypeParameterSymbol required,
		Dictionary<TypeParameterSymbol, TypeSymbol> toRequirement, Dictionary<TypeParameterSymbol, TypeSymbol> traitMap)
	{
		if (own.IsValue != required.IsValue || own.IsValue && own.ValueType != required.ValueType)
			return false;
		
		if (own.IsNoref && !required.IsNoref || own.IsCopy && !required.IsCopy || own.HasDrop && !required.HasDrop ||
		    own.HasNull && !required.HasNull || own.IsAtomic && !required.IsAtomic || own.HasNew && !required.HasNew ||
		    own.IsExt && !required.IsExt)
			return false;
		
		var bounds = _typePool.GetBounds(required).Select(bound => _typePool.Substitute(bound, traitMap)).ToList();
		if (_typePool.GetDeclaredBounds(own).Any(bound => !bounds.Contains(_typePool.Substitute(bound, toRequirement))))
			return false;
		
		var parameterBounds = _typePool.GetParameterBounds(required)
			.Select(bound => _typePool.Substitute(bound, traitMap))
			.ToList();
		
		if (_typePool.GetParameterBounds(own)
		    .Any(bound => !parameterBounds.Contains(_typePool.Substitute(bound, toRequirement))))
			return false;
		
		var functionBounds = _typePool.GetFunctionBounds(required)
			.Select(bound => _typePool.Substitute(bound, traitMap))
			.ToList();
		
		if (_typePool.GetFunctionBounds(own)
		    .Any(bound => !functionBounds.Contains(_typePool.Substitute(bound, toRequirement))))
			return false;
		
		var constructorBounds = _typePool.GetConstructorBounds(required)
			.Select(bound => _typePool.SubstituteSignature(bound.Signature, traitMap))
			.ToList();
		
		return _typePool.GetConstructorBounds(own)
			.Select(bound => _typePool.SubstituteSignature(bound.Signature, toRequirement))
			.All(signature => constructorBounds.Any(other => TypePool.ParametersMatch(signature, other, false)));
	}
	
	private NativeWitness? FindNativeWitness(FunctionSymbol requirement, FunctionSignature signature,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, TypeSymbol self)
	{
		if (self is not (PrimitiveType or EnumSymbol or PointerType) || requirement.Syntax is not FunctionNode node ||
		    node.Identifier.Type == TokenType.Identifier || IsConversion(node))
			return null;
		
		var expected = _typePool.SubstituteSignature(signature, traitMap);
		if (self is PointerType && self != NativeSymbols.VoidPtr)
			return FindPointerWitness(node.Identifier.Type, expected, self);
		
		var registry = _typePool.OperatorRegistry;
		if (node.Receiver is not null && expected.ParameterTypes.Length == 2)
			return expected.GetMode(0) == ParameterMode.Mut && registry.GetBinaryCandidates(node.Identifier.Type)
				.OfType<NativeImpl>()
				.FirstOrDefault(candidate => candidate.ReturnType == self && candidate.ParameterTypes[0] == self &&
				                             candidate.ParameterTypes[1] == expected.ParameterTypes[1]) is { } compound
				? new NativeWitness(compound, true)
				: null;
		
		var candidates = node.Receiver is null
			? registry.GetBinaryCandidates(node.Identifier.Type)
			: registry.GetUnaryCandidates(node.Identifier.Type);
		
		return candidates.OfType<NativeImpl>().FirstOrDefault(candidate =>
			candidate.ReturnType == expected.ReturnType &&
			candidate.ParameterTypes.SequenceEqual(expected.ParameterTypes)) is { } native
			? new NativeWitness(native)
			: null;
	}
	
	private SelfConversionWitness? FindSelfConversionWitness(FunctionSymbol requirement, FunctionSignature signature,
		Dictionary<TypeParameterSymbol, TypeSymbol> traitMap, TypeSymbol self)
	{
		if (requirement.Syntax is not FunctionNode { Receiver: not null } node || !IsConversion(node))
			return null;
		
		var expected = _typePool.SubstituteSignature(signature, traitMap);
		if (expected.ParameterTypes.Length != 1 || expected.GetMode(0) == ParameterMode.Mut)
			return null;
		
		var table = _typePool.ConversionTable;
		var implicitConversion = table.FindImplicit(self, expected.ReturnType);
		var conversion = node.Identifier.Type == TokenType.KeywordAs
			? implicitConversion
			: implicitConversion is null
				? table.FindExplicit(self, expected.ReturnType)
				: null;
		
		return conversion is null ? null : new SelfConversionWitness(conversion);
	}
	
	private NativeWitness? FindPointerWitness(TokenType op, FunctionSignature expected, TypeSymbol self) =>
		expected.ParameterTypes.Length == 2 && expected.ParameterTypes.All(type => type == self) &&
		_typePool.OperatorRegistry.GetBinaryCandidates(op).OfType<NativeImpl>().FirstOrDefault(candidate =>
			candidate.ReturnType == expected.ReturnType &&
			candidate.ParameterTypes.All(static type => type == NativeSymbols.VoidPtr)) is { } untyped
			? new NativeWitness(new NativeImpl(op, untyped.ReturnType, self, self))
			: null;
	
	private static SourceLocation GetFunctionLocation(FunctionSymbol function) => function.Syntax switch
	{
		FunctionNode node => node.Identifier.SourceLocation,
		_ => function.Syntax.SourceLocation
	};
	
	private void ReportUnmatchedMembers(ImplSymbol impl)
	{
		var traits = _localConformances.Where(c => c.Impl == impl).Select(c => $"'{DescribeTrait(c)}'").ToList();
		if (traits.Count == 0)
			return;
		
		var members = impl.Functions.Select(static method => (method.Name, method.Function)).Concat(impl.Properties
			.SelectMany(static property => new[] { property.Getter, property.Setter }
				.OfType<FunctionAccessor>()
				.Select(accessor => (property.Name, accessor.Function))));
		
		foreach (var (name, function) in members)
		{
			if (function.Visibility == Visibility.Private || function.Kind == FunctionKind.Destructor ||
			    _witnessMembers.Contains(function))
				continue;
			
			Diagnostics.Add(new(DiagnosticSeverity.Error, GetFunctionLocation(function),
				$"'{name}' isn't a member of {string.Join(" or ", traits)}"));
		}
	}
	
	private bool DeferConstraintCheck(ConstraintCheck check)
	{
		if (!MentionsPending(check.Argument))
			return false;
		
		_deferredChecks.Add(check);
		return true;
	}
	
	private bool MentionsPending(TypeSymbol type) => type switch
	{
		NamedTypeSymbol named => _inProgress.Any(entry => entry.Symbol == named.Definition) ||
		                         named.TypeArguments.Any(MentionsPending),
		PointerType pointer => MentionsPending(pointer.BaseType),
		BorrowType borrow => MentionsPending(borrow.Target),
		ArrayType array => MentionsPending(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(MentionsPending),
		_ => false
	};
	
	private void ReportDeferredChecks()
	{
		foreach (var check in _deferredChecks)
		{
			if (_typePool.FindConstraintViolation(check.Parameter, check.Argument, check.Map) is { } violation)
				Diagnostics.Add(violation.At(check.Location));
		}
		
		_deferredChecks.Clear();
	}
}