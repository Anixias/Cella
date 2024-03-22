using System.Collections.Immutable;
using Cella.Analysis.Semantics.Symbols;
using Cella.Analysis.Syntax;

namespace Cella.Analysis.Semantics;

public sealed class TypedProgramStatement : TypedStatementNode
{
	public readonly ModuleSymbol moduleSymbol;
	public readonly ImmutableArray<TypedStatementNode> statements;
	public readonly ProgramStatement sourceNode;

	public TypedProgramStatement(ModuleSymbol moduleSymbol, IEnumerable<TypedStatementNode> statements,
		ProgramStatement sourceNode) : base(sourceNode.range)
	{
		this.moduleSymbol = moduleSymbol;
		this.statements = statements.ToImmutableArray();
		this.sourceNode = sourceNode;
	}

	public override void Accept(IVisitor visitor)
	{
		visitor.Visit(this);
	}

	public override T Accept<T>(IVisitor<T> visitor)
	{
		return visitor.Visit(this);
	}

	public TypedProgramStatement Resolve(IEnumerable<TypedStatementNode> statements)
	{
		return new TypedProgramStatement(moduleSymbol, statements, sourceNode);
	}
}