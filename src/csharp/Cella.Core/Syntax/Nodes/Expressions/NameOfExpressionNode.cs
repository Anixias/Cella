using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class NameOfExpressionNode(IExpressionNode name, SourceLocation sourceLocation)
	: IExpressionNode
{
	public IExpressionNode Name { get; } = name;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}