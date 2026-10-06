using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class EnumNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	bool isExternal,
	bool isRef,
	ITypeNode? tagType,
	IEnumerable<EnumCaseNode> cases
) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public bool IsExternal { get; } = isExternal;
	public bool IsRef { get; } = isRef;
	public ITypeNode? TagType { get; } = tagType;
	public ImmutableArray<EnumCaseNode> Cases { get; } = cases.ToImmutableArray();
}

public sealed class EnumCaseNode(Token identifier, IEnumerable<FieldNode> payload, IExpressionNode? value)
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<FieldNode> Payload { get; } = payload.ToImmutableArray();
	public IExpressionNode? Value { get; } = value;
}