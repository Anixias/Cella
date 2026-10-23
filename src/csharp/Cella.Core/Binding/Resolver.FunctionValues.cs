using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode VisitIndirectCall(CallExpressionNode node, IResolvedExpressionNode target)
	{
		if (survey is { } activeSurvey && target.Type is FunctionGroupType group &&
		    !activeSurvey.GetTracked(target).IsEmpty)
			return CallOpenFunction(node, target, group);
		
		if (target.Type is UntypedType)
			target = MaterializeAsDefault(target);
		
		var callee = Decay(target);
		target = ResolveCallTarget(callee);
		ICallable[] candidates = target.Type is FunctionType type ? [new FunctionTypeCallable(type)] : [];
		if (RejectSpread(node, candidates) is { } rejected)
			return rejected;
		
		candidates = Shape(candidates, node.Arguments);
		var args = ResolveArguments(Unspread(node.Arguments),
			[..candidates.Select(static candidate => CreateShape(candidate, []))], out _);
		
		if (IsInvalid(target) || AnyInvalid(args) || TypePool.ContainsInvalid(callee.Type))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (target.Type is not FunctionType functionType)
			return Error(node, $"Cannot call a value of type '{DescribeWrittenType(callee)}'", CurrentTargetType,
				node.Target);
		
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		
		if (!resolutionSet.HasResult && ReportArgumentModes(candidates, args, null))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!resolutionSet.HasResult && candidates is [var only] &&
		    ReportSingleMismatch(node, node.Target is VarExpressionNode or AccessExpressionNode
			    ? GetName(node.Target)
			    : GetDisplayName(callee), only, only, args, node.Target.SourceLocation))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!resolutionSet.HasResult)
			return Error(node, $"'{GetDisplayName(callee)}' doesn't accept these arguments", CurrentTargetType,
				node.Target);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolutionSet[0]);
		if (GetForm(resolutionSet[0].Callable) is { } form)
			CompleteArguments(resolvedArgs, functionType.ParameterTypes, [], form, node);
		
		return new ResolvedIndirectCallExpressionNode(target, resolvedArgs, functionType, node);
	}
	
	private IResolvedExpressionNode CallOpenFunction(CallExpressionNode node, IResolvedExpressionNode callee,
		FunctionGroupType group)
	{
		var call = ResolveCall(node, group.FunctionName,
			[..group.Functions.Select(static function => new FunctionCallable(function))], null);
		
		if (call is ResolvedFunctionCallExpressionNode { Function: var function })
			RecordMaterialization(callee, GetNaturalType(function));
		
		return call;
	}
	
	private IResolvedExpressionNode ResolveCallTarget(IResolvedExpressionNode callee, bool dereferences = true)
	{
		if (callee.Type is FunctionType)
			return callee;
		
		if (_typePool.GetCallableSignatures(callee.Type) is [var signature])
			return new ResolvedConversionExpressionNode(callee,
				_conversionTable.FindImplicit(callee.Type, _typePool.GetRefFunctionType(signature))!, callee.Syntax);
		
		return dereferences && FindDereference(callee.Type, ParameterMode.Value) is not null
			? ResolveCallTarget(ResolveDereference(TokenType.OpStar, callee, callee.Syntax), false)
			: callee;
	}
	
	public IResolvedExpressionNode Visit(IndexerExpressionNode node) =>
		ResolveExplicitFunctionValue(node) ?? RejectTypeValue(node) ?? ResolveIndexing(node, VisitNode(node.Target));
	
	private IResolvedExpressionNode? RejectTypeValue(IndexerExpressionNode node) =>
		node.Target is VarExpressionNode or AccessExpressionNode &&
		CurrentResolutionContext.TryResolveExpressionAsType(node) is { } type
			? type is InvalidType
				? new ResolvedInvalidExpressionNode(node, CurrentTargetType)
				: Error(node, "Cannot use types as values", CurrentTargetType)
			: null;
	
	private IResolvedExpressionNode? ResolveExplicitFunctionValue(IndexerExpressionNode node)
	{
		var context = CurrentResolutionContext;
		MethodSymbol[] methods;
		TypeSymbol? owner = null;
		switch (node.Target)
		{
			case VarExpressionNode variable:
				methods = AsMethods(GetFunctions(context.Resolve(variable.Identifier.Text)));
				break;
			
			case AccessExpressionNode access when context.ResolveModule(access.Target) is { } module:
				methods = AsMethods(GetFunctions(context.ResolveMember(module, access.Member.Text)));
				break;
			
			case AccessExpressionNode access when context.TryResolveExpressionAsType(access.Target) is { } type:
				owner = RequireTypeArguments(type, access.Target);
				if (owner is InvalidType)
					return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
				
				methods = FindAccessible(owner, FindStatics(owner, access.Member.Text));
				break;
			
			default:
				return null;
		}
		
		if (methods.Length == 0)
			return null;
		
		FunctionInfo[] declared =
		[
			..methods.Select(method =>
				owner is null ? GetFunctionInfo(method.Function) : GetFunctionInfo(method, owner))
		];
		
		if (ResolveTypeArguments(node, declared) is not { } typeArguments)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var name = GetName(node.Target);
		var infos = new List<FunctionInfo>();
		foreach (var info in declared)
		{
			var open = GetOpenTypeParameters(info);
			if (_typePool.CompleteArguments(open, typeArguments.Types) is not { } completed)
				continue;
			
			var arguments = SubstituteOwnerArguments(info, completed);
			
			if (ReportConstraintViolation(open, arguments, typeArguments.Locate, info) is { } violation)
				return Error(node, violation, CurrentTargetType);
			
			infos.Add(InstantiateDeclared(info, arguments));
		}
		
		if (infos.Count > 0)
			return new ResolvedFunctionGroupExpressionNode(new FunctionGroupType(name, infos,
				infos is [var only] ? GetNaturalType(only).Name : name), node);
		
		var location = node.SourceLocation;
		return Error(node, methods is [var single]
			? ResolutionContext.ReportGenericArgumentCount(location, name, single.Function.DeclaredTypeParameters)
			: ReportOverloadArgumentCount(location, name, typeArguments.Types.Length), CurrentTargetType);
	}
	
	private static Diagnostic ReportOverloadArgumentCount(SourceLocation location, string name, int count) =>
		new(DiagnosticSeverity.Error, location,
			$"No overload of '{name}' takes {ResolutionContext.DescribeGenericArguments(count)}");
	
	private ResolvedInvalidExpressionNode RejectNamedValue(IExpressionNode node, Symbol symbol, string message)
	{
		if (RecordName is { } record && FindCallName(node) is { } name)
			record(name, symbol);
		
		return Error(node, message, CurrentTargetType);
	}
	
	public IResolvedExpressionNode Visit(VarExpressionNode node)
	{
		ProbeName(node.Identifier);
		if (node.Identifier.Text == "_")
			return Error(node, "Cannot use '_' as a value", CurrentTargetType);
		
		if (ResolveTargetCase(node.Identifier, node) is { } targetCase)
			return targetCase;
		
		var resolutionContext = CurrentResolutionContext;
		var varName = node.Identifier.Text;
		var symbol = resolutionContext.Resolve(varName);
		
		return symbol is null
			? Error(node, ReportUndefinedSymbol(node, varName), CurrentTargetType)
			: ResolveSymbolValue(node, symbol);
	}
	
	private IResolvedExpressionNode ResolveSymbolValue(IExpressionNode node, Symbol symbol)
	{
		if (symbol is LocalVariableSymbol used)
			MarkUsed(used);
		else
			MarkUsed(symbol);
		
		switch (symbol)
		{
			case LocalVariableSymbol { IsBorrowBinding: true, Type: PointerType } binding:
				return ResolveDereference(TokenType.OpStar, new ResolvedVarExpressionNode(binding, binding.Type, node),
					node);
			
			case LocalVariableSymbol v when survey?.Find(v) is { } open && node != storeTarget && node != placeTarget:
				return Substitute(open);
			
			case LocalVariableSymbol v:
				return new ResolvedVarExpressionNode(v, v.Type, node);
			
			case GlobalSymbol { Trait: { } trait } constant:
				return ResolveTypeConstant(node,
					new(trait.Self, _typePool.GetTraitType(trait, [..trait.TypeParameters]), constant));
			
			case GlobalSymbol { Impl: { } impl } constant when CurrentResolutionContext.ContainingType is { } target &&
			                                                   FindRequirement(impl, target, constant.Name) is { } use:
				return ResolveTypeConstant(node, use);
			
			case GlobalSymbol { Impl.IsExtension: true } constant when
				CurrentResolutionContext.ContainingType is { } target:
				return ResolveTypeConstant(node, new(target, null, constant));
			
			case GlobalSymbol g:
				return new ResolvedGlobalExpressionNode(g, _signatures.GetGlobalType(g), node);
			
			case PropertySymbol property:
				return ResolvePropertyUse(node, node.SourceLocation, property, property.ContainingType!, null);
			
			case ParameterSymbol { Mode: ParameterMode.Mut } parameter:
				var pointer = new ResolvedVarExpressionNode(parameter, _signatures.GetVariableType(parameter), node);
				return pointer.Type is PointerType ? ResolveDereference(TokenType.OpStar, pointer, node) : pointer;
			
			case VariableSymbol v:
				return new ResolvedVarExpressionNode(v, _signatures.GetVariableType(v), node);
			
			case FunctionSymbol or AmbiguousSymbol:
				return ResolveFunctionValue(node, symbol);
			
			case ModulePathSymbol:
				return RejectNamedValue(node, symbol, "Cannot use modules as values");
			
			case TypeParameterSymbol { IsValue: true } parameter:
				return new ResolvedValueParameterExpressionNode(parameter, node);
			
			case TraitSymbol or TypeParameterSymbol { IsTrait: true }:
				return RejectNamedValue(node, symbol, "Cannot use traits as values");
			
			case CapturedSymbol when node is VarExpressionNode variable:
				return ResolveCapture(variable);
			
			case TypeSymbol:
				return RejectNamedValue(node, symbol, "Cannot use types as values");
			
			default:
				return Error(node, $"Symbol '{GetName(node)}' is not a variable", CurrentTargetType);
		}
	}
	
	private FunctionSymbol[] GetFunctions(Symbol? symbol)
	{
		FunctionSymbol[] functions = symbol switch
		{
			FunctionSymbol f => [f],
			AmbiguousSymbol a when a.Candidates.All(static c => c is FunctionSymbol) =>
				RemoveShadowedImports([..a.Candidates.Cast<FunctionSymbol>()]),
			_ => []
		};
		
		foreach (var function in functions)
			MarkUsed(function);
		
		return functions;
	}
	
	private FunctionSymbol[] RemoveShadowedImports(FunctionSymbol[] functions)
	{
		var context = CurrentResolutionContext;
		var own = functions.Where(context.IsInModuleOf).Select(GetFunctionInfo).ToList();
		return own.Count == 0 || own.Count == functions.Length
			? functions
			:
			[
				..functions.Where(function => context.IsInModuleOf(function) ||
				                              !own.Any(info => HasSameParameters(info, GetFunctionInfo(function))))
			];
	}
	
	private static bool HasSameParameters(FunctionInfo a, FunctionInfo b) =>
		a.Signature.ParameterTypes.SequenceEqual(b.Signature.ParameterTypes) &&
		Enumerable.Range(0, a.Signature.ParameterTypes.Length)
			.All(i => a.Signature.GetMode(i) == b.Signature.GetMode(i));
	
	private static MethodSymbol[] AsMethods(FunctionSymbol[] functions) =>
		[..functions.Select(static function => new MethodSymbol(function.Name, function))];
	
	private static string GetName(IExpressionNode node) => node switch
	{
		VarExpressionNode v => v.Identifier.Text,
		AccessExpressionNode a => $"{GetName(a.Target)}.{a.Member.Text}",
		_ => node.SourceLocation.GetText().ToString()
	};
	
	private IResolvedExpressionNode ResolveFunctionValue(IExpressionNode node, Symbol symbol) =>
		GetFunctions(symbol) is { Length: > 0 } functions
			? CreateFunctionGroup(node, [..functions.Select(GetFunctionInfo)])
			: Error(node, $"Reference to '{GetName(node)}' is ambiguous", CurrentTargetType);
	
	private ResolvedFunctionGroupExpressionNode ResolveFunctionValue(IExpressionNode node, MethodSymbol[] methods,
		TypeSymbol owner) => CreateFunctionGroup(node, [..methods.Select(method => GetFunctionInfo(method, owner))]);
	
	private ResolvedFunctionGroupExpressionNode CreateFunctionGroup(IExpressionNode node, FunctionInfo[] infos)
	{
		var name = GetName(node);
		var typeName = infos is [var single] ? GetNaturalType(single).Name : name;
		return new ResolvedFunctionGroupExpressionNode(new FunctionGroupType(name, infos, typeName), node);
	}
	
	private FunctionType GetNaturalType(FunctionInfo function)
	{
		var signature = function.Signature;
		return _typePool.GetFunctionType(function.Symbol.IsExternal, signature.ParameterTypes,
			signature.ParameterTypes.Select((_, i) => signature.GetMode(i)), signature.ReturnType, false,
			signature.IsVariadic, signature.HasVariadicParameter);
	}
	
	private IResolvedExpressionNode MaterializeFunction(ResolvedFunctionGroupExpressionNode node, TypeSymbol target)
	{
		if (target is not FunctionType type || FindFunction(node.Group, type) is not { } function)
			return node;
		
		if (function.Symbol.Syntax is FunctionNode { When: not null })
			return Retain(node, Error(node.Syntax, "Cannot use 'when' functions as values",
				NativeSymbols.Invalid));
		
		TrackFunctionUse(function, node.Syntax);
		return new ResolvedFunctionReferenceExpressionNode(function, type, node.Syntax);
	}
	
	private IResolvedExpressionNode MaterializeFunctionAsDefault(ResolvedFunctionGroupExpressionNode node)
	{
		var group = node.Group;
		if (group.Functions is not [var function])
			return Retain(node, Error(node.Syntax, $"Reference to '{group.FunctionName}' is ambiguous",
				NativeSymbols.Invalid));
		
		if (GetOpenTypeParameters(function) is { IsEmpty: false } open)
			return Retain(node, Error(node.Syntax, ReportInferenceFailure(new() { Missing = open }, group.FunctionName,
				node.Syntax.SourceLocation), NativeSymbols.Invalid));
		
		if (function.Symbol.Syntax is FunctionNode { When: not null })
			return Retain(node, Error(node.Syntax, "Cannot use 'when' functions as values",
				NativeSymbols.Invalid));
		
		TrackFunctionUse(function, node.Syntax);
		return new ResolvedFunctionReferenceExpressionNode(function, GetNaturalType(function), node.Syntax);
	}
	
	private FunctionInfo? FindFunction(FunctionGroupType group, FunctionType type)
	{
		if (group.Find(type) is { } exact)
			return exact;
		
		foreach (var function in group.Functions)
		{
			if (InferFunctionValue(function, type) is not { } result ||
			    ReportConstraintViolation(GetOpenTypeParameters(function), result.Arguments, _ => default, function)
				    is not null)
				continue;
			
			var instantiated = InstantiateDeclared(function, result.Arguments);
			if (FunctionGroupType.Matches(instantiated, type, true) ||
			    _typePool.CopiesForImm(GetNaturalType(instantiated), type))
				return instantiated;
		}
		
		foreach (var function in group.Functions)
		{
			if (GetOpenTypeParameters(function).IsEmpty && _typePool.CopiesForImm(GetNaturalType(function), type))
				return function;
		}
		
		return null;
	}
	
	private InferenceResult? InferFunctionValue(FunctionInfo function, FunctionType type)
	{
		var signature = function.Signature;
		var open = GetOpenTypeParameters(function);
		if (open.IsEmpty || signature.ParameterTypes.Length != type.ParameterTypes.Length)
			return null;
		
		var inputs = signature.ParameterTypes
			.Select((parameter, i) => new InferenceInput(parameter, type.ParameterTypes[i]) { IsExact = true })
			.Append(new(signature.ReturnType, type.ReturnType) { IsExact = true });
		
		var result = _inference.Infer(open, inputs, null, null);
		return result.Succeeded ? result : null;
	}
	
	private ResolvedInvalidExpressionNode ReportFunctionMismatch(ResolvedFunctionGroupExpressionNode node,
		TypeSymbol target)
	{
		var group = node.Group;
		if (group.Functions.Any(static function => function.Signature.ParameterTypes
			    .Append(function.Signature.ReturnType).Any(TypePool.ContainsInvalid)))
			return new ResolvedInvalidExpressionNode(node.Syntax, target);
		
		if (group.Functions is [var only] && target is FunctionType type &&
		    InferFunctionValue(only, type) is { } result &&
		    ReportConstraintViolation(GetOpenTypeParameters(only), result.Arguments,
			    _ => node.Syntax.SourceLocation, only) is { } violation)
			return Retain(node, Error(node.Syntax, violation, target));
		
		var message = group.Functions switch
		{
			[_] => $"Cannot convert type '{group.Name}' to '{target.Name}'",
			_ => $"No overload of '{group.FunctionName}' matches '{target.Name}'"
		};
		
		return Retain(node, Error(node.Syntax, message, target));
	}
}