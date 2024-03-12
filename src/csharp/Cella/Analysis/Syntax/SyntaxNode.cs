﻿using System.Collections.Immutable;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public abstract class SyntaxNode
{
	public interface IVisitor<out T>
	{
		T Visit(ProgramNode programNode);
		T Visit(ImportNode importNode);
		T Visit(AggregateImportNode aggregateImportNode);
		T Visit(DeclarationNode declarationNode);
	}

	public interface IVisitor
	{
		void Visit(ProgramNode programNode);
		void Visit(ImportNode importNode);
		void Visit(AggregateImportNode aggregateImportNode);
		void Visit(DeclarationNode declarationNode);
	}
	
	public readonly TextRange range;

	protected SyntaxNode(TextRange range)
	{
		this.range = range;
	}

	public abstract void Accept(IVisitor visitor);
	public abstract T Accept<T>(IVisitor<T> visitor);
}

public sealed class ProgramNode : SyntaxNode
{
	public readonly ModuleName moduleName;
	public readonly ImmutableArray<SyntaxNode> statements;

	public ProgramNode(ModuleName moduleName, IEnumerable<SyntaxNode> statements,
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

public sealed class ImportNode : SyntaxNode
{
	public readonly ModuleName moduleName;
	public readonly ImportToken importToken;

	public ImportNode(ModuleName moduleName, Token identifier, Token? alias, TextRange range)
		: this(moduleName, new ImportToken(identifier, alias), range)
	{
	}

	public ImportNode(ModuleName moduleName, ImportToken importToken, TextRange range) : base(range)
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

public sealed class AggregateImportNode : SyntaxNode
{
	public readonly ModuleName moduleName;
	public readonly ImmutableArray<ImportToken> importTokens;
	public readonly Token? alias;

	public AggregateImportNode(ModuleName moduleName, IEnumerable<ImportToken> tokens, Token? alias, TextRange range)
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

public sealed class DeclarationNode : SyntaxNode
{
	public readonly Token name;
	public readonly SyntaxNode type;

	public DeclarationNode(Token name, SyntaxNode type, TextRange range) : base(range)
	{
		this.name = name;
		this.type = type;
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