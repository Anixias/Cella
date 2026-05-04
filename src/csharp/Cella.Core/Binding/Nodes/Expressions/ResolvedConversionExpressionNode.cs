using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedConversionExpressionNode(IResolvedExpressionNode source, Conversion conversion,
	IExpressionNode syntax) : IResolvedExpressionNode
{
	public IResolvedExpressionNode Source { get; } = source;
	public Conversion Conversion { get; } = conversion;
	public TypeSymbol Type => conversion.To;
	public bool IsConstant { get; } = conversion is NativeConversion && source.IsConstant;
	public IExpressionNode Syntax { get; } = syntax;
}