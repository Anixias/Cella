using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedArrayBuildExpressionNode
(
	ArrayType type,
	IResolvedExpressionNode make,
	IResolvedExpressionNode length,
	FunctionType makeType,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode Make { get; } = make;
	public IResolvedExpressionNode Length { get; } = length;
	public FunctionType MakeType { get; } = makeType;
	public IExpressionNode Syntax { get; } = syntax;
}
