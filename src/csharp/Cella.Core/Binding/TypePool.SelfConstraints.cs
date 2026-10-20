using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public static Dictionary<TypeParameterSymbol, TypeSymbol> CreateTraitMap(TraitType trait, TypeSymbol self)
	{
		var map = CreateMap(trait.Trait.TypeParameters, trait.Arguments);
		map[trait.Trait.Self] = self;
		return map;
	}
	
	public void InheritSelfConstraints(TypeParameterSymbol parameter)
	{
		var traits = GetInheritedTraits(parameter);
		parameter.InheritedConstraints =
		[
			..GetSelfConstraints(traits)
				.Except(parameter.DeclaredConstraints)
				.Distinct()
		];
		
		var declaredFunctions = GetFunctionBounds(parameter);
		var functions = traits
			.SelectMany(trait => GetFunctionBounds(trait.Trait.Self)
				.Select(function => SubstituteFunctionType(function, CreateTraitMap(trait, parameter))))
			.Except(declaredFunctions)
			.ToList();
		
		if (functions.Count > 0)
			SetFunctionBounds(parameter, [..declaredFunctions, ..functions.Distinct()]);
		
		var declaredParameters = GetParameterBounds(parameter);
		var parameters = traits
			.SelectMany(trait => GetParameterBounds(trait.Trait.Self)
				.Select(bound => CreateTraitMap(trait, parameter).GetValueOrDefault(bound, bound)))
			.OfType<TypeParameterSymbol>()
			.Except(declaredParameters)
			.ToList();
		
		if (parameters.Count > 0)
			SetParameterBounds(parameter, [..declaredParameters, ..parameters.Distinct()]);
	}
	
	public void InheritSelfConstructors(TypeParameterSymbol parameter)
	{
		var constructors = GetConstructorBounds(parameter).ToList();
		var count = constructors.Count;
		foreach (var trait in GetInheritedTraits(parameter))
		{
			var map = CreateTraitMap(trait, parameter);
			foreach (var bound in GetConstructorBounds(trait.Trait.Self))
			{
				var signature = SubstituteSignature(bound.Signature, map);
				if (constructors.Any(existing => MatchesConstructor(existing.Signature, signature)))
					continue;
				
				var symbol = new FunctionSymbol($"{parameter.Name}.new", bound.Symbol.Syntax, Visibility.Public, null,
					bound.Symbol.Parameters, FunctionKind.Constructor)
				{
					TypeParameters =
					[
						parameter,
						..signature.ParameterTypes.SelectMany(FindTypeParameters).Where(found => found != parameter)
							.Distinct()
					]
				};
				
				constructors.Add(new FunctionInfo(null, symbol, signature, null, null, bound.File));
			}
		}
		
		if (constructors.Count > count)
			SetConstructorBounds(parameter, [..constructors]);
	}
	
	public IEnumerable<TokenType> GetSelfConstraints(TraitType trait, TypeParameterSymbol parameter) =>
		GetSelfConstraints(AddRequiredTraits([trait], parameter)).Distinct();
	
	private static IEnumerable<TokenType> GetSelfConstraints(IEnumerable<TraitType> traits) =>
		traits.SelectMany(static trait => trait.Trait.Self.DeclaredConstraints);
	
	public void ResetFacts() => _facts.Clear();
	
	private List<TraitType> GetInheritedTraits(TypeParameterSymbol parameter) =>
		[..AddRequiredTraits(GetDeclaredBounds(parameter), parameter).Where(trait => trait.Trait.Self != parameter)];
}