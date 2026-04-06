using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes.Declarations;

public interface IFunctionNode : IDeclarationNode
{
	Token Identifier { get; }
	ImmutableArray<Token> Modifiers { get; }
	ImmutableArray<ParameterNode> Parameters { get; }
	Token? ReturnType { get; } // TODO More complex return type
}

// TODO Store local function nodes directly and omit from body node?
public sealed class FunctionNode(Token identifier, IEnumerable<Token> modifiers, IEnumerable<ParameterNode> parameters,
	Token? returnType, IStatementNode body) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public Token? ReturnType { get; } = returnType; // TODO More complex return type
	public IStatementNode Body { get; } = body;
}

public sealed class ExternalFunctionNode(Token identifier, IEnumerable<Token> modifiers,
	IEnumerable<ParameterNode> parameters, Token? returnType, string? origin) : IFunctionNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public Token? ReturnType { get; } = returnType; // TODO More complex return type
	public string? Origin { get; } = origin;
}