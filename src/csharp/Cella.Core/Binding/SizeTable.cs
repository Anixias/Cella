using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class SizeTable
{
	private readonly Dictionary<TypeSymbol, ISize> _sizes = new()
	{
		[NativeSymbols.Invalid] = StorageSize.Const(0),
		[NativeSymbols.UntypedInteger] = StorageSize.Const(0),
		[NativeSymbols.UntypedFloat] = StorageSize.Const(0),
		[NativeSymbols.VoidPtr] = StorageSize.Ptr,
		[NativeSymbols.Void] = StorageSize.Const(0),
		[NativeSymbols.Int8] = StorageSize.Const(1),
		[NativeSymbols.Int16] = StorageSize.Const(2),
		[NativeSymbols.Int32] = StorageSize.Const(4),
		[NativeSymbols.Int64] = StorageSize.Const(8),
		[NativeSymbols.Int128] = StorageSize.Const(16),
		[NativeSymbols.IntSize] = StorageSize.Ptr,
		[NativeSymbols.UInt8] = StorageSize.Const(1),
		[NativeSymbols.UInt16] = StorageSize.Const(2),
		[NativeSymbols.UInt32] = StorageSize.Const(4),
		[NativeSymbols.UInt64] = StorageSize.Const(8),
		[NativeSymbols.UInt128] = StorageSize.Const(16),
		[NativeSymbols.UIntSize] = StorageSize.Ptr,
		[NativeSymbols.Float32] = StorageSize.Const(4),
		[NativeSymbols.Float64] = StorageSize.Const(8),
		[NativeSymbols.Char] = StorageSize.Const(4),
		[NativeSymbols.Bool] = StorageSize.Const(1),
		[NativeSymbols.Str] = StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr), // usize length + ptr data
		[NativeSymbols.CStr] = StorageSize.Ptr
	};
	
	private readonly Dictionary<TypeSymbol, Func<ISize>> _deferredSizes = [];
	
	public void Register(TypeSymbol type, ISize size) => _sizes[type] = size;
	public void Register(TypeSymbol type, Func<ISize> computeSize) => _deferredSizes[type] = computeSize;
	
	public ISize GetSize(TypeSymbol type) =>
		TryGetSize(type) ?? throw new KeyNotFoundException($"No size is registered for type '{type.Name}'");
	
	public ISize? TryGetSize(TypeSymbol type)
	{
		if (_sizes.TryGetValue(type, out var size))
			return size;
		
		if (!_deferredSizes.Remove(type, out var computeSize))
			return null;
		
		size = computeSize();
		_sizes[type] = size;
		return size;
	}
}