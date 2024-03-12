using System.Text;
using Cella.Analysis.Text;

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

	public void Visit(EntryNode entryNode)
	{
		Write("Entry:");
		
		PushIndent(false);
		Write($"Name: {entryNode.name.Text}");
		Write($"Return Type: {entryNode.returnType?.ToString() ?? "Void"}");

		PrintParameters(entryNode.parameters.ToArray());
		PrintEffects(entryNode.effects.ToArray());

		IsLastChild = true;
		Write("Body:");
		PushIndent();
		entryNode.body.Accept(this);
		PopIndent();
		
		PopIndent();
	}

	public void Visit(BlockNode blockNode)
	{
		if (blockNode.nodes.Length == 0)
		{
			Write("Block (empty)");
			return;
		}
		
		Write("Block:");
		
		PushIndent(false);
		for (var i = 0; i < blockNode.nodes.Length; i++)
		{
			IsLastChild = i == blockNode.nodes.Length - 1;
			blockNode.nodes[i].Accept(this);
		}
		PopIndent();
	}

	private void PrintParameters(SyntaxParameter[] parameters)
	{
		if (parameters.Length == 0)
			return;
		
		Write("Parameters:");
		
		PushIndent(false);
		for (var i = 0; i < parameters.Length; i++)
		{
			IsLastChild = i == parameters.Length - 1;
			var parameter = parameters[i];
			Write($"{parameter.identifier.Text}:");

			switch (parameter)
			{
				case SyntaxParameter.Self self:
				{
					PushIndent();

					if (self.modifiers.Length == 0)
					{
						Write("Modifiers: (none)");
					}
					else
					{
						Write("Modifiers:");
						for (var j = 0; j < self.modifiers.Length; j++)
						{
							IsLastChild = j == self.modifiers.Length - 1;
							Write($"{self.modifiers[i].Text}:");
						}
					}
					
					PopIndent();
					break;
				}

				case SyntaxParameter.Variable variable:
				{
					PushIndent(false);
					
					Write($"Type: {variable.type}");
					
					if (variable.modifiers.Length == 0)
					{
						Write("Modifiers: (none)");
					}
					else
					{
						Write("Modifiers:");
						for (var j = 0; j < variable.modifiers.Length; j++)
						{
							IsLastChild = j == variable.modifiers.Length - 1;
							Write($"{variable.modifiers[i].Text}:");
						}
					}

					IsLastChild = variable.defaultValue is null;
					Write($"Variadic: {variable.isVariadic}");

					if (variable.defaultValue is not null)
					{
						IsLastChild = true;
						Write("Default Value: ");
						
						PushIndent();
						variable.defaultValue.Accept(this);
						PopIndent();
					}

					PopIndent();
					break;
				}
			}
		}
		PopIndent();
	}

	private void PrintEffects(Token[] effects)
	{
		if (effects.Length == 0)
			return;

		Write("Effects:");
		
		PushIndent(false);
		for (var i = 0; i < effects.Length; i++)
		{
			IsLastChild = i == effects.Length - 1;
			Write(effects[i].Text);
		}
		PopIndent();
	}
}