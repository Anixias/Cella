using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class VarExpressionNode(Token identifier) : IExpressionNode
{
	public Token Identifier { get; } = identifier;
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
}