using System.Text;
using Cella.Analysis.Text;

namespace Cella.Analysis.Syntax;

public sealed class AstPrinter : StatementNode.IVisitor, ExpressionNode.IVisitor
{
	private readonly TextWriter textWriter;
	private readonly StringBuilder stringBuilder = new();
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

	public void Visit(ProgramStatement programStatement)
	{
		Write("Program");
		
		var statementCount = programStatement.statements.Length;
		PushIndent(statementCount == 0);
		Write($"Module: {programStatement.moduleName}");

		if (statementCount > 0)
		{
			IsLastChild = true;
			Write("Statements:");
			
			PushIndent();
			for (var i = 0; i < statementCount; i++)
			{
				IsLastChild = i == statementCount - 1;
				programStatement.statements[i].Accept(this);
			}
			PopIndent();
		}
			
		PopIndent();
	}

	public void Visit(ImportStatement importStatement)
	{
		if (importStatement.importToken.alias is { } alias)
		{
			Write($"Import {importStatement.moduleName}::{importStatement.importToken.identifier.Text} as {alias.Text}");
		}
		else
		{
			Write($"Import {importStatement.moduleName}::{importStatement.importToken.identifier.Text}");
		}
	}

	public void Visit(AggregateImportStatement aggregateImportStatement)
	{
		if (aggregateImportStatement.alias is { } aggregateAlias)
		{
			Write($"Import {aggregateImportStatement.moduleName} as {aggregateAlias}");
		}
		else
		{
			Write($"Import {aggregateImportStatement.moduleName}");
		}
		
		PushIndent(false);
		for (var i = 0; i < aggregateImportStatement.importTokens.Length; i++)
		{
			IsLastChild = i == aggregateImportStatement.importTokens.Length - 1;
			
			var importToken = aggregateImportStatement.importTokens[i];
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

	public void Visit(EntryStatement entryStatement)
	{
		Write("Entry:");
		
		PushIndent(false);
		Write($"Name: {entryStatement.name.Text}");
		Write($"Return Type: {entryStatement.returnType?.ToString() ?? "Void"}");

		PrintParameters(entryStatement.parameters.ToArray());
		PrintEffects(entryStatement.effects.ToArray());

		IsLastChild = true;
		Write("Body:");
		PushIndent();
		entryStatement.body.Accept(this);
		PopIndent();
		
		PopIndent();
	}

	public void Visit(BlockStatement blockStatement)
	{
		if (blockStatement.nodes.Length == 0)
		{
			Write("Block (empty)");
			return;
		}
		
		Write("Block:");
		
		PushIndent(false);
		for (var i = 0; i < blockStatement.nodes.Length; i++)
		{
			IsLastChild = i == blockStatement.nodes.Length - 1;
			blockStatement.nodes[i].Accept(this);
		}
		PopIndent();
	}

	public void Visit(ReturnStatement returnStatement)
	{
		if (returnStatement.expression is null)
		{
			Write("Return");
			return;
		}
		
		Write("Return:");
		PushIndent();
		returnStatement.expression.Accept(this);
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

	public void Visit(LambdaExpression lambdaExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(AssignmentExpression assignmentExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(ConditionalExpression conditionalExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(BinaryExpression binaryExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(UnaryExpression unaryExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(CastExpression castExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(AccessExpression accessExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(IndexExpression indexExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(FunctionCallExpression functionCallExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(TokenExpression tokenExpression)
	{
		Write(tokenExpression.token.Value?.ToString() ?? "null");
	}

	public void Visit(TypeExpression typeExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(InterpolatedStringExpression interpolatedStringExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(TupleExpression tupleExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(ListExpression listExpression)
	{
		throw new NotImplementedException();
	}

	public void Visit(MapExpression mapExpression)
	{
		throw new NotImplementedException();
	}
}