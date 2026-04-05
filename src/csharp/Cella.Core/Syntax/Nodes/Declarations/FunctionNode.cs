using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes.Declarations;

// TODO Parameters
// TODO Store local function nodes directly and omit from body node?
public sealed class FunctionNode(Token identifier, IEnumerable<Token> modifiers, IEnumerable<ParameterNode> parameters,
	Token? returnType, BlockStatementNode body) : IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public ImmutableArray<ParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public Token? ReturnType { get; } = returnType; // TODO More complex return type
	public BlockStatementNode Body { get; } = body; // TODO Allow single expression as body
}