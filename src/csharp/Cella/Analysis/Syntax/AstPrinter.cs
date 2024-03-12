﻿using System.Text;

namespace Cella.Analysis.Syntax;

public sealed class AstPrinter : SyntaxNode.IVisitor
{
	private readonly TextWriter textWriter;
	private StringBuilder stringBuilder = new();
	private int indent;
	private readonly List<bool> finalChildIndents = [];

	private bool IsLastChild
	{
		get => finalChildIndents[indent - 1];
		set => finalChildIndents[indent - 1] = value;
	}

	private AstPrinter(TextWriter textWriter)
	{
		this.textWriter = textWriter;
	}

	public static void Print(Ast ast, TextWriter? textWriter = null)
	{
		var printer = new AstPrinter(textWriter ?? Console.Out);
		printer.PrintAst(ast);
	}

	private void PushIndent(bool isLastChild = true)
	{
		indent++;
		finalChildIndents.Add(isLastChild);
	}

	private void PopIndent()
	{
		indent--;
		finalChildIndents.RemoveAt(indent);
	}

	private void Write(string text)
	{
		for (var i = 0; i < indent - 1; i++)
		{
			stringBuilder.Append(finalChildIndents[i] ? "    " : "│   ");
		}

		if (indent > 0)
		{
			stringBuilder.Append(IsLastChild ? "└── " : "├── ");
		}

		stringBuilder.Append(text);
		stringBuilder.AppendLine();
	}

	private void PrintAst(Ast ast)
	{
		ast.Root.Accept(this);
		textWriter.WriteLine(stringBuilder.ToString());
	}

	public void Visit(ProgramNode programNode)
	{
		Write("Program");
		
		var statementCount = programNode.statements.Length;
		PushIndent(statementCount == 0);
		Write($"Module: {programNode.moduleName}");

		if (statementCount > 0)
		{
			IsLastChild = true;
			Write("Statements:");
			
			PushIndent();
			for (var i = 0; i < statementCount; i++)
			{
				IsLastChild = i == statementCount - 1;
				programNode.statements[i].Accept(this);
			}
			PopIndent();
		}
			
		PopIndent();
	}

	public void Visit(ImportNode importNode)
	{
		if (importNode.importToken.alias is { } alias)
		{
			Write($"Import {importNode.moduleName}::{importNode.importToken.identifier.Text} as {alias.Text}");
		}
		else
		{
			Write($"Import {importNode.moduleName}::{importNode.importToken.identifier.Text}");
		}
	}

	public void Visit(AggregateImportNode aggregateImportNode)
	{
		if (aggregateImportNode.alias is { } aggregateAlias)
		{
			Write($"Import {aggregateImportNode.moduleName} as {aggregateAlias}");
		}
		else
		{
			Write($"Import {aggregateImportNode.moduleName}");
		}
		
		PushIndent(false);
		for (var i = 0; i < aggregateImportNode.importTokens.Length; i++)
		{
			IsLastChild = i == aggregateImportNode.importTokens.Length - 1;
			
			var importToken = aggregateImportNode.importTokens[i];
			if (importToken.alias is { } alias)
			{
				Write($"{importToken.identifier.Text} as {alias.Text}");
			}
			else
			{
				Write($"{importToken.identifier.Text}");
			}
		}

		PopIndent();
	}

	public void Visit(DeclarationNode declarationNode)
	{
		Write("Declaration:");
		
		PushIndent(false);
		Write($"Name: {declarationNode.name.Text}");
		
		IsLastChild = true;
		Write("Type:");
		declarationNode.type.Accept(this);
		
		PopIndent();
	}
}