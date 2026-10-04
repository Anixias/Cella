using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedReturnExpressionNode(IResolvedExpressionNode? value, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeSymbol Type => NativeSymbols.Never;
	public IResolvedExpressionNode? Value { get; } = value;
	public IExpressionNode Syntax { get; } = syntax;
}