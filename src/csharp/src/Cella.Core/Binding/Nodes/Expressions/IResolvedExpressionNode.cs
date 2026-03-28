using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public interface IResolvedExpressionNode : IResolvedNode
{
	TypeSymbol Type { get; }
}