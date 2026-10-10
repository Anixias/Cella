using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedValueParameterExpressionNode(TypeParameterSymbol parameter, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeParameterSymbol Parameter { get; } = parameter;
	public TypeSymbol Type { get; } = parameter.ValueType ?? NativeSymbols.Invalid;
	public IExpressionNode Syntax { get; } = syntax;
}
