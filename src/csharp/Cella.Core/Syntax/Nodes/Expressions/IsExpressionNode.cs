using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class IsExpressionNode
(
	Token? mode,
	IExpressionNode value,
	PatternNode pattern,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public Token? Mode { get; } = mode;
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
	IEnumerable<Token?> bindingModes,
	bool hasParentheses,
	SourceLocation sourceLocation
)
{
	public ImmutableArray<Token> TypePath { get; } = typePath.ToImmutableArray();
	public Token CaseName { get; } = caseName;
	public ImmutableArray<Token> Bindings { get; } = bindings.ToImmutableArray();
	public ImmutableArray<Token?> BindingModes { get; } = bindingModes.ToImmutableArray();
	public bool HasParentheses { get; } = hasParentheses;
	public SourceLocation SourceLocation { get; } = sourceLocation;
}