using System.Text;

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
		
		PushIndent(false);
		Write($"Module: {programNode.moduleName}");

		var statementCount = programNode.statements.Length;
		var importCount = programNode.imports.Length;
		if (importCount > 0)
		{
			IsLastChild = statementCount == 0;
			Write("Imports:");
			
			PushIndent();
			for (var i = 0; i < importCount; i++)
			{
				IsLastChild = i == importCount - 1;
				programNode.imports[i].Accept(this);
			}
			PopIndent();
		}

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
		if (importNode.alias is { } alias)
		{
			Write($"{importNode.moduleName}::{importNode.identifier.Text} as {alias.Text}");
		}
		else
		{
			Write($"{importNode.moduleName}::{importNode.identifier.Text}");
		}
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