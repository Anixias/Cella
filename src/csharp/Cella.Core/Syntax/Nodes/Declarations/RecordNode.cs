using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class RecordNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	bool isRef,
	IEnumerable<IDeclarationNode> members
) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public bool IsRef { get; } = isRef;
	public ImmutableArray<IDeclarationNode> Members { get; } = members.ToImmutableArray();
	public ImmutableArray<TypeParameterNode> TypeParameters { get; init; } = [];
}

public sealed class FieldNode
(
	Token identifier,
	ITypeNode type,
	IEnumerable<Token> modifiers
) : IDeclarationNode
{
	public Token Identifier { get; } = identifier;
	public ITypeNode Type { get; } = type;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public Token? WriteVisibility { get; init; }
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
}

public sealed class ConstructorNode
(
	Token keyword,
	IEnumerable<Token> modifiers,
	IEnumerable<ParameterNode> parameters,
	IStatementNode body,
	SourceLocation sourceLocation
) : IDeclarationNode
{
	public Token Keyword { get; } = keyword;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public IStatementNode Body { get; } = body;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}

public sealed class DestructorNode
(
	Token keyword,
	IStatementNode body,
	SourceLocation sourceLocation
) : IDeclarationNode
{
	public Token Keyword { get; } = keyword;
	public IStatementNode Body { get; } = body;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}