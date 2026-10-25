using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public DiagnosticText? FindConstraintViolation(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map = null, string? owner = null) => argument switch
	{
		InvalidType => null,
		_ when parameter.IsTrait => IsTraitArgument(argument) || IsFunctionTrait(argument)
			? null
			: $"'{argument.Name}' is not a trait",
		_ when parameter.IsValue => FindValueViolation(parameter, argument),
		ValueArgumentType or TypeParameterSymbol { IsValue: true } => $"'{argument.Name}' is not a type",
		_ when IsTraitArgument(argument) => $"'{argument.Name}' is not a type",
		DynType => $"Cannot use '{argument.Name}' by value",
		_ when FindUnmetRequirement(parameter, argument, map) is (var requirement, var hint) =>
			new DiagnosticText(
				$"'{owner ?? parameter.Owner ?? parameter.Name}' requires '{parameter.Name}: {requirement}'",
				hint),
		_ => null
	};
	
	private (string Requirement, string Hint)? FindUnmetRequirement(TypeParameterSymbol parameter,
		TypeSymbol argument, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		if (FindUnmetKeyword(parameter, argument, map) is { } unmet)
			return unmet;
		
		var name = argument.Name;
		if (FindUnmetTrait(parameter, argument, map) is { } trait)
			return (trait.Name, $"'{name}' doesn't implement '{trait.Name}'");
		
		if (FindUnmetConstantTrait(parameter, argument, map) is { } constant)
			return ($"const {constant.Name}", $"'{name}' doesn't implement 'const {constant.Name}'");
		
		if (FindUnmetBound(parameter, argument, map) is { } bound)
			return (bound, $"'{name}' doesn't implement '{bound}'");
		
		return FindUnmetFunction(parameter, argument, map) is { } function
			? (function.Name, $"'{name}' doesn't implement '{function.Name}'")
			: null;
	}
	
	private (string Requirement, string Hint)? FindUnmetKeyword(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		var name = argument.Name;
		if (parameter.Declares(TokenType.KeywordAtomic) && !IsAtomic(argument))
			return ("atomic", $"'{name}' can't be accessed atomically");
		
		if (parameter.Declares(TokenType.KeywordExt) && !IsExtCompatible(argument))
			return ("ext", $"'{name}' can't be used in 'ext' signatures");
		
		if (parameter.Declares(TokenType.KeywordNoref) && HoldsBorrows(argument))
			return ("noref", HoldsBorrows(argument, false) ? $"'{name}' holds borrows" : $"'{name}' may hold borrows");
		
		if (parameter.Declares(TokenType.KeywordNull) && !HasNull(argument))
			return ("null", $"'{name}' has no null");
		
		if (parameter.Declares(TokenType.KeywordCopy) && !IsCopy(argument))
			return ("copy", $"'{name}' can't be copied");
		
		if (parameter.Declares(TokenType.KeywordDrop) && !HasDestructor(argument))
			return ("drop", $"'{name}' has no destructor");
		
		if (parameter.Declares(TokenType.KeywordNew) && !HasNew(argument))
			return ("new", $"'{name}' has no constructor without parameters");
		
		if (parameter.Declares(TokenType.KeywordDefault) && !HasDefault(argument))
			return ("default", $"'{name}' has no default");
		
		return FindMissingConstructor(parameter, argument, map) is var (missing, hint)
			? (DescribeConstructorConstraint(missing), hint ?? $"'{name}' has no constructor taking '{missing}'")
			: null;
	}
	
	public DiagnosticText? FindSelfViolation(TraitType trait, TypeSymbol target)
	{
		var map = CreateMap(trait.Trait.TypeParameters, trait.Arguments);
		var self = trait.Trait.Self;
		var unmet = FindUnmetKeyword(self, target, map);
		if (unmet is null && FindUnmetFunction(self, target, map) is { } function)
			unmet = (function.Name, $"'{target.Name}' doesn't implement '{function.Name}'");
		
		return unmet is var (requirement, hint)
			? new DiagnosticText($"'{trait.Name}' requires 'Self: {requirement}'", hint)
			: null;
	}
	
	private string? FindValueViolation(TypeParameterSymbol parameter, TypeSymbol argument) =>
		(argument, parameter.ValueType) switch
		{
			(_, null or InvalidType) => null,
			(ValueArgumentType value, IntegerStorageType type) when IntegerFits?.Invoke(value.Value, type) == false =>
				$"'{value.Value}' doesn't fit in '{type.Name}'",
			(ValueArgumentType, _) => null,
			(TypeParameterSymbol { IsValue: true, ValueType: var type }, var expected)
				when type is null or InvalidType || type == expected ||
				     ConversionTable.FindImplicit(type, expected) is not null => null,
			(TypeParameterSymbol { IsValue: true, ValueType: { } type }, var expected) =>
				$"Cannot convert type '{type.Name}' to '{expected.Name}'",
			_ => $"'{argument.Name}' is not a constant"
		};
	
	private FunctionType? FindUnmetFunction(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		var substitution = map?.ToDictionary() ?? [];
		substitution[parameter] = argument;
		return GetFunctionBounds(parameter)
			.Select(bound => SubstituteFunctionType(bound, substitution))
			.FirstOrDefault(bound => !IsCallableAs(argument, bound));
	}
	
	public static bool IsTraitArgument(TypeSymbol type) => type is TraitType or TypeParameterSymbol { IsTrait: true };
	
	public static bool IsFunctionTrait(TypeSymbol type) => type is FunctionType { IsExternal: false, IsRef: false };
	
	private TraitType? FindUnmetTrait(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		var substitution = map?.ToDictionary() ?? [];
		substitution[parameter] = argument;
		return GetDeclaredBounds(parameter)
			.Select(bound => SubstituteTrait(bound, substitution))
			.FirstOrDefault(bound => !Conforms(argument, bound));
	}
	
	private (string Parameters, string? Hint)? FindMissingConstructor(TypeParameterSymbol parameter,
		TypeSymbol argument, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		var bounds = GetConstructorBounds(parameter);
		if (bounds.IsEmpty)
			return null;
		
		var substitution = map?.ToDictionary() ?? [];
		substitution[parameter] = argument;
		return bounds
			.Select(bound => SubstituteSignature(bound.Signature, substitution))
			.FirstOrDefault(expected => !expected.ParameterTypes.Any(static type => type is InvalidType) &&
			                            !HasConstructor(argument, expected)) is { } missing
			? (DescribeParameters(missing), FindHiddenConstructor(argument, missing) is { } visibility
				? $"'{argument.Name}({DescribeParameters(missing)})' is private to its " +
				  DiagnosticReporter.DescribeScope(visibility, true)
				: DescribeHiddenFields(argument, missing))
			: null;
	}
	
	private Visibility? FindHiddenConstructor(TypeSymbol type, FunctionSignature expected)
	{
		var floor = GetConstructorFloor(type);
		return GetConstructors(type)
			.Where(constructor => constructor.Symbol.Visibility < floor &&
			                      ReachesConstructor(constructor.Signature, expected, true))
			.Select(static constructor => (Visibility?)constructor.Symbol.Visibility)
			.FirstOrDefault();
	}
	
	private string? FindUnmetBound(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		foreach (var bound in GetParameterBounds(parameter))
		{
			switch (map?.GetValueOrDefault(bound) ?? bound)
			{
				case TraitType trait when !Conforms(argument, trait):
					return trait.Name;
				
				case TypeParameterSymbol trait when !Conforms(argument, trait):
					return trait.Name;
				
				case FunctionType function when !IsCallableAs(argument, function):
					return function.Name;
			}
		}
		
		return null;
	}
}