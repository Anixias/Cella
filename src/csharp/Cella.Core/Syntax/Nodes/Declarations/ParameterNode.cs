using System.Collections.Immutable;
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

public sealed class TypeParameterNode
(
	Token identifier,
	IEnumerable<Token> keywords,
	IEnumerable<ITypeNode> traits,
	IEnumerable<ConstructorConstraintNode> constructors
)
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Keywords { get; } = keywords.ToImmutableArray();
	public ImmutableArray<ITypeNode> Traits { get; } = traits.ToImmutableArray();
	public ImmutableArray<ConstructorConstraintNode> Constructors { get; } = constructors.ToImmutableArray();
}

public sealed class ConstructorConstraintNode
(
	Token keyword,
	IEnumerable<Token?> modes,
	IEnumerable<ITypeNode> types,
	Token closeParen
) : IDeclarationNode
{
	public Token Keyword { get; } = keyword;
	public ImmutableArray<Token?> Modes { get; } = modes.ToImmutableArray();
	public ImmutableArray<ITypeNode> Types { get; } = types.ToImmutableArray();
	
	public SourceLocation SourceLocation { get; } = keyword.SourceLocation with
	{
		Range = keyword.SourceLocation.Range.Join(closeParen.SourceLocation.Range)
	};
}