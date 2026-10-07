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

public sealed class ReceiverNode(Token? mode, Token self)
{
	public SourceLocation SourceLocation => Self.SourceLocation;
	public Token? Mode { get; } = mode;
	public Token Self { get; } = self;
}

public sealed class TypeParameterNode(Token identifier, Token? constraint)
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public Token? Constraint { get; } = constraint;
}