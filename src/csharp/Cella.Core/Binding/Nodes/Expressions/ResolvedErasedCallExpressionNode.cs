using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedErasedCallExpressionNode(ResolvedFunctionCallExpressionNode call) : IResolvedExpressionNode
{
	public ResolvedFunctionCallExpressionNode Call { get; } = call;
	public TypeSymbol Type => Call.Type;
	public IExpressionNode Syntax => Call.Syntax;
}