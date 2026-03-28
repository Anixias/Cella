using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Expressions;

public sealed class ResolvedLiteralExpressionNode(TypeSymbol type, object? value) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public object? Value { get; } = value;
}