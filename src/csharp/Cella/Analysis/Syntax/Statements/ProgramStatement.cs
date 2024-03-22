using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class ProgramStatement : StatementNode
{
	public readonly IBuffer source;
	public readonly ModuleName moduleName;
	public readonly ImmutableArray<StatementNode> statements;

	public ProgramStatement(IBuffer source, ModuleName moduleName, IEnumerable<StatementNode> statements,
		TextRange range) : base(range)
	{
		this.source = source;
		this.moduleName = moduleName;
		this.statements = statements.ToImmutableArray();
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