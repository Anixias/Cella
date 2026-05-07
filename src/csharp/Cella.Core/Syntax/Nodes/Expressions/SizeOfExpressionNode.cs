using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class SizeOfExpressionNode(Token token, ITypeNode type, SourceLocation sourceLocation) : IExpressionNode
{
	public Token Token { get; } = token;
	public ITypeNode Type { get; } = type;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => true;
}