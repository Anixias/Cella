using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<TokenType>> _slotKeywords = [];
	
	public void ImplySlotConstraints(ImmutableArray<TypeParameterSymbol> parameters) => ImplySlotConstraints(parameters,
		parameters.Where(static parameter => parameter.IsDerived));
	
	public void ImplySlotConstraints(IReadOnlyList<TypeParameterSymbol> parameters,
		IEnumerable<TypeParameterSymbol> declared)
	{
		foreach (var derived in declared)
		{
			foreach (var owner in parameters.Where(owner => owner != derived))
			{
				foreach (var bound in GetDeclaredBounds(owner))
				{
					var index = bound.Arguments.IndexOf(derived);
					if (index < 0 || index >= bound.Trait.TypeParameters.Length)
						continue;
					
					var map = CreateTraitMap(bound, owner);
					ImplySlot(derived, bound.Trait.TypeParameters[index], map);
				}
			}
		}
	}
	
	private void ImplySlot(TypeParameterSymbol derived, TypeParameterSymbol slot,
		Dictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var bounds = GetDeclaredBounds(slot).Select(bound => SubstituteTrait(bound, map)).ToList();
		if (bounds.Count > 0)
			SetBounds(derived, [..GetDeclaredBounds(derived).Concat(bounds).Distinct()]);
		
		if (_constantBounds.TryGetValue(slot, out var constants))
			foreach (var constant in constants.Select(bound => SubstituteTrait(bound, map)))
				if (!HasConstantBound(derived, constant.Trait))
					AddConstantBound(derived, constant);
		
		var functions = GetFunctionBounds(slot).Select(function => SubstituteFunctionType(function, map)).ToList();
		if (functions.Count > 0)
			SetFunctionBounds(derived, [..GetFunctionBounds(derived).Concat(functions).Distinct()]);
		
		if (GetEnumTag(derived) is null && GetEnumTag(slot) is { } tag)
			SetEnumTag(derived, Substitute(tag, map));
		
		ImmutableArray<TokenType> keywords =
			[..slot.DeclaredConstraints.Concat(GetSlotKeywords(slot)).Except(derived.DeclaredConstraints)];
		
		if (keywords.IsEmpty)
			return;
		
		_slotKeywords[derived] = [..GetSlotKeywords(derived).Concat(keywords).Distinct()];
		derived.InheritedConstraints = [..derived.InheritedConstraints.Concat(keywords).Distinct()];
	}
	
	private ImmutableArray<TokenType> GetSlotKeywords(TypeParameterSymbol parameter) =>
		_slotKeywords.GetValueOrDefault(parameter, []);
}