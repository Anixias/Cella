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
public sealed class FunctionNode(Token identifier, IEnumerable<Token> modifiers, IEnumerable<ParameterNode> parameters,
	ITypeNode? returnType, IStatementNode body) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public IStatementNode Body { get; } = body;
}

public sealed class ExternalFunctionNode(Token identifier, IEnumerable<Token> modifiers,
	IEnumerable<ParameterNode> parameters, ITypeNode? returnType, string? origin) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public string? Origin { get; } = origin;
}