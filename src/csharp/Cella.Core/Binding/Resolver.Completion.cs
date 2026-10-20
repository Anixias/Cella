using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	public CompletionProbe? Probe { get; init; }
	
	private void ProbeName(Token name) => ProbeName(name, CurrentTargetType);
	
	private void ProbeName(Token name, TypeSymbol? expectedType)
	{
		if (Probe is null)
			return;
		
		if (Probe.Covers(name))
			Probe.Capture(CompletionPlace.Name, GetNameCandidates(), expectedType);
		
		Probe.CaptureCall(name, GetNameCandidates());
	}
	
	private IEnumerable<CompletionCandidate> GetNameCandidates() =>
		CurrentResolutionContext.GetAllSymbols().Distinct().Select(ToCandidate);
	
	private void ProbeMember(AccessExpressionNode node, Func<IEnumerable<CompletionCandidate>> candidates)
	{
		if (Probe is null)
			return;
		
		if (Probe.Covers(node.Member))
			Probe.Capture(CompletionPlace.Member, candidates(), CurrentTargetType);
		
		Probe.CaptureCall(node.Member, candidates());
	}
	
	private void ProbeCall(CallExpressionNode node)
	{
		if (Probe is null || FindCallee(node.Target) is not { } callee)
			return;
		
		var (source, range) = node.SourceLocation;
		var start = node.OpenParen.SourceLocation.Range.End;
		var end = node.CloseParen?.SourceLocation.Range.Start ?? range.End;
		Probe.EnterCall(callee, new(source, new TextRange(start, end)),
			node.Arguments.Select(static argument => argument.SourceLocation));
	}
	
	private static Token? FindCallee(IExpressionNode target) => target switch
	{
		VarExpressionNode name => name.Identifier,
		AccessExpressionNode access => access.Member,
		IndexerExpressionNode indexer => FindCallee(indexer.Target),
		_ => null
	};
	
	private void ProbePattern(PatternNode pattern, TypeSymbol subjectType)
	{
		if (pattern.Kind is PatternKind.Case or PatternKind.Type && Probe?.Covers(pattern.CaseName) == true)
			Probe.Capture(CompletionPlace.Member, subjectType.OriginalDefinition is EnumSymbol enumType
				? enumType.Cases.Select(enumCase => new CompletionCandidate(enumCase, Type: subjectType))
				: [], null);
	}
	
	private CompletionCandidate ToCandidate(Symbol symbol) => symbol switch
	{
		FunctionSymbol function => new(symbol, _signatures.FindFunctionInfo(function)),
		GlobalSymbol global => new(symbol, Global: _signatures.GetGlobalInfo(global)),
		ParameterSymbol parameter when CurrentResolutionContext.ContainingFunction is { } containing &&
		                               containing.Symbol.Parameters.IndexOf(parameter) is >= 0 and var index &&
		                               index < containing.Signature.ParameterTypes.Length =>
			new(symbol, Type: containing.Signature.GetDeclaredType(index)),
		_ => new(symbol)
	};
	
	private IEnumerable<CompletionCandidate> GetModuleCandidates(ModulePathSymbol module) =>
		CurrentResolutionContext.GetModuleMembers(module).Select(ToCandidate);
	
	private IEnumerable<CompletionCandidate> GetValueMemberCandidates(TypeSymbol type)
	{
		TypeSymbol[] owners = type switch
		{
			PointerType { BaseType: var baseType } when baseType != NativeSymbols.Void => [baseType],
			_ when GetDereferenceTarget(type) is { } target => [type, target],
			_ => [type]
		};
		
		foreach (var owner in owners)
		{
			foreach (var name in GetMemberNames(owner))
			{
				if (_typePool.ResolveMember(owner, name) is FieldSymbol field && CanAccess(owner, field.Visibility))
					yield return new(field);
				
				if (GetPropertyMember(owner, name) is { } property && CanAccess(owner, property))
					yield return new(property);
				
				foreach (var method in FindFunctions(owner, name)
					         .Where(method => method.HasReceiver && CanAccess(owner, method.Function)))
					yield return new(method, GetFunctionInfo(method, owner));
			}
		}
	}
	
	private IEnumerable<CompletionCandidate> GetStaticCandidates(TypeSymbol type)
	{
		if (type.OriginalDefinition is EnumSymbol enumType)
		{
			foreach (var enumCase in enumType.Cases)
				yield return new(enumCase, Type: type);
		}
		
		if (type.OriginalDefinition is NamedTypeSymbol named)
		{
			foreach (var field in named.StaticFields.Where(CurrentResolutionContext.IsVisible))
				yield return ToCandidate(field);
		}
		
		foreach (var use in GetTypeConstants(type).Where(use => type.GetStaticField(use.Constant.Name) is null)
			         .DistinctBy(static use => use.Constant.Name))
			yield return ToCandidate(use.Constant);
		
		foreach (var name in GetMemberNames(type))
		{
			foreach (var method in FindStatics(type, name).Where(method => CanAccess(type, method.Function)))
				yield return new(method, GetFunctionInfo(method, type));
		}
	}
}