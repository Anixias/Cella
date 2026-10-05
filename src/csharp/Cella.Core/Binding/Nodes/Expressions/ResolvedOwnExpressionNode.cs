using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedOwnExpressionNode(IResolvedExpressionNode value, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public IResolvedExpressionNode Value { get; } = value;
	public TypeSymbol Type { get; } = value.Type;
	public IExpressionNode Syntax { get; } = syntax;
}