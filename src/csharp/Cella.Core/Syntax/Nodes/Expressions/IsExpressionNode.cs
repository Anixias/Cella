using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class IsExpressionNode(IExpressionNode value, PatternNode pattern, SourceLocation sourceLocation)
	: IExpressionNode
{
	public IExpressionNode Value { get; } = value;
	public PatternNode Pattern { get; } = pattern;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => false;
}

public sealed class PatternNode
(
	IEnumerable<Token> typePath,
	Token caseName,
	IEnumerable<Token> bindings,
	bool hasParentheses,
	SourceLocation sourceLocation
)
{
	public ImmutableArray<Token> TypePath { get; } = typePath.ToImmutableArray();
	public Token CaseName { get; } = caseName;
	public ImmutableArray<Token> Bindings { get; } = bindings.ToImmutableArray();
	public bool HasParentheses { get; } = hasParentheses;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}