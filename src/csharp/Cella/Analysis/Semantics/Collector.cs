using System.Diagnostics;
using Cella.Analysis.Semantics.Symbols;
using Cella.Analysis.Syntax;
using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace Cella.Analysis.Semantics;

/// <summary>
/// Traverses an <see cref="Cella.Analysis.Syntax.Ast"/> to populate a global <see cref="Scope"/>. Only visits
/// <see cref="Cella.Analysis.Syntax.StatementNode"/> instances.
/// </summary>
public sealed class Collector : StatementNode.IVisitor<TypedStatementNode>
{
	private readonly DiagnosticList diagnostics = new();
	
	public readonly Scope globalScope;
	private readonly Stack<Scope> scopeStack = new();
	private Scope CurrentScope => scopeStack.Peek();

	private Collector(Scope globalScope)
	{
		this.globalScope = globalScope;
		scopeStack.Push(globalScope);
	}

	private static Diagnostic ConvertExceptionToDiagnostic(IBuffer source, Exception e)
	{
		var stackTrace = new StackTrace(e, true);
		var stackFrame = stackTrace.GetFrame(0);
		var method = e.TargetSite is null ? "Unknown method" : $"{e.TargetSite.Name}()";
		var location = stackFrame is null ? "Unknown" : $"{method}, line {stackFrame.GetFileLineNumber()}";
		var message = $"Collector failed with {e.GetType().Name} at {location}:\n\t{e.Message}";
		
		return new Diagnostic(DiagnosticSeverity.Error, source, null, message);
	}

	public static (TypedAst? typedAst, DiagnosticList diagnostics) Collect(Scope globalScope, Ast ast)
	{
		var collector = new Collector(globalScope);
		var typedAst = collector.Collect(ast);

		return (typedAst, collector.diagnostics);
	}

	private TypedAst? Collect(Ast ast)
	{
		try
		{
			var root = ast.Root.Accept(this);
			
			if (scopeStack.Count != 1)
				throw new CollectionException("Invalid operation: Scope stack unbalanced", ast.Source, TextRange.Empty);

			return new TypedAst(root, ast.Source);
		}
		catch (CollectionException e)
		{
			diagnostics.Add(e);
			return null;
		}
		catch (CollectionFailedException)
		{
			return null;
		}
		catch (Exception e)
		{
			var diagnostic = ConvertExceptionToDiagnostic(ast.Source, e);
			diagnostics.Add(diagnostic);
			return null;
		}
	}

	public TypedStatementNode Visit(ProgramStatement programStatement)
	{
		var moduleScopeCount = 0;
		ModuleSymbol moduleSymbol = null!;
		var tryLookup = true;
		foreach (var moduleIdentifier in programStatement.moduleName.identifiers)
		{
			Scope scope;
			if (tryLookup && CurrentScope.LookupSymbol(moduleIdentifier.Text, out moduleSymbol!, 
													   out var existingSymbol))
			{
				if (moduleSymbol is null)
				{
					// Todo: Report declaration location of 'existingSymbol'
					throw new CollectionException($"Cannot define a module named '{moduleIdentifier.Text}': " +
					                              $"A symbol with that name is already declared in this scope",
												  moduleIdentifier);
				}

				scope = moduleSymbol.Scope;
			}
			else
			{
				tryLookup = false;
				scope = new Scope(CurrentScope);
				moduleSymbol = new ModuleSymbol(moduleIdentifier.Text, scope);
				CurrentScope.AddSymbol(moduleSymbol);
			}
			
			moduleSymbol.DeclarationLocations.Add(moduleIdentifier.SourceLocation);
			scopeStack.Push(scope);
			moduleScopeCount++;
		}

		var statementScopeStart = moduleScopeCount + 1;
		var statements = programStatement.statements.Select(s =>
		{
			try
			{
				return s.Accept(this);
			}
			catch (CollectionException e)
			{
				diagnostics.Add(e);

				while (scopeStack.Count > statementScopeStart)
				{
					scopeStack.Pop();
				}
				return null!;
			}
			catch (Exception e)
			{
				diagnostics.Add(ConvertExceptionToDiagnostic(programStatement.source, e));
				return null!;
			}
		}).ToArray();
		
		while (moduleScopeCount-- > 0)
			scopeStack.Pop();

		if (statements.Contains(null))
			throw new CollectionFailedException();
		
		return new TypedProgramStatement(moduleSymbol, statements, programStatement);
	}

	public TypedStatementNode Visit(ImportStatement importStatement)
	{
		throw new NotImplementedException();
	}

	public TypedStatementNode Visit(AggregateImportStatement aggregateImportStatement)
	{
		throw new NotImplementedException();
	}

	public TypedStatementNode Visit(EntryStatement entryStatement)
	{
		var scope = new Scope(CurrentScope);
		scopeStack.Push(scope);

		// Todo: Parameters and effects
		var body = entryStatement.body.Accept(this);

		scopeStack.Pop();

		var entrySymbol = new EntrySymbol(entryStatement.name.Text, scope, []);
		CurrentScope.AddSymbol(entrySymbol);
		return new TypedEntryStatement(entrySymbol, body, entryStatement);
	}

	public TypedStatementNode Visit(BlockStatement blockStatement)
	{
		var scope = new Scope(CurrentScope);
		scopeStack.Push(scope);

		var statements = blockStatement.nodes.Select(s => s.Accept(this));

		scopeStack.Pop();
		
		return new TypedBlockStatement(scope, statements, blockStatement);
	}

	public TypedStatementNode Visit(ReturnStatement returnStatement)
	{
		return new TypedReturnStatement(returnStatement);
	}
}