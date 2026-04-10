using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedConversionExpressionNode(IResolvedExpressionNode source, Conversion conversion)
	: IResolvedExpressionNode
{
	public IResolvedExpressionNode Source { get; } = source;
	public Conversion Conversion { get; } = conversion;
	public TypeSymbol Type => conversion.To;
	public bool IsConstant { get; } = conversion is NativeConversion && source.IsConstant;
}