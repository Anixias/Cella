using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

// TODO Variadic, more complex type
public sealed class ParameterNode(Token? mode, Token identifier, ITypeNode type, IExpressionNode? defaultValue)
	: IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token? Mode { get; } = mode;
	public Token Identifier { get; } = identifier;
	public ITypeNode Type { get; } = type;
	public IExpressionNode? DefaultValue { get; } = defaultValue;
}