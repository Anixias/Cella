using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes.Declarations;

// TODO Variadic, more complex type
public sealed class ParameterNode(Token identifier, Token type, IExpressionNode? defaultValue) : IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public Token Type { get; } = type;
	public IExpressionNode? DefaultValue { get; } = defaultValue;
}