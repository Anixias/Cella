using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedContinueExpressionNode(LabelSymbol? label, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeSymbol Type => NativeSymbols.Never;
	public LabelSymbol? Label { get; } = label;
	public IExpressionNode Syntax { get; } = syntax;
}