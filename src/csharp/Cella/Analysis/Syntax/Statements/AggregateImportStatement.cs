using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class AggregateImportStatement : StatementNode
{
	public readonly ModuleName moduleName;
	public readonly ImmutableArray<ImportToken> importTokens;
	public readonly Token? alias;

	public AggregateImportStatement(ModuleName moduleName, IEnumerable<ImportToken> tokens, Token? alias, TextRange range)
		: base(range)
	{
		this.moduleName = moduleName;
		this.importTokens = tokens.ToImmutableArray();
		this.alias = alias;
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