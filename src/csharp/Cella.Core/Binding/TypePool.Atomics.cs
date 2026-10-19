using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

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
	
	public TypeSymbol? FindAtomicAmountType(BinaryOperation operation, TypeSymbol type) => OperatorRegistry
		.GetBinaryCandidates(GetCompoundOperator(operation))
		.OfType<NativeImpl>()
		.FirstOrDefault(impl => impl.ParameterTypes[0] == type && impl.ReturnType == type)?
		.ParameterTypes[1];
	
	private static TokenType GetCompoundOperator(BinaryOperation operation) => operation switch
	{
		BinaryOperation.Addition => TokenType.OpPlusEqual,
		BinaryOperation.Subtraction => TokenType.OpMinusEqual,
		BinaryOperation.WrappingAddition => TokenType.OpPlusPercentEqual,
		BinaryOperation.WrappingSubtraction => TokenType.OpMinusPercentEqual,
		BinaryOperation.BitwiseAnd => TokenType.OpAmpersandEqual,
		BinaryOperation.BitwiseOr => TokenType.OpBarEqual,
		_ => TokenType.OpHatEqual
	};
}