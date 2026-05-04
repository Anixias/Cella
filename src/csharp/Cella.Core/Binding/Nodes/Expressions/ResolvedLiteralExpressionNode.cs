using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedLiteralExpressionNode(TypeSymbol type, object? value, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public object? Value { get; } = value;
	public bool IsConstant => true;
	public IExpressionNode Syntax { get; } = syntax;
}