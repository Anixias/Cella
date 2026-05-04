using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedContinueStatementNode(LabelSymbol? label, IStatementNode syntax) : IResolvedStatementNode
{
	public LabelSymbol? Label { get; } = label;
	public IStatementNode Syntax { get; } = syntax;
}