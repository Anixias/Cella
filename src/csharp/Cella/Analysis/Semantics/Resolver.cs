using System.Diagnostics;
using Cella.Analysis.Semantics.Symbols;
using Cella.Analysis.Syntax;
using Cella.Analysis.Text;
using Cella.Diagnostics;

namespace Cella.Analysis.Semantics;

/// <summary>
/// Traverses a <see cref="TypedAst"/> to resolve <see cref="ISymbol"/> references and infer types.
/// </summary>
public sealed class Resolver : TypedStatementNode.IVisitor<TypedStatementNode>,
	ExpressionNode.IVisitor<TypedExpressionNode>
{
	private readonly DiagnosticList diagnostics = new();
	
	public readonly Scope globalScope;
	private readonly Stack<Scope> scopeStack = new();
	private Scope CurrentScope => scopeStack.Peek();

	private Resolver(Scope globalScope)
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
		var message = $"Resolver failed with {e.GetType().Name} at {location}:\n\t{e.Message}";
		
		return new Diagnostic(DiagnosticSeverity.Error, source, null, message);
	}

	public static (TypedAst? typedAst, DiagnosticList diagnostics) Resolve(Scope globalScope, TypedAst ast)
	{
		var resolver = new Resolver(globalScope);
		var typedAst = resolver.Resolve(ast);

		return (typedAst, resolver.diagnostics);
	}

	private TypedAst? Resolve(TypedAst ast)
	{
		try
		{
			var root = ast.Root.Accept(this);
			
			if (scopeStack.Count != 1)
				throw new ResolutionException("Invalid operation: Scope stack unbalanced", ast.Source, TextRange.Empty);

			return new TypedAst(root, ast.Source);
		}
		catch (ResolutionException e)
		{
			diagnostics.Add(e);
			return null;
		}
		catch (ResolutionFailedException)
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

	public TypedStatementNode Visit(TypedProgramStatement programStatement)
	{
		var statementScopeStart = scopeStack.Count;
		scopeStack.Push(programStatement.moduleSymbol.Scope);
		var statements = programStatement.statements.Select(s =>
		{
			try
			{
				return s.Accept(this);
			}
			catch (ResolutionException e)
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
				diagnostics.Add(ConvertExceptionToDiagnostic(programStatement.sourceNode.source, e));
				return null!;
			}
		}).ToArray();
		
		while (scopeStack.Count > statementScopeStart)
		{
			scopeStack.Pop();
		}

		if (statements.Contains(null))
			throw new ResolutionFailedException();

		return programStatement.Resolve(statements);
	}

	public TypedStatementNode Visit(TypedEntryStatement entryStatement)
	{
		scopeStack.Push(entryStatement.entrySymbol.Scope);

		// Todo: Parameters and effects
		var body = entryStatement.body.Accept(this);

		scopeStack.Pop();

		return entryStatement.Resolve(body);
	}

	public TypedStatementNode Visit(TypedBlockStatement blockStatement)
	{
		scopeStack.Push(blockStatement.scope);
		
		var statements = blockStatement.statements.Select(s => s.Accept(this));
		
		scopeStack.Pop();
		
		return blockStatement.Resolve(statements);
	}

	public TypedStatementNode Visit(TypedReturnStatement returnStatement)
	{
		var expression = returnStatement.sourceNode.expression?.Accept(this);
		return returnStatement.Resolve(expression);
	}

	public TypedExpressionNode Visit(LambdaExpression lambdaExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(AssignmentExpression assignmentExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(ConditionalExpression conditionalExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(BinaryExpression binaryExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(UnaryExpression unaryExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(CastExpression castExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(AccessExpression accessExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(IndexExpression indexExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(FunctionCallExpression functionCallExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(TokenExpression tokenExpression)
	{
		if (tokenExpression.token.Type.IsLiteral)
		{
			return new TypedLiteralExpression(tokenExpression.token, 
				NativeSymbolHandler.TypeOfValue(tokenExpression.token.Value));
		}

		if (tokenExpression.token.Type == TokenType.Identifier)
		{
			// Todo: Lookup symbol with imports
		}

		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(TypeExpression typeExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(InterpolatedStringExpression interpolatedStringExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(TupleExpression tupleExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(ListExpression listExpression)
	{
		throw new NotImplementedException();
	}

	public TypedExpressionNode Visit(MapExpression mapExpression)
	{
		throw new NotImplementedException();
	}
}