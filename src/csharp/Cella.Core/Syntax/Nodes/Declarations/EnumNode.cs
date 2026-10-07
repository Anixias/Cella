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
	IEnumerable<EnumCaseNode> cases,
	IEnumerable<IDeclarationNode> members
) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public bool IsExternal { get; } = isExternal;
	public bool IsRef { get; } = isRef;
	public ITypeNode? TagType { get; } = tagType;
	public ImmutableArray<EnumCaseNode> Cases { get; } = cases.ToImmutableArray();
	public ImmutableArray<IDeclarationNode> Members { get; } = members.ToImmutableArray();
	public ImmutableArray<TypeParameterNode> TypeParameters { get; init; } = [];
	public ITypeNode? MatchedType { get; init; }
	public bool IsMatch => MatchedType is not null;
}

public sealed class EnumCaseNode
(
	Token identifier,
	IEnumerable<FieldNode> payload,
	IEnumerable<IExpressionNode> values,
	Token? elseKeyword
)
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<FieldNode> Payload { get; } = payload.ToImmutableArray();
	public ImmutableArray<IExpressionNode> Values { get; } = values.ToImmutableArray();
	public Token? Else { get; } = elseKeyword;
	public IExpressionNode? Value => Values.IsEmpty ? null : Values[0];
}