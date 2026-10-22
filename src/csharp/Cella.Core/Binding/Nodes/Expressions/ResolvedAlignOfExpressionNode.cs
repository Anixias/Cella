using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedAlignOfExpressionNode(TypeSymbol target, IExpressionNode syntax) : IResolvedExpressionNode
{
	public TypeSymbol Target { get; } = target;
	public TypeSymbol Type { get; } = NativeSymbols.UIntSize;
	public IExpressionNode Syntax { get; } = syntax;
	
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global",
		Justification = "Read by the semantic model's walk over resolved nodes")]
	public ImmutableArray<IResolvedExpressionNode> Parts { get; init; } = [];
}