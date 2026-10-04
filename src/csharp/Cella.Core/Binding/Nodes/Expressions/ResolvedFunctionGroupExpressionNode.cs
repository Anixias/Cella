using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFunctionGroupExpressionNode(FunctionGroupType group, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = group;
	public FunctionGroupType Group { get; } = group;
	public IExpressionNode Syntax { get; } = syntax;
}