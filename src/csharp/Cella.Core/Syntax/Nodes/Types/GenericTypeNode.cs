using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class GenericTypeNode(SourceLocation sourceLocation, Token identifier,
	IEnumerable<IGenericArgumentNode> arguments) : ITypeNode
{
	public Token Identifier { get; } = identifier;
	public ImmutableArray<IGenericArgumentNode> Arguments { get; } = arguments.ToImmutableArray();
	public SourceLocation SourceLocation { get; } = sourceLocation;
}

public interface IGenericArgumentNode
{
	SourceLocation SourceLocation { get; }
}

// Identifiers are ambiguous, so we extract the identifier directly
public sealed class IdentifierArgumentNode(Token identifier) : IGenericArgumentNode
{
	public Token Identifier { get; } = identifier;
	public SourceLocation SourceLocation { get; } = identifier.SourceLocation;
}

public sealed class TypeArgumentNode(ITypeNode type) : IGenericArgumentNode
{
	public ITypeNode Type { get; } = type;
	public SourceLocation SourceLocation { get; } = type.SourceLocation;
}

public sealed class ExpressionArgumentNode(IExpressionNode expression) : IGenericArgumentNode
{
	public IExpressionNode Expression { get; } = expression;
	public SourceLocation SourceLocation { get; } = expression.SourceLocation;
}