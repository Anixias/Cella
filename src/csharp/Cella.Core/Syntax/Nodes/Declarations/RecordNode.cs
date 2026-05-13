using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class RecordNode(Token identifier, IEnumerable<Token> modifiers, IEnumerable<IDeclarationNode> members)
	: IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<IDeclarationNode> Members { get; } = members.ToImmutableArray();
}

public sealed class FieldNode(Token identifier, ITypeNode type, IEnumerable<Token> modifiers,
	IExpressionNode? initializer) : IDeclarationNode
{
	public Token Identifier { get; } = identifier;
	public ITypeNode Type { get; } = type;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public IExpressionNode? Initializer { get; } = initializer;
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
}

public sealed class ConstructorNode(IEnumerable<Token> modifiers, IEnumerable<ParameterNode> parameters,
	IStatementNode body, SourceLocation sourceLocation) : IDeclarationNode
{
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public IStatementNode Body { get; } = body;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}