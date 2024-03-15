using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class ProgramStatement : StatementNode
{
	public readonly ModuleName moduleName;
	public readonly ImmutableArray<StatementNode> statements;

	public ProgramStatement(ModuleName moduleName, IEnumerable<StatementNode> statements,
		TextRange range) : base(range)
	{
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