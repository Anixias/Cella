using Cella.Analysis.Semantics.Symbols;
using Cella.Analysis.Syntax;

namespace Cella.Analysis.Semantics;

public sealed class TypedEntryStatement : TypedStatementNode
{
	public readonly EntrySymbol entrySymbol;
	public readonly TypedStatementNode body;
	public readonly EntryStatement sourceNode;

	public TypedEntryStatement(EntrySymbol entrySymbol, TypedStatementNode body, EntryStatement sourceNode)
		: base(sourceNode.range)
	{
		this.entrySymbol = entrySymbol;
		this.sourceNode = sourceNode;
		this.body = body;
	}

	public override void Accept(IVisitor visitor)
	{
		visitor.Visit(this);
	}

	public override T Accept<T>(IVisitor<T> visitor)
	{
		return visitor.Visit(this);
	}
}