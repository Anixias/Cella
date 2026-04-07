using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Statements;

public sealed class ResolvedBreakStatementNode(LabelSymbol? label) : IResolvedStatementNode
{
	public LabelSymbol? Label { get; } = label;
}