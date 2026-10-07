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

// TODO Store local function nodes directly and omit from body node?
public sealed class FunctionNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	ReceiverNode? receiver,
	IEnumerable<ParameterNode> parameters,
	ITypeNode? returnType,
	IStatementNode body,
	bool isExternal
) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public ReceiverNode? Receiver { get; } = receiver;
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public IStatementNode Body { get; } = body;
	public bool IsExternal { get; } = isExternal;
	public ImmutableArray<TypeParameterNode> TypeParameters { get; init; } = [];
}

public sealed class ExternalFunctionNode
(
	Token identifier,
	IEnumerable<Token> modifiers,
	IEnumerable<ParameterNode> parameters,
	ITypeNode? returnType,
	bool isVariadic,
	string? origin
) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token? Visibility { get; init; }
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public bool IsVariadic { get; } = isVariadic;
	public string? Origin { get; } = origin;
}