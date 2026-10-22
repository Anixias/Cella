using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode VisitTypeCall(CallExpressionNode node, TypeSymbol targetType)
	{
		if (targetType is ArrayType array && node.Arguments is [var maker] && ResolveArrayBuild(node, array, maker) is
			    { } built)
			return built;
		
		if (targetType is DynType dyn)
			return VisitDynConversion(node, dyn);
		
		if (targetType is BorrowType)
			return Error(node, "Cannot construct borrow types", targetType, node.Target);
		
		if (targetType is FStrType && node.Arguments is [var text])
			return VisitNode(text, targetType);
		
		if (targetType is RecordSymbol record)
			return _typePool.GetConstructors(record).Count == 0
				? VisitRecordConversion(node, record) ?? VisitRecordConstruction(node, record)
				: VisitConstructorCall(node, record, null);
		
		if (targetType is TypeParameterSymbol { HasNew: true } && node.Arguments.Length == 0)
			return new ResolvedNewExpressionNode(targetType, node);
		
		if (targetType is TypeParameterSymbol && _typePool.GetConstructors(targetType).Count > 0)
			return VisitConstructorCall(node, targetType, null);
		
		if (node.Arguments.Length == 0 && _typePool.HasNew(targetType))
			return new ResolvedNewExpressionNode(targetType, node);
		
		if (node.Arguments.Length != 1)
			return VisitConstructorCall(node, targetType, null);
		
		// Don't push targetType; we're trying to find a CAST to targetType, not a targetType itself
		var argument = node.Arguments[0];
		var arg = VisitPeerOperand(argument, () => VisitNode(argument, null), targetType);
		RecordCast(arg, targetType);
		
		// If the argument has an invalid type, we don't want to cascade useless errors; assume identity conversion
		if (IsInvalid(arg))
			return new ResolvedConversionExpressionNode(arg, new IdentityConversion(targetType), node);
		
		if (arg is ResolvedFunctionGroupExpressionNode group && targetType is FunctionType)
		{
			var reference = MaterializeFunction(group, targetType);
			return reference == group ? ReportFunctionMismatch(group, targetType) : reference;
		}
		
		if (arg.Type is UntypedType)
		{
			arg = MaterializeExpression(arg, targetType is EnumSymbol { IsMatch: true } matchEnum
				? _typePool.GetMatchedType(matchEnum)
				: targetType);
			
			if (arg.Type is UntypedType && FindUserConversion(arg, targetType, true) is { } literalConversion)
				return ApplyUserConversion(arg, literalConversion, node);
			
			if (arg.Type is UntypedType)
				arg = MaterializeAsDefault(arg);
		}
		
		if (arg.Type == targetType && ConversionTable.ConstructsItself(targetType))
			return arg;
		
		var value = targetType is BorrowType ? arg : Decay(arg);
		if (value.Type == targetType && ConversionTable.ConstructsItself(targetType))
			return value;
		
		if (_conversionTable.FindExplicit(value.Type, targetType) is { } conversion)
			return new ResolvedConversionExpressionNode(value, conversion, node);
		
		if (FindUserConversion(arg, targetType, true) is { } userConversion)
			return ApplyUserConversion(arg, userConversion, node);
		
		return VisitConstructorCall(node, targetType, arg);
	}
	
	private IResolvedExpressionNode? VisitRecordConversion(CallExpressionNode node, RecordSymbol record)
	{
		if (node.Arguments is not [var argument] || record.IsGenericDefinition)
			return null;
		
		var fields = _typePool.GetMembers(record).OfType<FieldSymbol>().ToArray();
		ICallable[] memberwise = fields is [var field] && !record.IsUnion
			? [new FieldsCallable([GetMemberType(field)], record)]
			: [];
		
		CallShape[] shapes =
		[
			..memberwise.Select(static candidate => CreateShape(candidate, [])),
			..GetInwardConversions(record).Select(info =>
				CreateShape(new ConversionCallable(info, record), GetOpenTypeParameters(info)))
		];
		
		var checkpoint = OpenCheckpoint();
		var first = argument is ArrayExpressionNode ? VisitHinted(argument, record) : null;
		var args = ResolveArguments(Unspread(node.Arguments), shapes, out var isAmbiguous, Succeeds, null, first);
		var conversions = FindConversionCallables(args, record);
		if (conversions.Length == 0)
		{
			Rollback(checkpoint);
			return null;
		}
		
		Commit(checkpoint);
		if (isAmbiguous)
			return Error(node, $"Conversion to '{record.Name}' is ambiguous", record, node);
		
		var resolutionSet = ResolveConstruction([..memberwise, ..conversions], args, record);
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Conversion to '{record.Name}' is ambiguous", record, node);
		
		return resolutionSet is { HasResult: true } && resolutionSet[0].Callable is ConversionCallable conversion
			? CallConversion(conversion.Info, ApplyArgumentResolution(args, resolutionSet[0])[0], node)
			: VisitRecordConstruction(node, record, args);
		
		bool Succeeds(IResolvedExpressionNode[] values) =>
			ResolveConstruction([..memberwise, ..FindConversionCallables(values, record)], values, record).HasResult;
	}
	
	private IResolvedExpressionNode VisitRecordConstruction(CallExpressionNode node, RecordSymbol record,
		IResolvedExpressionNode[]? resolvedArgs = null)
	{
		var fields = _typePool.GetMembers(record).OfType<FieldSymbol>().ToArray();
		var hasDefault = _typePool.HasDefault(record);
		if (record.IsUnion && node.Arguments.Length > 0)
		{
			var args = resolvedArgs ?? node.Arguments.Select(a => VisitNode(a, null)).ToArray();
			if (AnyInvalid(args))
				return new ResolvedInvalidExpressionNode(node, record);
			
			var matching = args is [var argument]
				? fields.Where(field => GetMemberType(field) == Decay(argument).Type).ToArray()
				: [];
			
			return matching switch
			{
				[var field] => new ResolvedRecordExpressionNode(record,
					[(field, CoerceToType(args[0], GetMemberType(field)))], node),
				[_, _, ..] => Error(node, $"Conversion to '{record.Name}' is ambiguous", record, node),
				_ => Error(node, new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
					$"No constructor for '{record.Name}' accepts these arguments")
				{
					Hints = fields.Length == 0
						? [$"'{record.Name}' has no fields"]
						:
						[
							$"'{record.Name}' takes no arguments, or one value of a field's type: " +
							string.Join(", ",
								fields.Select(field => DiagnosticReporter.DescribeType(GetMemberType(field)))
									.Distinct())
						]
				}, record)
			};
		}
		
		if (node.Arguments.Length != fields.Length && (node.Arguments.Length > 0 || !hasDefault))
		{
			var args = resolvedArgs ?? node.Arguments.Select(a => VisitNode(a, null)).ToArray();
			if (AnyInvalid(args))
				return new ResolvedInvalidExpressionNode(node, record);
			
			var names = string.Join(", ", fields.Select(static f => f.Name));
			var diagnostic = new Diagnostic(DiagnosticSeverity.Error, node.SourceLocation,
				$"No constructor for '{record.Name}' accepts these arguments")
			{
				Hints =
				[
					(fields.Length, hasDefault) switch
					{
						(0, _) => $"'{record.Name}' has no fields",
						(_, true) => $"'{record.Name}' takes no arguments, or one for each field: {names}",
						_ => $"'{record.Name}' takes one argument for each field: {names}"
					}
				]
			};
			
			return Error(node, diagnostic, record);
		}
		
		var values = resolvedArgs is null
			? node.Arguments.Select((a, i) => VisitNode(a, GetMemberType(fields[i]))).ToArray()
			: resolvedArgs.Select((a, i) => CoerceToType(a, GetMemberType(fields[i]))).ToArray();
		
		return new ResolvedRecordExpressionNode(record, fields.Zip(values), node);
	}
	
	private IResolvedExpressionNode VisitInferredConstruction(CallExpressionNode node, RecordSymbol definition)
	{
		var constructors = _typePool.GetConstructors(definition);
		var accessible = constructors.Where(info => CanAccess(definition, info.Symbol.Visibility)).ToArray();
		ReceiverCallable[] unpacked =
			[..accessible.Select(constructor => new ReceiverCallable(constructor, definition))];
		
		if (RejectSpread(node, unpacked) is { } rejected)
			return rejected;
		
		var callables = Shape(unpacked, node.Arguments).Cast<ReceiverCallable>().ToArray();
		var fields = _typePool.GetMembers(definition).OfType<FieldSymbol>().ToArray();
		var name = GetName(node.Target);
		var location = node.Target.SourceLocation;
		CallShape[] shapes = constructors.Count > 0
			? [..callables.Select(callable => CreateShape(callable, definition.TypeParameters))]
			: [CreateShape([..fields.Select(GetMemberType)], definition)];
		
		var args = ResolveArguments(Unspread(node.Arguments), shapes, out var isAmbiguous, Succeeds, Accepts);
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (isAmbiguous)
			return Error(node, $"Conversion to '{definition.Name}' is ambiguous", CurrentTargetType, node);
		
		if (constructors.Count > 0)
			return ResolveInferredConstructor(node, definition, name, args, constructors, accessible, callables);
		
		if (args.Length != fields.Length && args.Length > 0)
			return VisitRecordConstruction(node, definition, args);
		
		var inputs = args.Length == 0
			? []
			: fields.Select((field, i) => CreateInferenceInput(GetMemberType(field), ParameterMode.Value, args[i]));
		
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			ExpectedType);
		
		if (InstantiateInferred(result, definition, name, location) is not RecordSymbol instance)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		return VisitRecordConstruction(node, instance, args);
		
		bool Succeeds(IResolvedExpressionNode[] values) => constructors.Count > 0
			? ResolveCallable(InstantiateConstructors(values), values, MaterializationMode.Overload).HasResult
			: AcceptsInferred(shapes[0], values);
		
		bool Accepts(int index, IResolvedExpressionNode[] values) => constructors.Count > 0
			? InstantiateConstructor(definition, callables[index], values, name, location, []) is { } callable &&
			  ResolveCallable([callable], values, MaterializationMode.Overload).HasResult
			: AcceptsInferred(shapes[0], values);
		
		ICallable[] InstantiateConstructors(IResolvedExpressionNode[] values) =>
		[
			..callables
				.Select(callable => InstantiateConstructor(definition, callable, values, name, location, []))
				.OfType<ICallable>()
		];
	}
	
	private IResolvedExpressionNode ResolveInferredConstructor(CallExpressionNode node, RecordSymbol definition,
		string name, IResolvedExpressionNode[] args, IReadOnlyList<FunctionInfo> constructors,
		FunctionInfo[] accessible, ReceiverCallable[] callables)
	{
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(node.SourceLocation, "new",
				constructors.Select(static info => info.Symbol)), CurrentTargetType);
		
		var failures = new List<Diagnostic>();
		var location = node.Target.SourceLocation;
		ICallable[] candidates =
		[
			..callables
				.Select(callable => InstantiateConstructor(definition, callable, args, name, location, failures))
				.OfType<ICallable>()
		];
		
		if (candidates.Length == 0 && callables.Length == 1 && failures is [var failure])
		{
			Diagnostics.Add(failure);
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		}
		
		return ResolveConstructorCall(node, definition, args, candidates, null);
	}
	
	private ReceiverCallable? InstantiateConstructor(RecordSymbol definition, ReceiverCallable callable,
		IReadOnlyList<IResolvedExpressionNode> args, string name, SourceLocation location, List<Diagnostic> failures)
	{
		var inputs = args
			.Take(callable.ParameterTypes.Length)
			.Select((arg, i) => CreateInferenceInput(callable.ParameterTypes[i], callable.GetMode(i), arg));
		
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			ExpectedType);
		
		if (InstantiateInferred(result, definition, name, location, failures) is not { } instance)
			return null;
		
		var info = _typePool.GetConstructors(instance).First(info => info.Symbol == callable.Info.Symbol);
		return new ReceiverCallable(info, instance, Repack(callable, info));
	}
	
	private NamedTypeSymbol? InstantiateInferred(InferenceResult result, NamedTypeSymbol definition, string name,
		SourceLocation location, List<Diagnostic>? failures = null)
	{
		var diagnostic = result.Succeeded
			? ReportConstraintViolation(definition.TypeParameters, result.Arguments, _ => location)
			: ReportInferenceFailure(result, name, location);
		
		if (diagnostic is null)
		{
			var instance = _typePool.Instantiate(definition, result.Arguments);
			TrackTypeInstance(CurrentResolutionContext, instance, location);
			return instance as NamedTypeSymbol;
		}
		
		if (failures is null)
			Diagnostics.Add(diagnostic);
		else
			failures.Add(diagnostic);
		
		return null;
	}
	
	private IResolvedExpressionNode VisitConstructorCall(CallExpressionNode node, TypeSymbol targetType,
		IResolvedExpressionNode? firstArg)
	{
		var constructors = _typePool.GetConstructors(targetType);
		ReceiverCallable[] accessible =
		[
			..constructors
				.Where(info => CanAccess(targetType, info.Symbol))
				.Select(info => new ReceiverCallable(info, targetType))
		];
		
		if (RejectSpread(node, accessible) is { } rejected)
			return rejected;
		
		var ctorCandidates = Shape(accessible, node.Arguments);
		if (firstArg is null && node.Arguments is [ArrayExpressionNode literal] &&
		    FindConvertedElementType(targetType, literal.Values.Length) is not null)
			firstArg = VisitHinted(literal, targetType);
		
		var shapes = ctorCandidates.Select(static candidate => CreateShape(candidate, [])).ToArray();
		var args = ResolveArguments(Unspread(node.Arguments), shapes, out var isAmbiguous,
			values => Accepts(ctorCandidates, values), (index, values) => Accepts([ctorCandidates[index]], values),
			firstArg);
		
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, targetType);
		
		if (accessible.Length == 0 && constructors.Count > 0)
			return Error(node, ReportHiddenMember(node.SourceLocation, "new",
				constructors.Select(static info => info.Symbol)), targetType);
		
		if (isAmbiguous)
			return Error(node, $"Conversion to '{targetType.Name}' is ambiguous", targetType, node);
		
		return ResolveConstructorCall(node, targetType, args, ctorCandidates, targetType);
		
		bool Accepts(ICallable[] candidates, IResolvedExpressionNode[] values) =>
			ResolveCallable(candidates, values, MaterializationMode.Overload, targetType).HasResult;
	}
	
	private IResolvedExpressionNode ResolveConstructorCall(CallExpressionNode node, TypeSymbol targetType,
		IResolvedExpressionNode[] args, ICallable[] ctorCandidates, TypeSymbol? target)
	{
		ICallable[] candidates = [..ctorCandidates, ..FindConversionCallables(args, targetType)];
		var resolutionSet = ResolveConstruction(candidates, args, target);
		var recovery = target ?? CurrentTargetType;
		
		if (resolutionSet.IsAmbiguous)
			return Error(node, $"Conversion to '{targetType.Name}' is ambiguous", recovery, node);
		
		if (!resolutionSet.HasResult)
		{
			var hidden = _typePool.GetConstructors(targetType).Where(info => !CanAccess(targetType, info.Symbol))
				.ToList();
			
			if (hidden.Count > 0 && ResolveCallable([..hidden.Select(info => new ReceiverCallable(info, targetType))],
				    args,
				    MaterializationMode.Overload, target).HasResult)
				return Error(node, ReportHiddenMember(node.SourceLocation, "new",
					hidden.Select(static info => info.Symbol)), recovery);
			
			if (ReportArgumentModes(candidates, args, target) ||
			    candidates.Any(static candidate => candidate.ParameterTypes.Any(static type => type is InvalidType)))
				return new ResolvedInvalidExpressionNode(node, recovery);
			
			if (targetType is EnumSymbol { IsMatch: true } matchEnum && args is [var only] &&
			    DescribeLiteralMismatch(only, _typePool.GetMatchedType(matchEnum)) is { } literalMismatch)
				return Error(node, literalMismatch, recovery, only.Syntax);
			
			var message = args is [not ResolvedCaseNameExpressionNode]
				? $"No constructor for '{DiagnosticReporter.DescribeType(targetType)}' accepts argument of type " +
				  $"'{DescribeWrittenType(args[0])}'"
				: $"No constructor for '{DiagnosticReporter.DescribeType(targetType)}' accepts these arguments";
			
			return Error(node, message, recovery, node);
		}
		
		var resolution = resolutionSet[0];
		if (resolution.Callable is ConversionCallable conversion)
			return CallConversion(conversion.Info, ApplyArgumentResolution(args, resolution)[0], node);
		
		var callable = (ReceiverCallable)resolution.Callable;
		var info = callable.Info;
		
		var resolvedArgs = ApplyArgumentResolution(args, resolution);
		if (GetForm(callable) is { } form)
			CompleteArguments(resolvedArgs, info, form, node);
		
		var result = new ResolvedConstructorCallExpressionNode(info, resolvedArgs, callable.ReturnType, node);
		
		if (resolution.ResultConversion is { } resultConversion && resultConversion.To != targetType)
			throw new InvalidOperationException(); // Should be impossible
		
		TrackFunctionUse(info, node.Target);
		
		return result;
	}
}