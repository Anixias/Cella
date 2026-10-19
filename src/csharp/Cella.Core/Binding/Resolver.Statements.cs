using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public IResolvedStatementNode Visit(BlockStatementNode node)
	{
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(
			node.StatementNodes.OfType<VarStatementNode>().Select(static v => v.Identifier),
			static name => $"'{name}' is declared more than once in this scope"));
		
		var statements = new List<IResolvedStatementNode>(node.StatementNodes.Length);
		var scope = CurrentScope?.CreateChild();
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		_resolutionContexts.Push(resolutionContext);
		foreach (var child in node.StatementNodes)
			statements.Add(VisitNode(child));
		
		_resolutionContexts.Pop();
		
		return new ResolvedBlockStatementNode(statements, node);
	}
	
	public IResolvedExpressionNode Visit(ReturnExpressionNode node)
	{
		if (CurrentResolutionContext.InlineReturn is { } inline)
			return ResolveInlineReturn(node, inline);
		
		if (CurrentResolutionContext.ContainingFunction is not { } function)
			return Error(node, "Cannot use 'ret' outside functions", null);
		
		if (function.Signature.ReturnType is NeverType)
		{
			var value = node.Value is { } returned ? VisitDiscarded(returned) : null;
			return value switch
			{
				{ Type: NeverType } => new ResolvedReturnExpressionNode(value, node),
				{ Type: InvalidType } => new ResolvedInvalidExpressionNode(node),
				_ => Error(node, $"Cannot return from {DiagnosticReporter.DescribeFunction(function.Symbol)}", null)
			};
		}
		
		if (node.Value is not { } expression)
			return new ResolvedReturnExpressionNode(null, node);
		
		var returnType = function.Signature.ReturnType;
		if (returnType != NativeSymbols.Void)
			return new ResolvedReturnExpressionNode(VisitNode(expression, returnType), node);
		
		var discarded = VisitDiscarded(expression);
		if (IsInvalid(discarded))
			return new ResolvedInvalidExpressionNode(node);
		
		if (discarded.Type is NeverType)
			return new ResolvedReturnExpressionNode(discarded, node);
		
		return Error(node, $"Cannot return a value from {DiagnosticReporter.DescribeFunction(function.Symbol)}", null,
			expression);
	}
	
	public IResolvedExpressionNode Visit(BreakExpressionNode node)
	{
		var (label, error) = ResolveLabel(node, node.Label);
		return error ?? (IResolvedExpressionNode)new ResolvedBreakExpressionNode(label, node);
	}
	
	public IResolvedExpressionNode Visit(ContinueExpressionNode node)
	{
		var (label, error) = ResolveLabel(node, node.Label);
		return error ?? (IResolvedExpressionNode)new ResolvedContinueExpressionNode(label, node);
	}
	
	private (LabelSymbol? Label, ResolvedInvalidExpressionNode? Error) ResolveLabel(IExpressionNode node,
		IExpressionNode? labelNode)
	{
		if (labelNode is null)
			return (null, null);
		
		if (labelNode is not VarExpressionNode varExpr)
			return (null, Error(node, "Expected a label", null, labelNode));
		
		var name = varExpr.Identifier.Text;
		if (CurrentScope?.ResolveLabel(name) is { } label)
			return (label, null);
		
		if (CurrentResolutionContext.Resolve(name) is not null)
			return (null, Error(node, $"'{name}' is not a label", null, varExpr));
		
		var labels = CurrentScope?.GetLabelNames().Distinct() ?? [];
		return (null, Error(node, DiagnosticReporter.ReportUndefinedSymbol(labelNode, name, labels), null));
	}
	
	public IResolvedStatementNode Visit(ExpressionStatementNode node) =>
		new ResolvedExpressionStatementNode(VisitDiscarded(node.ExpressionNode), node);
	
	public IResolvedStatementNode Visit(DropStatementNode node) =>
		new ResolvedDropStatementNode(VisitNode(node.Target, null), node);
	
	public IResolvedExpressionNode Visit(CallExpressionNode node)
	{
		_callArguments.Add(null);
		var result = VisitCall(node);
		var arguments = _callArguments[^1];
		_callArguments.RemoveAt(_callArguments.Count - 1);
		if (result is not ResolvedInvalidExpressionNode { Parts.IsEmpty: true } invalid)
			return result;
		
		var parts = ResolveAbandonedParts(node, arguments);
		return parts.Length > 0
			? new ResolvedInvalidExpressionNode(invalid.Syntax, invalid.Type) { Parts = parts }
			: result;
	}
	
	private ImmutableArray<IResolvedExpressionNode> ResolveAbandonedParts(CallExpressionNode node,
		IResolvedExpressionNode[]? arguments)
	{
		var trial = OpenCheckpoint();
		var parts = new List<IResolvedExpressionNode>();
		if (node.Target is AccessExpressionNode { Target: var receiver })
			parts.Add(VisitNode(receiver, null));
		
		parts.AddRange(arguments ?? node.Arguments.Select(argument => VisitNode(argument, NativeSymbols.Invalid)));
		Rollback(trial);
		return [..parts.Where(static part => part is not ResolvedInvalidExpressionNode { Parts.IsEmpty: true })];
	}
	
	private IResolvedExpressionNode VisitCall(CallExpressionNode node)
	{
		if (node.Target is VarExpressionNode callee && ResolveTargetCase(callee.Identifier, node) is { } targetCase)
			return targetCase;
		
		if (node.Target is AccessExpressionNode access && ResolveEnumType(access.Target) is { } enumType)
			return DeclaresNonCaseMember(enumType, access.Member.Text)
				? VisitStaticCall(node, access, ResolveStaticOwner(node, access, enumType))
				: VisitEnumCase(access, enumType, node);
		
		if (CurrentResolutionContext.TryResolveExpressionAsType(node.Target) is not { } targetType)
			return VisitFunctionCall(node);
		
		if (node.Target is not IndexerExpressionNode &&
		    targetType is RecordSymbol { IsGenericDefinition: true } generic)
			return VisitInferredConstruction(node, generic);
		
		return RequireTypeArguments(targetType, node.Target) is InvalidType
			? new ResolvedInvalidExpressionNode(node, CurrentTargetType)
			: VisitTypeCall(node, targetType);
	}
	
	public IResolvedStatementNode Visit(IfStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		ReportDeclarationBody(node.Then);
		var then = VisitInScope(node.Then, GetTrueBindings(condition));
		
		if (node.Else is { } elseNode)
			ReportDeclarationBody(elseNode);
		
		var @else = node.Else is null ? null : VisitInScope(node.Else, []);
		
		return new ResolvedIfStatementNode(condition, then, @else, node);
	}
	
	public IResolvedStatementNode Visit(BrokenStatementNode node)
	{
		if (node.Name is { } name)
			CurrentResolutionContext.LocalScope!.Define(new LocalVariableSymbol(name, NativeSymbols.Invalid,
				node.IsMutable));
		
		return new ResolvedBlockStatementNode([], node);
	}
	
	public IResolvedStatementNode Visit(VarStatementNode node)
	{
		var resolutionContext = CurrentResolutionContext;
		TypeSymbol? type;
		if (node.Type is { } specifiedType)
			type = resolutionContext.ResolveType(specifiedType);
		else
			type = settledTypes?.GetValueOrDefault(node);
		
		OpenLocal? open = null;
		IResolvedExpressionNode? initializer;
		if (node.ExpressionNode is not { } initializerNode)
			initializer = null;
		else if (type is not null)
			initializer = VisitNode(initializerNode, type);
		else if (survey is null && initializerNode is UndefExpressionNode { Type: null })
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"Cannot infer the type of '{node.Identifier.Text}'"));
			
			initializer = new ResolvedUndefExpressionNode(NativeSymbols.Invalid, initializerNode);
		}
		else
		{
			var value = VisitNode(initializerNode, null);
			if (survey is not null && (IsOpenValue(value) || IsOpenSyntax(initializerNode)))
				open = DeclareOpen(new(node, node.Identifier, initializerNode, resolutionContext)
				{
					Value = value,
					IsCaseName = IsCaseNameSyntax(initializerNode)
				}, [value]);
			
			initializer = MaterializeAsDefault(value);
			if (initializer.Type is BorrowType { Target: var target } && _typePool.IsCopy(target))
				initializer = Decay(initializer);
		}
		
		if (type is null && initializer is not null && BorrowsWhenDeclared(initializer))
			initializer = new ResolvedBorrowExpressionNode(initializer,
				_typePool.GetBorrowType(initializer.Type, false), true, initializer.Syntax);
		else if (type is null && initializer is ResolvedBorrowExpressionNode { IsImplicit: false } borrow &&
		         borrow.Syntax is BorrowExpressionNode { IsMutable: false } syntax &&
		         BorrowsWhenDeclared(borrow.Place))
			Diagnostics.Add(new(DiagnosticSeverity.Hint, syntax.Keyword.SourceLocation, "Redundant 'imm'"));
		
		if (type is null && initializer is null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"Cannot infer the type of '{node.Identifier.Text}'"));
		
		if (type is null && initializer?.Type is NeverType)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				"'never' can only be a return type"));
			
			type = NativeSymbols.Invalid;
		}
		
		type ??= initializer?.Type ?? NativeSymbols.Invalid;
		if (TypePool.FindValueDyn(type) is { } valueDyn)
		{
			var location = node.Type?.SourceLocation ?? node.ExpressionNode!.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot use '{valueDyn.Name}' by value"));
			
			type = NativeSymbols.Invalid;
		}
		
		var constant = node.IsConstant && initializer is not null && type is not InvalidType &&
		               !_typePool.NeedsDrop(type)
			? _evaluator.Evaluate(initializer)
			: null;
		
		if (node.IsConstant)
			ReportConstant(node.Identifier, node.Type?.SourceLocation ?? node.Identifier.SourceLocation, type,
				initializer, constant);
		
		var symbol = new LocalVariableSymbol(node.Identifier, type, node.IsMutable)
		{
			ConstantValue = constant,
			IsConstant = node.IsConstant,
			IsDeferred = !node.IsMutable && initializer is null,
			StartsZeroed = node.IsMutable && initializer is null && HasLocalDefault(type)
		};
		
		resolutionContext.LocalScope!.Define(symbol);
		if (open is not null)
			survey!.Bind(symbol, open);
		
		return new ResolvedVarStatementNode(symbol, initializer, node);
	}
	
	private void ReportConstant(Token name, SourceLocation typeLocation, TypeSymbol type,
		IResolvedExpressionNode? initializer, Constant? value)
	{
		if (type is InvalidType || initializer is ResolvedInvalidExpressionNode)
			return;
		
		if (_typePool.NeedsDrop(type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeLocation,
				"Cannot store values with destructors in constants"));
		else if (initializer is null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation,
				"Cannot declare constants without values"));
		else if (value is null)
			Diagnostics.Add(new(DiagnosticSeverity.Error, initializer.Syntax.SourceLocation,
				$"The initial value of '{name.Text}' must be a constant"));
	}
	
	private bool BorrowsWhenDeclared(IResolvedExpressionNode value) =>
		value.Type is DynType || !_typePool.IsCopy(value.Type) && IsStored(value);
	
	public IResolvedStatementNode Visit(WhileStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		// Create a scope for the body and label (if applicable)
		var scope = CurrentScope?.CreateChild() ?? new();
		foreach (var binding in GetTrueBindings(condition))
			scope.Define(binding);
		
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		
		LabelSymbol? symbol;
		if (node.Label is { } label)
		{
			symbol = new(label);
			scope.Define(symbol);
		}
		else
			symbol = null;
		
		_resolutionContexts.Push(resolutionContext);
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedWhileStatementNode(condition, body, symbol, node);
	}
	
	public IResolvedStatementNode Visit(DoWhileStatementNode node)
	{
		var condition = VisitNode(node.Condition, NativeSymbols.Bool);
		
		// Create a scope for the body and label (if applicable)
		var scope = CurrentScope?.CreateChild() ?? new();
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		
		LabelSymbol? symbol;
		if (node.Label is { } label)
		{
			symbol = new(label);
			scope.Define(symbol);
		}
		else
			symbol = null;
		
		_resolutionContexts.Push(resolutionContext);
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedDoWhileStatementNode(body, condition, symbol, node);
	}
	
	public IResolvedStatementNode Visit(ForStatementNode node)
	{
		var header = node.End is { } end ? ResolveRange(node, end) : ResolveIteration(node);
		var scope = CreateScope([header.Binding]);
		LabelSymbol? label = node.Label is { } labelToken ? new(labelToken) : null;
		if (label is not null)
			scope.Define(label);
		
		_resolutionContexts.Push(CurrentResolutionContext with { LocalScope = scope });
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		return header.Build(body, label);
	}
	
	public IResolvedStatementNode Visit(LoopStatementNode node)
	{
		// Create a scope for the body and label (if applicable)
		var scope = CurrentScope?.CreateChild() ?? new();
		var resolutionContext = CurrentResolutionContext with { LocalScope = scope };
		
		LabelSymbol? symbol;
		if (node.Label is { } label)
		{
			symbol = new(label);
			scope.Define(symbol);
		}
		else
			symbol = null;
		
		_resolutionContexts.Push(resolutionContext);
		ReportDeclarationBody(node.Body);
		var body = VisitNode(node.Body);
		_resolutionContexts.Pop();
		
		return new ResolvedLoopStatementNode(body, symbol, node);
	}
}