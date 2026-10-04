using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class EnumNode(Token identifier, IEnumerable<Token> modifiers, IEnumerable<EnumCaseNode> cases)
	: IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<EnumCaseNode> Cases { get; } = cases.ToImmutableArray();
}

public sealed class EnumCaseNode(Token identifier, IEnumerable<FieldNode> payload)
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<FieldNode> Payload { get; } = payload.ToImmutableArray();
}