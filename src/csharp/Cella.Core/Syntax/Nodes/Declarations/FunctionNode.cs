using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public interface IFunctionNode : IDeclarationNode
{
	Token Identifier { get; }
	ImmutableArray<Token> Modifiers { get; }
	ImmutableArray<ParameterNode> Parameters { get; }
	ITypeNode? ReturnType { get; }
}

public sealed class FunctionNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	ReceiverNode? receiver,
	IEnumerable<ParameterNode> parameters,
	ITypeNode? returnType,
	IStatementNode? body,
	bool isExternal
) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public SourceLocation Extent { get; init; } = identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public ReceiverNode? Receiver { get; } = receiver;
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public IStatementNode? Body { get; } = body;
	public Token? BodyArrow { get; init; }
	public bool IsExternal { get; } = isExternal;
	public Token? ExternalName { get; init; }
	public ImmutableArray<TypeParameterNode> TypeParameters { get; init; } = [];
	public WhenClause? When { get; init; }
}

public sealed record WhenClause(Token Keyword, bool IsActive);

public sealed class ExternalFunctionNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	IEnumerable<ParameterNode> parameters,
	ITypeNode? returnType,
	bool isVariadic,
	Token? origin
) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public bool IsVariadic { get; } = isVariadic;
	public string? Origin { get; } = origin?.Text;
	public SourceLocation? OriginLocation { get; } = origin?.SourceLocation;
	public Token? ExternalName { get; init; }
}