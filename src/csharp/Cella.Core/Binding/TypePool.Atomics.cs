using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	private readonly Dictionary<FunctionSymbol, List<AtomicRequirement>> _atomicRequirements = [];
	
	public IReadOnlyList<AtomicRequirement> GetAtomicRequirements(FunctionSymbol function) =>
		_atomicRequirements.GetValueOrDefault(function) ?? [];
	
	public bool AddAtomicRequirement(FunctionSymbol function, AtomicRequirement requirement)
	{
		if (!_atomicRequirements.TryGetValue(function, out var list))
			_atomicRequirements[function] = list = [];
		
		if (list.Any(existing => existing.Parameter == requirement.Parameter &&
		                         existing.Operation == requirement.Operation))
			return false;
		
		list.Add(requirement);
		return true;
	}
	
	public void RemoveAtomicRequirement(FunctionSymbol function, AtomicRequirement requirement) =>
		_atomicRequirements.GetValueOrDefault(function)?.Remove(requirement);
	
	public static bool SupportsAtomic(BinaryOperation operation, TypeSymbol type) => operation switch
	{
		BinaryOperation.Addition or BinaryOperation.Subtraction => type is IntegerType or FloatType,
		BinaryOperation.WrappingAddition or BinaryOperation.WrappingSubtraction => type is IntegerType,
		_ => type is IntegerType || type == NativeSymbols.Bool
	};
}