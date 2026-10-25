using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	private readonly Dictionary<TypeParameterSymbol, TypeSymbol> _enumTags = [];
	private readonly HashSet<TypeParameterSymbol> _enumOperations = [];
	
	public void SetEnumTag(TypeParameterSymbol parameter, TypeSymbol tag) => _enumTags[parameter] = tag;
	
	public TypeSymbol? GetEnumTag(TypeParameterSymbol parameter) => _enumTags.GetValueOrDefault(parameter);
	
	public TypeSymbol? FindTag(TypeSymbol type) => type switch
	{
		EnumSymbol { IsMatch: false, HasPayload: false } enumType => GetTagType(enumType),
		TypeParameterSymbol { IsEnum: true } parameter => GetEnumTag(parameter),
		_ => null
	};
	
	public void InheritEnumTag(TypeParameterSymbol parameter)
	{
		if (GetEnumTag(parameter) is not null)
			return;
		
		foreach (var trait in GetInheritedTraits(parameter))
		{
			if (GetEnumTag(trait.Trait.Self) is not { } tag)
				continue;
			
			SetEnumTag(parameter, Substitute(tag, CreateTraitMap(trait, parameter)));
			return;
		}
	}
	
	public void RegisterEnumOperations(TypeParameterSymbol parameter)
	{
		if (!parameter.IsEnum || !_enumOperations.Add(parameter))
			return;
		
		foreach (var integerType in NativeSymbols.PureIntegerTypes)
		{
			ConversionTable.Add(new EnumConversion(parameter, integerType));
			ConversionTable.Add(new EnumConversion(integerType, parameter));
		}
		
		if (GetEnumTag(parameter) is TypeParameterSymbol tag)
		{
			ConversionTable.Add(new EnumConversion(parameter, tag));
			ConversionTable.Add(new EnumConversion(tag, parameter));
		}
		
		OperatorRegistry.Create(new NativeImpl(TokenType.OpEqualEqual, NativeSymbols.Bool, parameter, parameter));
		OperatorRegistry.Create(new NativeImpl(TokenType.OpBangEqual, NativeSymbols.Bool, parameter, parameter));
	}
	
	public TypeParameterSymbol? FindPinningSelf(TypeParameterSymbol parameter,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? outer) => outer?.Keys.FirstOrDefault(self =>
		self.IsSelf && GetEnumTag(self) == parameter && outer[self] is not (TypeParameterSymbol or InvalidType));
	
	private TypeSymbol? FindPinnedArgument(TypeParameterSymbol parameter,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? outer) =>
		FindPinningSelf(parameter, outer) is { } self ? FindTag(outer![self]) : null;
	
	public static string DescribeEnumRequirement(TypeSymbol? tag) => tag is null ? "enum" : $"enum({tag.Name})";
	
	public string? FindEnumViolation(TypeSymbol argument, TypeSymbol? expected)
	{
		var name = argument.Name;
		return argument switch
		{
			EnumSymbol { IsMatch: true } => $"'{name}' has no tag type",
			EnumSymbol { HasPayload: true } => $"'{name}' has payloads",
			EnumSymbol enumType when expected is not null && GetTagType(enumType) != expected =>
				$"'{name}' has the tag type '{GetTagType(enumType).Name}'",
			EnumSymbol => null,
			TypeParameterSymbol { IsEnum: true } parameter when expected is not null &&
			                                                    GetEnumTag(parameter) != expected =>
				GetEnumTag(parameter) is { } own
					? $"'{name}' has the tag type '{own.Name}'"
					: $"'{name}' may have another tag type",
			TypeParameterSymbol { IsEnum: true } => null,
			_ => $"'{name}' isn't an enum"
		};
	}
	
	private (string Requirement, string Hint)? FindUnmetEnum(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		if (!parameter.Declares(TokenType.KeywordEnum))
			return null;
		
		var substitution = map?.ToDictionary() ?? [];
		substitution[parameter] = argument;
		var expected = GetEnumTag(parameter) is { } tag ? Substitute(tag, substitution) : null;
		return FindEnumViolation(argument, expected) is { } hint ? (DescribeEnumRequirement(expected), hint) : null;
	}
}