using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes.Declarations;

// TODO Parameters
// TODO Store local function nodes directly and omit from body node?
public sealed class FunctionNode(Token identifier, IEnumerable<Token> modifiers, Token returnType,
	BlockStatementNode body) : IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public ImmutableArray<Token> Modifiers { get; } = modifiers.ToImmutableArray();
	public Token Identifier { get; } = identifier;
	public Token ReturnType { get; } = returnType; // TODO More complex return type
	public BlockStatementNode Body { get; } = body; // TODO Allow single expression as body
}