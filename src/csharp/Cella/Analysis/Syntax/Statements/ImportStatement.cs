using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class ImportStatement : StatementNode
{
	public readonly ModuleName moduleName;
	public readonly ImportToken importToken;

	public ImportStatement(ModuleName moduleName, Token identifier, Token? alias, TextRange range)
		: this(moduleName, new ImportToken(identifier, alias), range)
	{
	}

	public ImportStatement(ModuleName moduleName, ImportToken importToken, TextRange range) : base(range)
	{
		this.moduleName = moduleName;
		this.importToken = importToken;
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