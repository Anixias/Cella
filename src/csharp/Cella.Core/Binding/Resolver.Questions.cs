using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private const string QuestionName = "?";
	
	private readonly Dictionary<FunctionSymbol, bool> _questionBodies = [];
	private readonly Dictionary<FunctionSymbol, IResolvedExpressionNode> _questionDeclarations = [];
	private readonly Dictionary<FunctionSymbol, IResolvedStatementNode> _questionStatements = [];
	private readonly HashSet<FunctionSymbol> _inlinedQuestions = [];
	
	public IResolvedExpressionNode Visit(QuestionExpressionNode node)
	{
		var operand = VisitNode(node.Value, null);
		if (IsInvalid(operand))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (operand.Type is NeverType)
			return operand;
		
		operand = Decay(operand);
		var declared = GetMethods(operand.Type, QuestionName).Where(static method => method.HasReceiver).ToList();
		if (declared.Count == 0)
			return Error(node, $"Cannot use '?' on '{operand.Type.Name}'", CurrentTargetType);
		
		if (declared.FirstOrDefault(method => CanAccess(operand.Type, method.Function)) is not { } method)
			return Error(node, ReportHiddenMember(node.Op.SourceLocation, QuestionName,
				declared.Select(static method => method.Function)), CurrentTargetType);
		
		var info = GetFunctionInfo(method, operand.Type);
		var function = info.Symbol;
		if (info.Signature.GetMode(0) != ParameterMode.Value || info.Signature.ReturnType is InvalidType ||
		    _signatures.FindInlineContext(function) is not { } declaration ||
		    FindQuestionSyntax(function) is not { } body || !IsSoundQuestionBody(function, declaration, body))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var context = CurrentResolutionContext;
		var caller = context.InlineReturn is { } outer ? outer.Caller : context.ContainingFunction;
		if (caller is null && context.InlineReturn is null)
			return Error(node, "Cannot use '?' outside functions", CurrentTargetType);
		
		if (!_inlinedQuestions.Add(function))
			return Error(node, $"Cannot use '?' on '{operand.Type.Name}' in its own '?' operator", CurrentTargetType);
		
		var self = new LocalVariableSymbol(new(TokenType.KeywordSelf, node.Op.SourceLocation, "self"), operand.Type,
			false) { IsSynthesized = true };
		
		var scope = new Scope();
		scope.Define(self);
		if (!info.TypeArguments.IsDefaultOrEmpty)
		{
			foreach (var (parameter, argument) in TypePool.CreateMap(function.TypeParameters, info.TypeArguments))
				scope.Define(parameter.Name, argument);
		}
		
		_resolutionContexts.Push(WithResolverState(declaration) with
		{
			ContainingFunction = info,
			LocalScope = scope,
			InlineReturn = context.InlineReturn ?? new(caller, node.Op.SourceLocation, operand.Type),
			RecordName = null
		});
		
		var captured = new List<Diagnostic>();
		var previous = Diagnostics.Redirect(captured);
		var resolvedBody = body is IExpressionNode expression ? VisitNode(expression, info.Signature.ReturnType) : null;
		var resolvedStatement = body is IStatementNode statement ? VisitNode(statement) : null;
		Diagnostics.Redirect(previous);
		Diagnostics.AddRange(captured.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
		_resolutionContexts.Pop();
		_inlinedQuestions.Remove(function);
		
		return new ResolvedQuestionExpressionNode(info, self, CreateReceiver(operand, info), resolvedBody,
			resolvedStatement, node);
	}
	
	private ResolvedFunctionNode? ResolveQuestionDeclaration(FunctionSymbol function)
	{
		if (_signatures.FindInlineContext(function) is not { } declaration ||
		    FindQuestionSyntax(function) is not { } body)
			return null;
		
		IsSoundQuestionBody(function, declaration, body);
		if (_questionStatements.TryGetValue(function, out var resolvedStatement))
			return new ResolvedFunctionNode(_signatures.GetFunctionInfo(function), resolvedStatement,
				(FunctionNode)function.Syntax) { IsInline = true };
		
		if (!_questionDeclarations.TryGetValue(function, out var resolved))
			return null;
		
		var syntax = (FunctionNode)function.Syntax;
		var statement = (ExpressionStatementNode)syntax.Body!;
		var returned = new ResolvedReturnExpressionNode(resolved, statement.ExpressionNode);
		return new ResolvedFunctionNode(_signatures.GetFunctionInfo(function),
			new ResolvedExpressionStatementNode(returned, statement), syntax) { IsInline = true };
	}
	
	private static ISyntaxNode? FindQuestionSyntax(FunctionSymbol function) => function.Syntax switch
	{
		FunctionNode { Body: ExpressionStatementNode { ExpressionNode: ReturnExpressionNode { Value: { } value } } } =>
			value,
		FunctionNode { ReturnType: null, Body: { } body } => body,
		_ => null
	};
	
	private ResolutionContext WithResolverState(ResolutionContext declaration)
	{
		var current = CurrentResolutionContext;
		return declaration with
		{
			Diagnostics = Diagnostics,
			ExtSignatureTypes = current.ExtSignatureTypes,
			Containment = current.Containment,
			EvaluateConstant = current.EvaluateConstant,
			FindValueParameterUse = current.FindValueParameterUse,
			MarkUsed = current.MarkUsed,
			GenericTypes = current.GenericTypes,
			RecordUndo = current.RecordUndo,
			TrackInstance = current.TrackInstance,
			DeferConstraintCheck = current.DeferConstraintCheck,
			Capture = null
		};
	}
	
	private bool IsSoundQuestionBody(FunctionSymbol function, ResolutionContext declaration, ISyntaxNode body)
	{
		if (!_signatures.IsLocal(function))
			return true;
		
		if (_questionBodies.TryGetValue(function, out var isSound))
			return isSound;
		
		_questionBodies[function] = true;
		using var frame = Own(function);
		var info = _signatures.GetFunctionInfo(function);
		var previous = Diagnostics.Redirect(null);
		var errors = Diagnostics.ErrorCount;
		
		_resolutionContexts.Push(WithResolverState(declaration) with
		{
			ContainingFunction = info,
			LocalScope = info.Scope,
			TypeParameters = function.DeclaredTypeParameters,
			InlineReturn = new(null, body.SourceLocation)
		});
		
		if (body is IExpressionNode expression)
			_questionDeclarations[function] = InferLocals(() => VisitNode(expression, info.Signature.ReturnType));
		else if (body is IStatementNode statement)
			_questionStatements[function] = InferLocals(() => VisitNode(statement));
		
		_resolutionContexts.Pop();
		
		isSound = Diagnostics.ErrorCount == errors;
		Diagnostics.Redirect(previous);
		_questionBodies[function] = isSound;
		return isSound;
	}
	
	private IResolvedExpressionNode ResolveInlineReturn(ReturnExpressionNode node, InlineReturn inline)
	{
		if (inline.Caller is null && node.Value is { } guessed &&
		    CurrentResolutionContext.ContainingType is EnumSymbol owner)
		{
			var guess = OpenCheckpoint();
			var resolvedGuess = VisitNode(guessed, owner);
			if (!guess.HasErrors)
			{
				Rollback(guess);
				return new ResolvedReturnExpressionNode(resolvedGuess, node) { HasUnknownTarget = true };
			}
			
			Rollback(guess);
		}
		
		var checkpoint = OpenCheckpoint();
		if (inline.Caller is not { } caller)
		{
			var discarded = node.Value is { } value ? VisitDiscarded(value) : null;
			var isBroken = checkpoint.HasErrors;
			Rollback(checkpoint);
			if (isBroken && discarded is not null)
				discarded = new ResolvedInvalidExpressionNode(discarded.Syntax) { Parts = [discarded] };
			
			return new ResolvedReturnExpressionNode(discarded, node) { HasUnknownTarget = true };
		}
		
		var returnType = caller.Signature.ReturnType;
		var resolved = node.Value is not { } expression ? null
			: returnType is NeverType || returnType == NativeSymbols.Void ? VisitDiscarded(expression)
			: VisitNode(expression, returnType);
		
		var isMismatched = returnType is NeverType ? resolved is not { Type: NeverType }
			: returnType == NativeSymbols.Void ? resolved is not (null or { Type: NeverType })
			: resolved is null;
		
		if (!checkpoint.HasErrors && !isMismatched)
		{
			Commit(checkpoint);
			return new ResolvedReturnExpressionNode(resolved, node) { IsInline = true };
		}
		
		Rollback(checkpoint);
		var function = DiagnosticReporter.DescribeFunction(caller.Symbol);
		var subject = DiagnosticReporter.DescribeFunction(caller.Symbol, true);
		Diagnostics.Add(new(DiagnosticSeverity.Error, inline.Site,
			$"Cannot use '?' on '{inline.Operand?.Name}' in {function}")
		{
			Hints =
			[
				returnType is NeverType ? $"{subject} never returns"
				: returnType == NativeSymbols.Void ? $"{subject} doesn't return a value"
				: $"{subject} returns '{returnType.Name}'"
			]
		});
		
		return new ResolvedInvalidExpressionNode(node);
	}
}