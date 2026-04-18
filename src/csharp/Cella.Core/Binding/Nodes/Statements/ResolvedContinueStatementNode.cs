using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedContinueStatementNode(LabelSymbol? label) : IResolvedStatementNode
{
	public LabelSymbol? Label { get; } = label;
}