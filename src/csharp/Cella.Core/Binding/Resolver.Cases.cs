using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private EnumSymbol? ResolveEnumType(IExpressionNode node) =>
		CurrentResolutionContext.TryResolveExpressionAsType(node) as EnumSymbol;
	
	private IResolvedExpressionNode VisitEnumCase(AccessExpressionNode access, EnumSymbol enumType,
		CallExpressionNode? call)
	{
		if (enumType.IsGenericDefinition && access.Target is not IndexerExpressionNode)
			return VisitInferredEnumCase(access.Member, GetName(access.Target), access.Target.SourceLocation,
				enumType, call, call is null ? access : call);
		
		return ResolveCase(access.Member, enumType, call, call is null ? access : call);
	}
	
	private IResolvedExpressionNode ResolveCase(Token name, EnumSymbol enumType, CallExpressionNode? call,
		IExpressionNode node)
	{
		var arguments = call?.Arguments ?? [];
		var enumCase = FindCase(enumType, name);
		ImmutableArray<TypeSymbol> payloadTypes = enumCase is null ? [] : _typePool.GetPayloadTypes(enumType, enumCase);
		
		if (enumCase is null || arguments.Length != payloadTypes.Length || call is not null && payloadTypes.IsEmpty)
		{
			var values = arguments.Select(argument => VisitNode(argument, null)).ToArray();
			if (enumCase is not null && !AnyInvalid(values))
				Diagnostics.Add(ReportPayloadCount(node.SourceLocation, enumType, enumCase));
			
			return new ResolvedInvalidExpressionNode(node, enumType);
		}
		
		var payload = arguments.Select((argument, i) => VisitNode(argument, payloadTypes[i]));
		return new ResolvedEnumCaseExpressionNode(enumType, enumCase, payload, node);
	}
	
	private IResolvedExpressionNode VisitInferredEnumCase(Token member, string name, SourceLocation location,
		EnumSymbol definition, CallExpressionNode? call, IExpressionNode node)
	{
		var named = definition.Cases.FirstOrDefault(c => c.Name == member.Text);
		var shape = CreateShape(named is null ? [] : _typePool.GetPayloadTypes(definition, named), definition);
		var args = ResolveArguments(call?.Arguments ?? [], [shape], out _, values => AcceptsInferred(shape, values),
			(_, values) => AcceptsInferred(shape, values));
		
		if (FindCase(definition, member) is not { } enumCase || AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var payloadTypes = _typePool.GetPayloadTypes(definition, enumCase);
		if (args.Length != payloadTypes.Length || call is not null && payloadTypes.IsEmpty)
			return Error(node, ReportPayloadCount(node.SourceLocation, definition, enumCase), CurrentTargetType);
		
		var inputs = args.Select((arg, i) => CreateInferenceInput(payloadTypes[i], ParameterMode.Value, arg));
		var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(), definition,
			ExpectedType);
		
		if (InstantiateInferred(result, definition, name, location) is not EnumSymbol instance)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var instanceCase = instance.Cases[enumCase.Index];
		var instanceTypes = _typePool.GetPayloadTypes(instance, instanceCase);
		var payload = args.Select((arg, i) => CoerceToType(arg, instanceTypes[i]));
		return new ResolvedEnumCaseExpressionNode(instance, instanceCase, payload, node);
	}
	
	private EnumCaseSymbol? FindCase(EnumSymbol enumType, Token name)
	{
		if (enumType.Cases.FirstOrDefault(c => c.Name == name.Text) is { } enumCase)
			return enumCase;
		
		Diagnostics.Add(DiagnosticReporter.ReportUndefinedCase(name.SourceLocation, enumType.Name, name.Text,
			enumType.Cases.Select(static c => c.Name)));
		
		return null;
	}
	
	private static Diagnostic ReportPayloadCount(SourceLocation location, EnumSymbol enumType,
		EnumCaseSymbol enumCase)
	{
		var name = $"'{enumType.Name}.{enumCase.Name}'";
		var fields = string.Join(", ", enumCase.Fields.Select(static f => f.Name));
		var message = enumCase.Fields.Length switch
		{
			0 => $"{name} holds no values",
			1 => $"{name} holds 1 value: {fields}",
			var count => $"{name} holds {count} values: {fields}"
		};
		
		return new Diagnostic(DiagnosticSeverity.Error, location, message);
	}
	
	private EnumSymbol? FindGenericCaseEnum(IExpressionNode node) =>
		node is AccessExpressionNode access &&
		CurrentResolutionContext.TryResolveExpressionAsType(access.Target) is EnumSymbol
		{
			IsGenericDefinition: true
		} definition
			? definition
			: null;
	
	private ResolvedCaseNameExpressionNode? VisitCaseName(IExpressionNode node, IEnumerable<TypeSymbol> expected)
	{
		var name = GetCaseToken(node);
		return name is { } token && expected.Any(type => HasCase(type, token.Text))
			? CreateCaseName(token, node)
			: null;
	}
	
	private ResolvedCaseNameExpressionNode CreateCaseName(Token name, IExpressionNode node)
	{
		var context = CurrentResolutionContext;
		var near = context.ResolveNear(name.Text);
		return new ResolvedCaseNameExpressionNode(name, near ?? context.Resolve(name.Text), near is not null, node);
	}
	
	private static IEnumerable<TypeSymbol> ParameterTypesAt(IEnumerable<ICallable> candidates, int index) => candidates
		.Where(candidate => index < candidate.ParameterTypes.Length)
		.Select(candidate => candidate.ParameterTypes[index]);
	
	private static bool HasCase(TypeSymbol? type, string name) =>
		type is EnumSymbol enumType && enumType.Cases.Any(enumCase => enumCase.Name == name);
	
	private IResolvedExpressionNode? ResolveTargetCase(Token name, IExpressionNode node)
	{
		if (CurrentTargetType is not EnumSymbol target || !HasCase(target, name.Text))
			return null;
		
		var caseName = CreateCaseName(name, node);
		return ChooseCase(target, caseName) == CaseChoice.Symbol ? null : ResolveCaseName(caseName, target);
	}
	
	private IResolvedExpressionNode ResolveCaseName(ResolvedCaseNameExpressionNode caseName, TypeSymbol target)
	{
		RecordMaterialization(caseName, target);
		if (target is not EnumSymbol enumType)
			return GetFallback(caseName);
		
		return ChooseCase(enumType, caseName) switch
		{
			CaseChoice.Case => ResolveCase(caseName.Name, enumType, caseName.Syntax as CallExpressionNode,
				caseName.Syntax),
			CaseChoice.Ambiguous => Error(caseName.Syntax, $"'{caseName.Name.Text}' is ambiguous", target,
				caseName.Name.SourceLocation),
			_ => GetFallback(caseName)
		};
	}
	
	private CaseChoice ChooseCase(EnumSymbol enumType, ResolvedCaseNameExpressionNode caseName)
	{
		if (!HasCase(enumType, caseName.Name.Text))
			return CaseChoice.Symbol;
		
		if (caseName.Symbol is not { } symbol)
			return CaseChoice.Case;
		
		if (!IsViableCase(enumType, caseName))
			return CaseChoice.Symbol;
		
		return caseName.IsNear && CouldProduce(symbol, caseName.Syntax is CallExpressionNode, enumType)
			? CaseChoice.Ambiguous
			: CaseChoice.Case;
	}
	
	private static bool IsViableCase(EnumSymbol enumType, ResolvedCaseNameExpressionNode caseName) =>
		enumType.Cases.FirstOrDefault(enumCase => enumCase.Name == caseName.Name.Text) is { } enumCase &&
		(caseName.Syntax is CallExpressionNode call
			? !enumCase.Fields.IsEmpty && call.Arguments.Length == enumCase.Fields.Length
			: enumCase.Fields.IsEmpty);
	
	private IResolvedExpressionNode GetFallback(ResolvedCaseNameExpressionNode caseName)
	{
		if (_caseNameFallbacks.TryGetValue(caseName, out var fallback))
			return fallback;
		
		if (!_resolvingFallbacks.Add(caseName.Syntax))
			return new ResolvedInvalidExpressionNode(caseName.Syntax);
		
		_caseNameFallbacks[caseName] = fallback = VisitNode(caseName.Syntax, null);
		_resolvingFallbacks.Remove(caseName.Syntax);
		return fallback;
	}
	
	private IResolvedExpressionNode ResolveUnmatched(IResolvedExpressionNode node, TypeSymbol? peer = null) =>
		node switch
		{
			ResolvedCaseNameExpressionNode caseName when HasCase(peer, caseName.Name.Text) =>
				MaterializeExpression(caseName, peer!),
			ResolvedCaseNameExpressionNode caseName => Decay(GetFallback(caseName)),
			_ => node
		};
	
	private bool CouldProduce(Symbol symbol, bool isCall, TypeSymbol target)
	{
		var valueType = GetValueType(symbol);
		if (!isCall)
			return valueType is not null && CouldConvert(valueType, target);
		
		return GetFunctions(symbol)
			       .Any(function => CouldConvert(GetFunctionInfo(function).Signature.ReturnType, target)) ||
		       valueType is FunctionType functionType && CouldConvert(functionType.ReturnType, target);
	}
	
	private bool CouldConvert(TypeSymbol type, TypeSymbol target)
	{
		if (type is BorrowType borrow)
			type = borrow.Target;
		
		return type == target || type is NeverType || _conversionTable.FindImplicit(type, target) is not null ||
		       FindUserConversion(type, null, ParameterMode.Value, target) is not null ||
		       TypePool.ContainsTypeParameters(type) && type.OriginalDefinition == target.OriginalDefinition;
	}
	
	private TypeSymbol? GetValueType(Symbol symbol) => symbol switch
	{
		LocalVariableSymbol { IsBorrowBinding: true, Type: PointerType pointer } => pointer.BaseType,
		LocalVariableSymbol local => local.Type,
		GlobalSymbol global => _signatures.GetGlobalType(global),
		ParameterSymbol { Mode: ParameterMode.Mut } parameter when
			_signatures.GetVariableType(parameter) is PointerType pointer => pointer.BaseType,
		VariableSymbol variable => _signatures.GetVariableType(variable),
		PropertySymbol { Getter: FunctionAccessor { Function: var getter } } property =>
			GetFunctionInfo(getter, property.ContainingType!, property.TraitArguments).Signature.ReturnType,
		_ => null
	};
	
	private IResolvedExpressionNode VisitArgument(IExpressionNode node)
	{
		if (node is not BorrowExpressionNode { IsMutable: true } argument)
			return VisitNode(node, null);
		
		var outerTarget = placeTarget;
		placeTarget = argument.Value;
		var place = VisitNode(argument.Value, null);
		placeTarget = outerTarget;
		if (IsInvalid(place))
			return new ResolvedInvalidExpressionNode(argument);
		
		if (place.Type is UntypedType)
			place = MaterializeAsDefault(place);
		
		return new ResolvedMutArgumentExpressionNode(MakeWritable(place), _typePool.GetPointerType(place.Type),
			argument);
	}
}