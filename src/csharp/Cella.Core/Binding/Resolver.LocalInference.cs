using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private T InferLocals<T>(Func<T> resolve)
	{
		if (survey is not null || settledTypes is not null)
			return resolve();
		
		var checkpoint = OpenCheckpoint();
		var activeSurvey = survey = new LocalSurvey();
		var surveyed = resolve();
		survey = null;
		if (!activeSurvey.HasLocals)
		{
			Commit(checkpoint);
			return surveyed;
		}
		
		var settlement = activeSurvey.Settle(ConvertsInOneStep, CastsInOneStep);
		Rollback(checkpoint);
		
		foreach (var (local, types) in settlement.Conflicts)
			Diagnostics.Add(new(DiagnosticSeverity.Error, local.Identifier.SourceLocation,
				$"Cannot infer the type of '{local.Identifier.Text}' from {JoinWrittenNames(types)}"));
		
		settledTypes = settlement.Types.ToDictionary();
		var result = resolve();
		settledTypes = null;
		return result;
	}
	
	private bool ConvertsInOneStep(TypeSymbol from, TypeSymbol to) =>
		from == to || _conversionTable.FindImplicit(from, to) is not null ||
		FindUserConversion(from, null, ParameterMode.Value, to) is { IsAmbiguous: false };
	
	private bool CastsInOneStep(TypeSymbol from, TypeSymbol to) =>
		from == to || _conversionTable.FindExplicit(from, to) is not null ||
		FindUserConversion(from, null, ParameterMode.Value, to, true) is { IsAmbiguous: false } ||
		_typePool.GetConstructors(to).Any(constructor => constructor.Signature.ParameterTypes is [var parameter] &&
		                                                 parameter == from);
	
	private OpenLocal DeclareOpen(OpenLocal local, IEnumerable<IResolvedExpressionNode> values)
	{
		var activeSurvey = survey!;
		local = activeSurvey.Declare(local);
		var linked = values
			.SelectMany(value => activeSurvey.GetTracked(value))
			.Where(static tracked => tracked.IsWhole)
			.Select(static tracked => tracked.Local)
			.Distinct();
		
		foreach (var other in linked)
			Link(local, other);
		
		return local;
	}
	
	private void Link(OpenLocal local, OpenLocal other)
	{
		if (survey is not { } activeSurvey || local == other)
			return;
		
		activeSurvey.Add(new(local, LocalUseKind.Link, null, other));
		Journal(activeSurvey.RemoveLast);
	}
	
	private void RecordUse(OpenLocal local, TypeSymbol type, LocalUseKind kind)
	{
		if (survey is not { } activeSurvey || IsInvalid(type) || ContainsUntyped(type))
			return;
		
		activeSurvey.Add(new(local, kind, type, null));
		Journal(activeSurvey.RemoveLast);
	}
	
	private static bool ContainsUntyped(TypeSymbol type) => type switch
	{
		UntypedType => true,
		PointerType pointer => ContainsUntyped(pointer.BaseType),
		BorrowType borrow => ContainsUntyped(borrow.Target),
		ArrayType array => ContainsUntyped(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(ContainsUntyped),
		_ => type.TypeArguments.Any(ContainsUntyped)
	};
	
	private void RecordMaterialization(IResolvedExpressionNode node, TypeSymbol target)
	{
		if (survey is not { } activeSurvey || activeSurvey.IsDefaulted(node))
			return;
		
		var type = target is BorrowType { IsMutable: false } borrow ? borrow.Target : target;
		if (!CouldBecome(node, type))
			return;
		
		foreach (var tracked in activeSurvey.GetTracked(node))
			RecordUse(tracked.Local, Wrap(type, tracked.Lengths), LocalUseKind.Read);
	}
	
	private void RecordCast(IResolvedExpressionNode node, TypeSymbol target)
	{
		if (survey is not { } activeSurvey)
			return;
		
		activeSurvey.MarkDefaulted(node);
		foreach (var tracked in activeSurvey.GetTracked(node).Where(static tracked => tracked.IsWhole))
			RecordUse(tracked.Local, target, LocalUseKind.Cast);
	}
	
	private TypeSymbol Wrap(TypeSymbol type, ImmutableArray<BigInteger> lengths)
	{
		for (var i = lengths.Length - 1; i >= 0; i--)
			type = _typePool.GetArrayType(type, lengths[i]);
		
		return type;
	}
	
	private OpenLocal? FindElement(IResolvedExpressionNode array)
	{
		foreach (var tracked in survey?.GetTracked(array) ?? [])
		{
			var syntax = tracked.Local.Initializer as ArrayExpressionNode;
			for (var i = 0; i < tracked.Lengths.Length && syntax is not null; i++)
				syntax = syntax.Values is [ArrayExpressionNode inner, ..] ? inner : null;
			
			if (syntax is { Values: [var element, ..] })
				return new(tracked.Local.Declaration, tracked.Local.Identifier, element, tracked.Local.Context)
				{
					Owner = tracked.Local,
					Lengths = [..tracked.Lengths, syntax.Values.Length],
					IsCaseName = IsCaseNameSyntax(element)
				};
		}
		
		return null;
	}
	
	private bool CouldBecome(IResolvedExpressionNode node, TypeSymbol type) => node switch
	{
		ResolvedCaseNameExpressionNode => type is EnumSymbol,
		{ Type: FunctionGroupType group } => type is FunctionType function && FindFunction(group, function) is not null,
		_ => node.Type is UntypedType untyped &&
		     untyped.MaterializationCost(type, MaterializationMode.Overload) != int.MaxValue
	};
	
	private bool IsOpenValue(IResolvedExpressionNode value) =>
		!IsInvalid(value) &&
		value.Type is UntypedType and not (NeverType or FunctionGroupType { Functions.Length: 1 }) ||
		value.Type is FunctionGroupType { Functions: [var only] } && !GetOpenTypeParameters(only).IsEmpty ||
		!survey!.GetTracked(value).IsEmpty;
	
	private bool IsOpenSyntax(IExpressionNode node) => node switch
	{
		LiteralExpressionNode { Token: { Suffix: null, Type: var type } } => type is TokenType.IntegerLiteral
			or TokenType.FloatLiteral or TokenType.StringLiteral or TokenType.KeywordNull,
		InterpolatedStringExpressionNode or UndefExpressionNode { Type: null } => true,
		ArrayExpressionNode array => !array.Values.IsEmpty && array.Values.All(IsOpenSyntax),
		MatchExpressionNode match => match.Arms.All(arm => arm.Value is { } value && IsCaseNameSyntax(value)),
		LambdaExpressionNode lambda => lambda.Parameters.Any(static parameter => parameter.Type is null),
		UnaryOpExpressionNode { Op.Type: TokenType.OpMinus } unary => IsOpenSyntax(unary.Operand),
		VarExpressionNode name when FindOpenLocal(name) is not null => true,
		_ => IsCaseNameSyntax(node)
	};
	
	private OpenLocal? FindOpenLocal(VarExpressionNode name) =>
		survey is { } activeSurvey &&
		CurrentResolutionContext.Resolve(name.Identifier.Text) is LocalVariableSymbol local
			? activeSurvey.Find(local)
			: null;
	
	private bool IsCaseNameSyntax(IExpressionNode node) => GetCaseToken(node) is { } name &&
	                                                       CurrentResolutionContext.Resolve(name.Text) is null;
	
	private static Token? GetCaseToken(IExpressionNode node) => node switch
	{
		VarExpressionNode variable => variable.Identifier,
		CallExpressionNode { Target: VarExpressionNode callee } => callee.Identifier,
		_ => null
	};
	
	private bool NeedsTarget(IExpressionNode initializer) => initializer switch
	{
		ArrayExpressionNode or LambdaExpressionNode => true,
		MatchExpressionNode match => match.Arms.All(arm => arm.Value is { } value && IsCaseNameSyntax(value)),
		VarExpressionNode name when FindOpenLocal(name) is { } other => other.IsCaseName ||
		                                                                NeedsTarget(other.Initializer),
		_ => false
	};
	
	private OpenLocal? FindOpenPlace(IResolvedExpressionNode node) =>
		survey is { } activeSurvey && node is ResolvedVarExpressionNode { Symbol: LocalVariableSymbol local }
			? activeSurvey.Find(local)
			: null;
	
	private IResolvedExpressionNode Substitute(OpenLocal local)
	{
		var expected = ExpectedType;
		_resolutionContexts.Push(local.Context);
		var value = local switch
		{
			{ IsCaseName: true } => CreateCaseName(GetCaseToken(local.Initializer)!.Value, local.Initializer),
			{ Initializer: LambdaExpressionNode lambda } => ResolveLambda(lambda, expected as FunctionType),
			_ => ((IExpressionNodeVisitor<IResolvedExpressionNode>)this).Visit(local.Initializer)
		};
		
		_resolutionContexts.Pop();
		var tracked = local.Tracked;
		if (expected is not null && !IsInvalid(value) && value.Type is not UntypedType)
			RecordUse(tracked.Local,
				Wrap(local.Initializer is LambdaExpressionNode ? expected : value.Type, tracked.Lengths),
				LocalUseKind.Read);
		
		survey!.Track(value, [tracked]);
		return value;
	}
	
	private ImmutableArray<TrackedLocal> FindStoredLocals(IResolvedExpressionNode node) =>
		FindOpenPlace(node) is { } open ? [open.Tracked] : survey?.GetTracked(node) ?? [];
	
	private IResolvedExpressionNode RecordStore(BinaryOpExpressionNode node, ImmutableArray<TrackedLocal> places)
	{
		var value = VisitNode(node.Right, null);
		var linked = survey!.GetTracked(value);
		foreach (var place in places.Where(static place => place.IsWhole))
		{
			foreach (var other in linked.Where(static other => other.IsWhole))
				Link(place.Local, other.Local);
		}
		
		if (linked.IsEmpty && !IsInvalid(value))
		{
			foreach (var place in places)
				RecordUse(place.Local, Wrap(Decay(value).Type, place.Lengths), LocalUseKind.Write);
		}
		
		return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
}