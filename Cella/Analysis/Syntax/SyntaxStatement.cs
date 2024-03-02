using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class SyntaxStatement : IAstNode
{
	public interface IVisitor<out T>
	{
		T Visit(ProgramStatement programStatement);
	}

	public interface IVisitor
	{
		void Visit(ProgramStatement programStatement);
	}
	
	public readonly TextRange range;

	protected SyntaxStatement(TextRange range)
	{
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}

public sealed class ProgramStatement : SyntaxStatement
{
	public readonly ModuleName? moduleName;
	public readonly ImmutableArray<SyntaxStatement> importStatements;
	public readonly ImmutableArray<SyntaxStatement> topLevelStatements;

	public ProgramStatement(ModuleName? moduleName, IEnumerable<SyntaxStatement> importStatements,
		IEnumerable<SyntaxStatement> topLevelStatements, TextRange range) : base(range)
	{
		this.moduleName = moduleName;
		this.importStatements = importStatements.ToImmutableArray();
		this.topLevelStatements = topLevelStatements.ToImmutableArray();
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