using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class TypePool
{
	private readonly Dictionary<TypeSymbol, ArrayType> _arrayTypes = [];
	// private readonly Dictionary<TypeSymbol, SliceType> _sliceTypes = [];
	// private readonly Dictionary<TypeSymbol, ViewType> _viewTypes = [];
	
	public ArrayType GetArrayType(TypeSymbol elementType)
	{
		if (_arrayTypes.TryGetValue(elementType, out var existing))
			return existing;
		
		var arrayType = new ArrayType(elementType);
		_arrayTypes[elementType] = arrayType;
		return arrayType;
	}
}