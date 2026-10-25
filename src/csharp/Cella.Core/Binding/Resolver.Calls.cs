using System.Collections.Immutable;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode VisitFunctionCall(CallExpressionNode node)
	{
		var context = CurrentResolutionContext;
		Symbol? symbol;
		switch (node.Target)
		{
			case VarExpressionNode varExpr:
				ProbeName(varExpr.Identifier);
				symbol = context.Resolve(varExpr.Identifier.Text);
				if (symbol is not null)
					break;
				
				return Error(node,
					ReportUndefinedSymbol(node, varExpr.Identifier.Text, varExpr.Identifier.SourceLocation),
					CurrentTargetType);
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				ProbeMember(access, () => GetModuleCandidates(module));
				symbol = context.ResolveMember(module, access.Member.Text);
				break;
			
			case AccessExpressionNode access:
				return VisitMemberCall(node, access);
			
			case IndexerExpressionNode indexer:
				return VisitExplicitCall(node, indexer);
			
			default:
				return VisitIndirectCall(node, VisitNode(node.Target, null));
		}
		
		var functionSymbols = GetFunctions(symbol);
		if (functionSymbols.Length == 0)
			return VisitIndirectCall(node, VisitNode(node.Target, null));
		
		var candidates = functionSymbols.Select(GetFunctionInfo).Select(static info => new FunctionCallable(info));
		var hidden = node.Target switch
		{
			VarExpressionNode name => FindHiddenCallables(context.FindHiddenFunctions(name.Identifier.Text)),
			AccessExpressionNode access when context.ResolveModule(access.Target) is { } module =>
				FindHiddenCallables(context.FindHiddenMembers(module, access.Member.Text)),
			_ => []
		};
		
		return ResolveCall(node, GetName(node.Target), [..candidates], null, hidden: hidden);
	}
	
	private ICallable[] FindHiddenCallables(IEnumerable<FunctionSymbol> functions) =>
		[..functions.Select(GetFunctionInfo).Select(static info => new FunctionCallable(info))];
	
	private IResolvedExpressionNode VisitExplicitCall(CallExpressionNode node, IndexerExpressionNode indexer)
	{
		var context = CurrentResolutionContext;
		switch (indexer.Target)
		{
			case VarExpressionNode name when GetFunctions(context.Resolve(name.Identifier.Text)) is [_, ..] functions:
				return ResolveExplicitCall(node, indexer, name.Identifier.Text, [..functions.Select(GetFunctionInfo)],
					FindHiddenCallables(context.FindHiddenFunctions(name.Identifier.Text)));
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				return GetFunctions(context.ResolveMember(module, access.Member.Text)) is [_, ..] members
					? ResolveExplicitCall(node, indexer, GetName(access), [..members.Select(GetFunctionInfo)],
						FindHiddenCallables(context.FindHiddenMembers(module, access.Member.Text)))
					: VisitIndirectCall(node, VisitNode(indexer, null));
			
			case AccessExpressionNode access:
				return VisitMemberCall(node, access, indexer);
			
			default:
				return VisitIndirectCall(node, VisitNode(indexer, null));
		}
	}
	
	private IResolvedExpressionNode ResolveExplicitCall(CallExpressionNode node, IndexerExpressionNode indexer,
		string name, FunctionInfo[] functions, ICallable[]? hidden = null)
	{
		ICallable[] candidates = [..functions.Select(static info => new FunctionCallable(info))];
		return ResolveTypeArguments(indexer, functions) is { } typeArguments
			? ResolveCall(node, name, candidates, null, typeArguments, hidden)
			: RejectCall(node, candidates);
	}
	
	private static bool AgreesBeforeNever(ResolutionSet resolutionSet, IResolvedExpressionNode[] args)
	{
		var diverging = Array.FindIndex(args, static arg => arg.Type is NeverType);
		for (var i = 1; i < resolutionSet.Count; i++)
		{
			for (var j = 0; j < diverging; j++)
			{
				if (!IsSameConversion(resolutionSet[0].ArgumentConversions[j], resolutionSet[i].ArgumentConversions[j]))
					return false;
			}
		}
		
		return true;
	}
	
	private static bool IsSameConversion(Conversion? first, Conversion? second) => (first, second) switch
	{
		(null, null) => true,
		(FunctionConversion a, FunctionConversion b) => a.Function == b.Function,
		({ } a, { } b) => a.GetType() == b.GetType() && a.From == b.From && a.To == b.To,
		_ => false
	};
	
	private IResolvedExpressionNode ResolveCall(CallExpressionNode node, string functionName, ICallable[] candidates,
		IResolvedExpressionNode? receiver, TypeArgumentList? typeArguments = null, ICallable[]? hidden = null)
	{
		var result = ResolveCandidates(node, functionName, candidates, receiver, typeArguments, hidden);
		if (result is ResolvedInvalidExpressionNode)
			RecordCallees(node.Target, candidates);
		
		return result;
	}
	
	private ResolvedInvalidExpressionNode RejectCall(CallExpressionNode node, ICallable[] candidates)
	{
		RecordCallees(node.Target, candidates);
		return new(node, CurrentTargetType);
	}
	
	private void RecordCallees(IExpressionNode target, IEnumerable<ICallable> candidates) => RecordCallees(target,
		candidates.Select(GetInfo).OfType<FunctionInfo>().Select(static info => info.Symbol));
	
	private void RecordCallees(IExpressionNode target, IEnumerable<FunctionSymbol> functions)
	{
		if (RecordName is not { } record || FindCallName(target) is not { } name)
			return;
		
		foreach (var function in functions.Distinct())
			record(name, function);
	}
	
	private static Token? FindCallName(IExpressionNode target) => target switch
	{
		VarExpressionNode variable => variable.Identifier,
		AccessExpressionNode access => access.Member,
		IndexerExpressionNode indexer => FindCallName(indexer.Target),
		_ => null
	};
	
	private IResolvedExpressionNode ResolveCandidates(CallExpressionNode node, string functionName,
		ICallable[] candidates, IResolvedExpressionNode? receiver, TypeArgumentList? typeArguments,
		ICallable[]? hidden)
	{
		var location = (node.Target is IndexerExpressionNode indexer ? indexer.Target : node.Target).SourceLocation;
		if (RejectSpread(node, candidates) is { } rejected)
			return rejected;
		
		var shaped = Shape(candidates, node.Arguments);
		var args = ResolveArguments(Unspread(node.Arguments),
			[..shaped.Select(candidate => GetCallShape(candidate, typeArguments))], out var isAmbiguous,
			Succeeds, Accepts);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var isEndless = args.Any(static arg => arg.Type is NeverType);
		if (isAmbiguous && !isEndless)
			return ReportAmbiguousCall(node, functionName, shaped, receiver, typeArguments);
		
		var failures = new List<Diagnostic>();
		var specialized = SpecializeAll(args, failures);
		var needsTarget = specialized.Length == 0 && failures.Count > 0 && failures.All(_inferenceFailures.Contains);
		if (specialized.Length == 0 && shaped.Length == 1 && failures is [var failure])
		{
			Diagnostics.Add(failure);
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType) { NeedsTargetType = needsTarget };
		}
		
		var resolutionSet = ResolveCallable(specialized, args, MaterializationMode.Overload, CurrentTargetType);
		
		if (resolutionSet.IsAmbiguous && (!isEndless || !AgreesBeforeNever(resolutionSet, args)))
			return ReportAmbiguousCall(node, functionName, shaped, receiver, typeArguments, resolutionSet);
		
		if (!resolutionSet.HasResult && hidden is [_, ..] &&
		    ResolveCallable(SpecializeHidden(hidden, args), args, MaterializationMode.Overload, CurrentTargetType)
			    .HasResult)
			return Error(node, CurrentResolutionContext.ReportHidden(functionName, location) ??
			                   DiagnosticReporter.ReportHidden(location, functionName,
				                   hidden.OfType<FunctionCallable>()
					                   .Max(static h => ModuleIndex.GetVisibility(h.Info.Symbol)),
				                   false), CurrentTargetType);
		
		if (!resolutionSet.HasResult && ReportArgumentModes(specialized, args, CurrentTargetType))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!resolutionSet.HasResult &&
		    candidates.Any(static candidate => candidate.ParameterTypes.Any(static type => type is InvalidType)))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!resolutionSet.HasResult && candidates is [var only])
			return ReportSingleMismatch(node, functionName, only, specialized is [var single] ? single : null, args,
				location)
				? new ResolvedInvalidExpressionNode(node, CurrentTargetType)
				: Error(node, $"'{functionName}' doesn't accept these arguments", CurrentTargetType, node.Target);
		
		if (!resolutionSet.HasResult)
		{
			var error = Error(node, $"No overload of '{functionName}' accepts these arguments", CurrentTargetType,
				node.Target);
			
			return needsTarget ? new ResolvedInvalidExpressionNode(node, error.Type) { NeedsTargetType = true } : error;
		}
		
		var resolution = resolutionSet[0];
		if (resolution.Callable is OperationImpl or StoreCallable)
			return ResolveOperationCall(node, args, resolution);
		
		var info = resolution.Callable switch
		{
			FunctionCallable callable => callable.Info,
			ReceiverCallable callable => callable.Info,
			_ => throw new InvalidOperationException()
		};
		
		if (info.Symbol.Syntax is NativeFStrPartNode fstrPart && receiver is not null)
			return new ResolvedFStrPartExpressionNode(info.Signature.ReturnType, receiver, fstrPart.Part,
				ApplyArgumentResolution(args, resolution)[0], node);
		
		var isErased = info.Symbol.Syntax is FunctionNode { When.IsActive: false };
		if (!isErased)
			TrackFunctionUse(info, node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		if (GetForm(resolution.Callable) is { } form)
			CompleteArguments(resolvedArgs, info, form, node);
		
		if (receiver is not null)
			resolvedArgs.Insert(0, CreateReceiver(receiver, info));
		
		var result = new ResolvedFunctionCallExpressionNode(info, resolvedArgs, node);
		return isErased ? new ResolvedErasedCallExpressionNode(result) : ApplyResultResolution(result, resolution);
		
		ICallable[] SpecializeHidden(ICallable[] callables, IResolvedExpressionNode[] values) =>
		[
			..Shape(callables, node.Arguments)
				.Select(candidate => Specialize(candidate, functionName, values, typeArguments, location, []))
				.OfType<ICallable>()
		];
		
		ICallable[] SpecializeAll(IResolvedExpressionNode[] values, List<Diagnostic> failed) =>
		[
			..shaped
				.Select(candidate => Specialize(candidate, functionName, values, typeArguments, location, failed))
				.OfType<ICallable>()
		];
		
		bool Succeeds(IResolvedExpressionNode[] values) => ResolveCallable(SpecializeAll(values, []), values,
			MaterializationMode.Overload, CurrentTargetType).HasResult;
		
		bool Accepts(int index, IResolvedExpressionNode[] values) =>
			Specialize(shaped[index], functionName, values, typeArguments, location, []) is { } callable &&
			ResolveCallable([callable], values, MaterializationMode.Overload, CurrentTargetType).HasResult;
	}
	
	private static IEnumerable<ICallable> GetCallables(ResolutionSet? tied) =>
		tied is not { } set ? [] : Enumerable.Range(0, set.Count).Select(i => set[i].Callable);
	
	private static IEnumerable<string> DescribeCandidates(SourceLocation location,
		IEnumerable<ICallable> candidates) => candidates
		.Select(static callable => GetInfo(callable)?.Symbol.Definition)
		.OfType<SourceLocation>()
		.Select(definition => (Other: definition.Source != location.Source,
			File: DescribeFile(location, definition), definition.GetLineColumn().Line))
		.OrderBy(static candidate => candidate.Other)
		.ThenBy(static candidate => candidate.File, StringComparer.Ordinal)
		.ThenBy(static candidate => candidate.Line)
		.Select(static candidate => candidate.Other
			? $"Candidate in '{candidate.File}' on line {candidate.Line}"
			: $"Candidate on line {candidate.Line}");
	
	private static string DescribeFile(SourceLocation from, SourceLocation definition) =>
		Path.GetDirectoryName(from.Source.FilePath) is { Length: > 0 } directory
			? Path.GetRelativePath(directory, definition.Source.FilePath).Replace('\\', '/')
			: Path.GetFileName(definition.Source.FilePath);
	
	private ResolvedInvalidExpressionNode ReportAmbiguousCall(CallExpressionNode node, string functionName,
		ICallable[] shaped, IResolvedExpressionNode? receiver, TypeArgumentList? typeArguments,
		ResolutionSet? tied = null)
	{
		if (!node.Arguments.Any(IsDeferred))
			return ReportAmbiguity(node, functionName, GetCallables(tied));
		
		var succeeded = new List<ICallable>();
		var failures = new List<List<Diagnostic>>();
		foreach (var candidate in shaped)
		{
			var trial = OpenCheckpoint();
			var result = ResolveCall(node, functionName, [candidate], receiver, typeArguments);
			var errors = trial.Redirections
				.SelectMany(static redirection => redirection.Captured)
				.Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
				.ToList();
			
			Rollback(trial);
			if (errors.Count == 0 && !IsInvalid(result))
				succeeded.Add(candidate);
			else
				failures.Add(errors);
		}
		
		if (succeeded.Count > 0)
			return ReportAmbiguity(node, functionName, succeeded.Count > 1 ? succeeded : GetCallables(tied));
		
		var shared = failures[0]
			.DistinctBy(static diagnostic => (diagnostic.SourceLocation, diagnostic.Message))
			.Where(diagnostic => failures.All(errors => errors.Any(other =>
				other.SourceLocation == diagnostic.SourceLocation && other.Message == diagnostic.Message)))
			.OrderBy(static diagnostic => diagnostic.SourceLocation.Range.Start)
			.ToList();
		
		if (shared.Count > 0)
		{
			Diagnostics.AddRange(shared);
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		return Error(node, $"No overload of '{functionName}' accepts these arguments", CurrentTargetType,
			node.Target);
	}
	
	private ResolvedInvalidExpressionNode ReportAmbiguity(CallExpressionNode node, string functionName,
		IEnumerable<ICallable> candidates) => Error(node, new Diagnostic(DiagnosticSeverity.Error,
		node.Target.SourceLocation, $"Call to '{functionName}' is ambiguous")
	{
		Hints = [..DescribeCandidates(node.SourceLocation, candidates)]
	}, CurrentTargetType);
	
	private ResolvedInvalidExpressionNode? RejectSpread(CallExpressionNode node, IReadOnlyList<ICallable> candidates)
	{
		var index = FindSpread(node.Arguments);
		if (index < 0)
			return null;
		
		var spread = node.Arguments[index];
		var variadic = candidates.Where(static candidate => candidate.HasVariadicParameter).ToList();
		if (variadic.Count == 0)
			return Error(node, candidates.Any(static candidate => candidate.IsVariadic)
				? "Cannot spread arguments into 'ext' functions"
				: "Cannot spread arguments into calls without variadic parameters", CurrentTargetType, spread);
		
		if (variadic.Any(candidate => index == candidate.ParameterTypes.Length - 1 &&
		                              node.Arguments.Length == index + 1))
			return null;
		
		return variadic.Any(candidate => index >= candidate.ParameterTypes.Length - 1)
			? Error(node, "Cannot combine spread arguments with other variadic arguments", CurrentTargetType, spread)
			: null;
	}
	
	private static ImmutableArray<IExpressionNode> Unspread(ImmutableArray<IExpressionNode> arguments) =>
		[..arguments.Select(static argument => argument is SpreadExpressionNode spread ? spread.Value : argument)];
	
	private static int FindSpread(ImmutableArray<IExpressionNode> arguments) => arguments
		.Select(static (argument, i) => argument is SpreadExpressionNode ? i : -1)
		.FirstOrDefault(static i => i >= 0, -1);
	
	private IResolvedExpressionNode ResolveOperationCall(CallExpressionNode node, IResolvedExpressionNode[] args,
		CallableResolution resolution)
	{
		var operands = ApplyArgumentResolution(args, resolution);
		IResolvedExpressionNode result = resolution.Callable switch
		{
			StoreCallable store => new ResolvedAssignmentExpressionNode(store.Place,
				((ResolvedMutArgumentExpressionNode)operands[0]).Place, store.Op, operands[1], store.Operation, node,
				false),
			OperationImpl operation when operands is [var operand] =>
				new ResolvedUnaryOpExpressionNode(operand, operation, node),
			var operation => new ResolvedBinaryOpExpressionNode(operands[0], operands[1], (OperationImpl)operation,
				node)
		};
		
		return ApplyResultResolution(result, resolution);
	}
	
	private sealed class StoreCallable(OperationImpl operation, Token op, TypePool typePool) : ICallable
	{
		public OperationImpl Operation { get; } = operation;
		public Token Op { get; } = op;
		public TypeSymbol Place { get; } = operation.ParameterTypes[0];
		
		public ImmutableArray<TypeSymbol> ParameterTypes { get; } =
			[typePool.GetPointerType(operation.ParameterTypes[0]), ..operation.ParameterTypes.Skip(1)];
		
		public TypeSymbol ReturnType { get; } = operation.ParameterTypes[0];
		
		public ParameterMode GetMode(int index) => index == 0 ? ParameterMode.Mut : ParameterMode.Value;
	}
	
	private ICallable[] Shape(IEnumerable<ICallable> candidates, ImmutableArray<IExpressionNode> arguments)
	{
		var spread = FindSpread(arguments);
		return [..candidates.Select(candidate => Shape(candidate, arguments.Length, spread)).OfType<ICallable>()];
	}
	
	private ICallable? Shape(ICallable candidate, int count, int spread)
	{
		var isVariadic = candidate.HasVariadicParameter;
		var defaults = GetInfo(candidate)?.Signature.Defaults.Length ?? 0;
		if (!isVariadic && defaults == 0)
			return spread < 0 ? candidate : null;
		
		var fixedCount = candidate.ParameterTypes.Length - (isVariadic ? 1 : 0);
		if (spread >= 0)
			return isVariadic && spread == fixedCount && count == fixedCount + 1 ? candidate : null;
		
		var omitted = Math.Max(fixedCount - count, 0);
		if (omitted > defaults || !isVariadic && omitted == 0)
			return candidate;
		
		TypeSymbol? element = null;
		if (isVariadic && (element = _typePool.FindVariadicElement(candidate.ParameterTypes[^1])) is null)
			return null;
		
		var form = new CallForm(omitted, element, Math.Max(count - fixedCount, 0));
		return candidate switch
		{
			FunctionCallable callable => new FunctionCallable(callable.Info, form) { IsGeneric = callable.IsGeneric },
			ReceiverCallable callable => new ReceiverCallable(callable.Info, callable.ReturnType, form)
			{
				IsGeneric = callable.IsGeneric
			},
			FunctionTypeCallable callable => new FunctionTypeCallable(callable.Type, form),
			_ => candidate
		};
	}
	
	private static FunctionInfo? GetInfo(ICallable callable) => callable switch
	{
		FunctionCallable function => function.Info,
		ReceiverCallable receiver => receiver.Info,
		_ => null
	};
	
	private static CallForm? GetForm(ICallable callable) => callable switch
	{
		FunctionCallable function => function.Form,
		ReceiverCallable receiver => receiver.Form,
		FunctionTypeCallable type => type.Form,
		_ => null
	};
	
	private CallForm? Repack(ICallable candidate, FunctionInfo info) => GetForm(candidate) switch
	{
		{ Element: null } form => form,
		{ } form => _typePool.FindVariadicElement(info.Signature.ParameterTypes[^1]) is { } element
			? form with { Element = element }
			: null,
		null => null
	};
	
	private void CompleteArguments(List<IResolvedExpressionNode> args, FunctionInfo info, CallForm form,
		CallExpressionNode node)
	{
		var map = info.TypeArguments.IsDefaultOrEmpty
			? []
			: TypePool.CreateMap(info.Symbol.TypeParameters, info.TypeArguments);
		
		CompleteArguments(args, info.Signature.ParameterTypes,
		[
			..info.Signature.Defaults.Select(value => value is ParametricConstant parametric
				? new ParametricConstant(SubstituteParametric(parametric.Value, map))
				: _typePool.SubstituteConstant(value, map))
		], form, node);
	}
	
	private IResolvedExpressionNode SubstituteParametric(IResolvedExpressionNode node,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) => node switch
	{
		ResolvedSizeOfExpressionNode n => new ResolvedSizeOfExpressionNode(_typePool.Substitute(n.Target, map),
			n.Syntax),
		ResolvedAlignOfExpressionNode n =>
			new ResolvedAlignOfExpressionNode(_typePool.Substitute(n.Target, map), n.Syntax),
		ResolvedValueParameterExpressionNode n => _typePool.SubstituteParameter(n.Parameter, map) switch
		{
			ValueArgumentType { Value: var value } when n.Type is IntegerStorageType type =>
				MaterializeLiteral(n.Syntax, type, value),
			TypeParameterSymbol parameter when parameter != n.Parameter =>
				new ResolvedValueParameterExpressionNode(parameter, n.Syntax),
			_ => n
		},
		ResolvedUnaryOpExpressionNode n =>
			new ResolvedUnaryOpExpressionNode(SubstituteParametric(n.Operand, map), n.Operation, n.Syntax),
		ResolvedBinaryOpExpressionNode n => new ResolvedBinaryOpExpressionNode(SubstituteParametric(n.Left, map),
			SubstituteParametric(n.Right, map), n.Operation, n.Syntax),
		ResolvedConversionExpressionNode n =>
			new ResolvedConversionExpressionNode(SubstituteParametric(n.Source, map), n.Conversion, n.Syntax),
		_ => node
	};
	
	private void CompleteArguments(List<IResolvedExpressionNode> args, ImmutableArray<TypeSymbol> parameterTypes,
		ImmutableArray<Constant> defaults, CallForm form, CallExpressionNode node)
	{
		var packed = args.GetRange(args.Count - form.Packed, form.Packed);
		args.RemoveRange(args.Count - form.Packed, form.Packed);
		
		var fixedCount = parameterTypes.Length - (form.Element is null ? 0 : 1);
		var firstDefault = fixedCount - defaults.Length;
		for (var i = fixedCount - form.Omitted; i < fixedCount; i++)
			args.Add(MaterializeConstant(defaults[i - firstDefault], parameterTypes[i], node));
		
		if (form.Element is { } element)
			args.Add(CreatePack(parameterTypes[^1], element, packed, packed is [var first, ..] ? first.Syntax : node));
	}
	
	private IResolvedExpressionNode MaterializeConstant(Constant constant, TypeSymbol type, IExpressionNode syntax)
	{
		switch (constant)
		{
			case ParametricConstant c:
				return c.Value;
			
			case IntegerConstant c when type is IntegerStorageType integer:
				return MaterializeLiteral(syntax, integer, c.Value);
			
			case FloatConstant c:
				return new ResolvedLiteralExpressionNode(type, c.Value, syntax);
			
			case BoolConstant c:
				return new ResolvedLiteralExpressionNode(type, c.Value, syntax);
			
			case StringConstant c:
				return new ResolvedLiteralExpressionNode(type, c.Value, syntax);
			
			case NullConstant:
				return new ResolvedLiteralExpressionNode(type, null, syntax);
			
			case RecordConstant c when type is RecordSymbol record:
				return new ResolvedRecordExpressionNode(record, _typePool.GetMembers(record).OfType<FieldSymbol>()
						.Zip(c.Fields,
							(field, value) => (field, MaterializeConstant(value, GetMemberType(field), syntax))),
					syntax);
			
			case ZeroConstant when type is RecordSymbol record:
				return new ResolvedRecordExpressionNode(record, [], syntax);
			
			case ArrayConstant c when type is ArrayType array:
				return new ResolvedArrayExpressionNode(array,
					c.Elements.Select(element => MaterializeConstant(element, array.ElementType, syntax)), syntax);
			
			case EnumConstant c when type is EnumSymbol enumType:
			{
				var enumCase = enumType.Cases[c.Case.Index];
				var payloadTypes = _typePool.GetPayloadTypes(enumType, enumCase);
				return new ResolvedEnumCaseExpressionNode(enumType, enumCase,
					c.Payload.Select((value, i) => MaterializeConstant(value, payloadTypes[i], syntax)), syntax);
			}
			
			case EnumTagConstant c when type is EnumSymbol enumType &&
			                            _typePool.FindCase(enumType, c.Tag) is { } tagCase:
				return new ResolvedEnumCaseExpressionNode(enumType, tagCase, [], syntax);
			
			case FunctionConstant c:
			{
				TrackFunctionUse(c.Function, syntax);
				var reference = new ResolvedFunctionReferenceExpressionNode(c.Function, GetNaturalType(c.Function),
					syntax);
				
				return reference.Type == type ? reference : CoerceToType(reference, type);
			}
			
			default:
				return new ResolvedInvalidExpressionNode(syntax, type);
		}
	}
	
	private IResolvedExpressionNode CreatePack(TypeSymbol type, TypeSymbol element,
		IReadOnlyList<IResolvedExpressionNode> values, IExpressionNode syntax)
	{
		IResolvedExpressionNode[] parts =
		[
			new ResolvedPackExpressionNode(_typePool.GetPointerType(element), element, values, syntax),
			MaterializeLiteral(syntax, NativeSymbols.UIntSize, values.Count)
		];
		
		switch (_typePool.FindRunWitness(type, element))
		{
			case FunctionWitness { Info: var constructor }:
				TrackFunctionUse(constructor, syntax);
				return new ResolvedConstructorCallExpressionNode(constructor, parts, type, syntax);
			
			case MemberwiseWitness when type is RecordSymbol record:
				return new ResolvedRecordExpressionNode(record,
					_typePool.GetMembers(record).OfType<FieldSymbol>().Zip(parts), syntax);
			
			default:
				return new ResolvedInvalidExpressionNode(syntax, type);
		}
	}
	
	private IResolvedExpressionNode CreateReceiver(IResolvedExpressionNode receiver, FunctionInfo method,
		PlaceOperator? place = null) =>
		method.Signature.GetMode(0) == ParameterMode.Mut
			? new ResolvedMutArgumentExpressionNode(MakeWritable(receiver), _typePool.GetPointerType(receiver.Type),
				receiver.Syntax)
			{
				OnlyMutOperator = place is { } op &&
				                  FindPlaceOperator(receiver.Type, op.GetName(), ParameterMode.Value) is null
					? op.GetName()
					: null
			}
			: receiver;
	
	private IResolvedExpressionNode VisitArgument(IExpressionNode node, IEnumerable<TypeSymbol> expected,
		TypeSymbol? hint = null)
	{
		var types = expected.ToList();
		if (node is VarExpressionNode name)
			ProbeName(name.Identifier, hint ?? (types.Distinct().ToList() is [var only] ? only : null), types);
		
		return VisitCaseName(node, types) ?? ReportMisspelledCase(node, types) ??
			(hint is null ? VisitArgument(node) : VisitHinted(node, hint));
	}
	
	private IResolvedExpressionNode? ReportMisspelledCase(IExpressionNode node, IEnumerable<TypeSymbol> expected)
	{
		if (node is not VarExpressionNode { Identifier.Text: var name } ||
		    CurrentResolutionContext.Resolve(name) is not null)
			return null;
		
		var cases = expected.OfType<EnumSymbol>().SelectMany(static type => type.Cases).Select(static c => c.Name)
			.ToList();
		
		return DiagnosticReporter.FindSuggestion(name, cases) is null
			? null
			: Error(node, DiagnosticReporter.ReportUndefinedSymbol(node, name, cases), null);
	}
}