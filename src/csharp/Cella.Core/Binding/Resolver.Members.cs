using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode VisitMemberCall(CallExpressionNode node, AccessExpressionNode access,
		IndexerExpressionNode? indexer = null)
	{
		if (FindTraitView(access.Target) is var (trait, view))
			return VisitTraitCall(node, access, trait, view, indexer);
		
		if (CurrentResolutionContext.TryResolveExpressionAsType(access.Target) is { } type)
			return indexer is not null && type is EnumSymbol && HasCase(type, access.Member.Text)
				? Error(node, "Cannot pass type arguments to cases", CurrentTargetType, indexer.SourceLocation)
				: VisitStaticCall(node, access, ResolveStaticOwner(node, access, type), indexer);
		
		if (access.Member.Type != TokenType.Identifier)
			return Error(node, $"Cannot call '{access.Member.Text}' through a value", CurrentTargetType,
				access.Member.SourceLocation);
		
		var target = Decay(VisitNode(access.Target, null));
		if (target is ResolvedLiteralExpressionNode { Type: UntypedType })
			target = MaterializeAsDefault(target);
		
		if (IsInvalid(target))
			return VisitIndirectCall(node, target);
		
		if (target.Type is FStrType fstr && FindFStrPart(access.Member.Text) is { } part)
			return ResolveFStrPart(node, access, target, fstr, part, indexer);
		
		var owner = GetMemberOwner(target.Type, access.Member.Text);
		if (owner is DynType dyn)
			return VisitDynCall(node, access,
				owner == target.Type ? target : ResolveDereference(TokenType.OpStar, target, access.Target), dyn,
				indexer);
		
		if (_typePool.ResolveMember(owner, access.Member.Text) is FieldSymbol or PropertySymbol ||
		    GetPropertyMember(owner, access.Member.Text) is not null)
			return VisitIndirectCall(node, Index(indexer, ResolveAccess(access, target)));
		
		var functions = FindFunctions(owner, access.Member.Text);
		var methods = functions.Where(static function => function.HasReceiver).ToArray();
		if (methods.Length == 0)
			return Error(node, DescribeMissingMember(owner, access.Member.Text), CurrentTargetType,
				access.Member.SourceLocation);
		
		var accessible = methods.Where(method => CanAccess(owner, method.Function)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(access.Member.SourceLocation, access.Member.Text,
				methods.Select(static method => method.Function)), CurrentTargetType);
		
		if (owner != target.Type)
			target = ResolveDereference(TokenType.OpStar, target, access.Target);
		
		ICallable[] candidates =
		[
			..accessible
				.Select(method => GetFunctionInfo(method, owner))
				.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (indexer is null)
			return ResolveCall(node, access.Member.Text, candidates, target);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, access.Member.Text, candidates, target, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private static FStrPart? FindFStrPart(string name) => name switch
	{
		"piece" => FStrPart.Piece,
		"spec" => FStrPart.Spec,
		"value" => FStrPart.Value,
		_ => null
	};
	
	private IResolvedExpressionNode ResolveFStrPart(CallExpressionNode node, AccessExpressionNode access,
		IResolvedExpressionNode target, FStrType type, FStrPart part, IndexerExpressionNode? indexer)
	{
		if (indexer is not null || node.Arguments is not [var argument] || argument is BorrowExpressionNode)
		{
			foreach (var extra in node.Arguments)
				VisitArgument(extra);
			
			return Error(node, $"No overload of '{access.Member.Text}' accepts these arguments", CurrentTargetType,
				node.Target);
		}
		
		var index = VisitNode(argument, NativeSymbols.UIntSize);
		if (IsInvalid(index))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		TypeSymbol resultType = part == FStrPart.Value ? _typePool.GetBorrowType(type.Value, false) : NativeSymbols.Str;
		return new ResolvedFStrPartExpressionNode(resultType, target, part, index, node);
	}
	
	private IResolvedExpressionNode Index(IndexerExpressionNode? indexer, IResolvedExpressionNode target) =>
		indexer is null ? target : ResolveIndexing(indexer, target);
	
	private TypeSymbol ResolveStaticOwner(CallExpressionNode node, AccessExpressionNode access, TypeSymbol type) =>
		type is NamedTypeSymbol { IsGenericDefinition: true } definition && access.Target is not IndexerExpressionNode
			? InferStaticOwner(node, access, definition) ?? NativeSymbols.Invalid
			: RequireTypeArguments(type, access.Target);
	
	private TypeSymbol? InferStaticOwner(CallExpressionNode node, AccessExpressionNode access,
		NamedTypeSymbol definition)
	{
		var name = access.Member.Text;
		var statics = FindAccessible(definition, FindStatics(definition, name));
		if (statics.Length == 0 || node.Arguments.Any(static argument => argument is SpreadExpressionNode))
			return RequireTypeArguments(definition, access.Target);
		
		var trial = OpenCheckpoint();
		var args = node.Arguments.Select(argument => VisitNode(argument, null)).ToArray();
		var failures = new List<Diagnostic>();
		var location = access.Target.SourceLocation;
		var owners = new List<NamedTypeSymbol>();
		foreach (var method in statics)
		{
			var signature = GetFunctionInfo(method, definition).Signature;
			if (signature.ParameterTypes.Length < args.Length)
				continue;
			
			var inputs = args.Select((arg, i) =>
				CreateInferenceInput(signature.ParameterTypes[i], signature.GetMode(i), arg));
			
			var result = _inference.Infer(definition.TypeParameters, inputs.OfType<InferenceInput>(),
				signature.ReturnType, ExpectedType);
			
			if (InstantiateInferred(result, definition, definition.Name, location, failures) is { } owner &&
			    !owners.Contains(owner))
				owners.Add(owner);
		}
		
		Rollback(trial);
		switch (owners)
		{
			case [var only]:
				return only;
			
			case []:
				Diagnostics.Add(failures.FirstOrDefault() ??
				                ResolutionContext.ReportGenericArgumentCount(location, definition));
				
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, access.Member.SourceLocation,
					$"Call to '{GetName(access)}' is ambiguous"));
				
				return null;
		}
	}
	
	private IResolvedExpressionNode VisitStaticCall(CallExpressionNode node, AccessExpressionNode access,
		TypeSymbol type, IndexerExpressionNode? indexer = null)
	{
		if (type is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (type.GetStaticField(access.Member.Text) is not null ||
		    GetPropertyMember(type, access.Member.Text) is not null)
			return VisitIndirectCall(node, Index(indexer, VisitStaticMember(access, type)));
		
		var statics = access.Member.Type == TokenType.Identifier
			? FindStatics(type, access.Member.Text)
			: FindFunctions(type, access.Member.Text);
		
		if (statics.Length == 0)
			return VisitBuiltInPick(node, access.Member, type) ??
			       Error(node, DescribeMissingStatic(type, access.Member.Text), CurrentTargetType,
				       access.Member.SourceLocation);
		
		var accessible = FindAccessible(type, statics);
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(access.Member.SourceLocation, access.Member.Text,
				statics.Select(static method => method.Function)), CurrentTargetType);
		
		ICallable[] candidates =
		[
			..accessible
				.Select(method => GetFunctionInfo(method, type))
				.Select(static info => new FunctionCallable(info))
		];
		
		if (indexer is null)
			return ResolveCall(node, GetName(access), candidates, null);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, GetName(access), candidates, null, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IResolvedExpressionNode? VisitBuiltInPick(CallExpressionNode node, Token op, TypeSymbol type)
	{
		if (op.Type == TokenType.Identifier || type is not (PrimitiveType or PointerType))
			return null;
		
		var rejected = $"'{type.Name}.{op.Text}' doesn't accept these arguments";
		if (IsAssignment(op.Type))
		{
			if (node.Arguments is not [BorrowExpressionNode { IsMutable: true } target, var value])
				return Error(node, rejected, CurrentTargetType);
			
			var assignment = VisitNode(new BinaryOpExpressionNode(target.Value, op, value), CurrentTargetType);
			return assignment is ResolvedAssignmentExpressionNode { Left.Type: var left } && left != type
				? Error(node, rejected, CurrentTargetType)
				: assignment;
		}
		
		List<ICallable> unary = [.._operatorRegistry.GetUnaryCandidates(op.Type).Where(TakesType)];
		List<ICallable> binary = [.._operatorRegistry.GetBinaryCandidates(op.Type).Where(TakesType)];
		if (unary.Count + binary.Count == 0)
			return null;
		
		var candidates = node.Arguments.Length switch
		{
			1 => unary,
			2 => binary,
			_ => []
		};
		
		if (candidates.Count == 0)
			return Error(node, rejected, CurrentTargetType);
		
		IResolvedExpressionNode[] args = [..node.Arguments.Select(argument => Decay(VisitNode(argument, type)))];
		if (AnyInvalid(args))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var resolutionSet = ResolveCallable(candidates, args, MaterializationMode.Overload);
		if (!resolutionSet.HasResult || resolutionSet.IsAmbiguous)
			return Error(node, rejected, CurrentTargetType);
		
		var resolvedArgs = ApplyArgumentResolution(args, resolutionSet[0]);
		var operation = (OperationImpl)resolutionSet[0].Callable;
		IResolvedExpressionNode result = resolvedArgs.Count == 1
			? new ResolvedUnaryOpExpressionNode(resolvedArgs[0], operation, node)
			: new ResolvedBinaryOpExpressionNode(resolvedArgs[0], resolvedArgs[1], operation, node);
		
		return ApplyResultResolution(result, resolutionSet[0]);
		
		bool TakesType(ICallable candidate) => candidate.ParameterTypes.Contains(type);
	}
	
	private IResolvedExpressionNode VisitStaticMember(AccessExpressionNode node, TypeSymbol type)
	{
		if (type is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (type.GetStaticField(node.Member.Text) is { } field)
			return CanAccess(type, field.Visibility)
				? _signatures.GetGlobalInfo(field) is { Fault: not null }
					? Error(node, $"'{type.Name}.{field.Name}' has no constant value", CurrentTargetType)
					: ResolveSymbolValue(node, field)
				: Error(node, DiagnosticReporter.ReportHidden(node.Member.SourceLocation, field.Name, field.Visibility,
					true), CurrentTargetType);
		
		if (GetPropertyMember(type, node.Member.Text) is { } property)
			return IsAmbiguousProperty(type, node.Member.Text)
				? Error(node, $"Reference to '{property.Name}' is ambiguous", CurrentTargetType,
					node.Member.SourceLocation)
				: ResolvePropertyUse(node, node.Member.SourceLocation, property, type, null);
		
		var statics = FindStatics(type, node.Member.Text);
		if (statics.Length == 0)
			return Error(node, DescribeMissingStatic(type, node.Member.Text), CurrentTargetType,
				node.Member.SourceLocation);
		
		var accessible = FindAccessible(type, statics);
		return accessible.Length > 0
			? ResolveFunctionValue(node, accessible, type)
			: Error(node, ReportHiddenMember(node.Member.SourceLocation, node.Member.Text,
				statics.Select(static method => method.Function)), CurrentTargetType);
	}
	
	private bool DeclaresNonCaseMember(TypeSymbol type, string name) => FindFunctions(type, name).Length > 0 ||
	                                                                    type.GetStaticField(name) is not null ||
	                                                                    GetPropertyMember(type, name) is not null;
	
	private IResolvedExpressionNode ResolvePropertyUse(IExpressionNode node, SourceLocation member,
		PropertySymbol property, TypeSymbol owner, IResolvedExpressionNode? receiver)
	{
		if (FindReceiverError(property.IsStatic, receiver) is { } receiverError)
			return Error(node, receiverError, CurrentTargetType, member);
		
		if (!CanAccess(owner, property))
			return Error(node, ReportHidden(member, property.Name, property.Visibility, property.Trait, property.Impl),
				CurrentTargetType);
		
		if (node == storeTarget)
			return new ResolvedPropertyExpressionNode(property, owner, receiver, GetPropertyType(property, owner),
				node);
		
		property = WithDefaultAccessor(property, owner, false);
		if (property.Getter is not FunctionAccessor { Function: var getter })
			return Error(node, "Cannot read write-only properties", CurrentTargetType, member);
		
		if (owner is DynType dyn && !IsDynMember(getter))
			return Error(node, $"Cannot use '{property.Name}' through '{dyn.Name}'", CurrentTargetType, member);
		
		return CanAccess(owner, getter)
			? CallAccessor(getter, owner, property.TraitArguments, receiver, [], node)
			: Error(node, DiagnosticReporter.ReportWriteOnly(member, property.Name, getter.Visibility),
				CurrentTargetType);
	}
	
	private static string? FindReceiverError(bool isStatic,
		IResolvedExpressionNode? receiver) => (isStatic, receiver) switch
	{
		(true, not null) => "Cannot use static properties through values",
		(false, null) => "Cannot use properties through types",
		_ => null
	};
	
	private TypeSymbol GetPropertyType(PropertySymbol property, TypeSymbol owner)
	{
		if (property.Getter is FunctionAccessor { Function: var getter })
			return GetFunctionInfo(getter, owner, property.TraitArguments).Signature.ReturnType;
		
		if (property.Setter is not FunctionAccessor { Function: var setter })
			return NativeSymbols.Invalid;
		
		var signature = GetFunctionInfo(setter, owner, property.TraitArguments).Signature;
		return signature.GetDeclaredType(signature.ParameterTypes.Length - 1);
	}
	
	private ResolvedFunctionCallExpressionNode CallAccessor(FunctionSymbol accessor, TypeSymbol owner,
		ImmutableArray<TypeSymbol> traitArguments, IResolvedExpressionNode? receiver,
		IEnumerable<IResolvedExpressionNode> arguments, IExpressionNode syntax)
	{
		var info = GetFunctionInfo(accessor, owner, traitArguments);
		TrackFunctionUse(info, syntax);
		IEnumerable<IResolvedExpressionNode> receivers = receiver is null ? [] : [CreateReceiver(receiver, info)];
		return new ResolvedFunctionCallExpressionNode(info, [..receivers, ..arguments], syntax);
	}
	
	private static bool TakesMutSelf(FunctionInfo accessor) => accessor.Signature.GetMode(0) == ParameterMode.Mut;
	
	private static SourceLocation GetMemberLocation(IExpressionNode syntax) =>
		syntax is AccessExpressionNode access ? access.Member.SourceLocation : syntax.SourceLocation;
	
	private MethodSymbol[] FindAccessible(TypeSymbol owner, IEnumerable<MethodSymbol> methods) =>
		[..methods.Where(method => CanAccess(owner, method.Function))];
	
	private bool HasLocalDefault(TypeSymbol type) =>
		type is RecordSymbol && !_typePool.HasDefault(type) && _typePool.GetConstructors(type)
			.Any(constructor => _typePool.IsDefaultConstructor(constructor) && CanAccess(type, constructor.Symbol));
	
	private bool CanAccess(TypeSymbol owner, Visibility visibility) =>
		CurrentResolutionContext.CanAccess(owner, visibility);
	
	private bool CanAccess(TypeSymbol owner, FunctionSymbol function) =>
		CanAccess(owner, function.Visibility, function.Trait, function.Impl);
	
	private bool CanAccess(TypeSymbol owner, PropertySymbol property) =>
		CanAccess(owner, property.Visibility, property.Trait, property.Impl);
	
	private bool CanAccess(TypeSymbol owner, Visibility visibility, TraitSymbol? trait, ImplSymbol? impl)
	{
		if (trait is null && impl is null)
			return CanAccess(owner, visibility);
		
		var context = CurrentResolutionContext;
		if (visibility == Visibility.Private)
			return trait is null ? context.ImplBlock == impl : context.Trait == trait;
		
		if (visibility == Visibility.Module && trait is not null)
			return context.IsInModuleOf(trait);
		
		return impl is not { Node.Traits.IsEmpty: true } || CanAccess(owner, visibility);
	}
	
	private IEnumerable<MethodSymbol> GetMethods(TypeSymbol type, string name) =>
		type.GetFunctions(name).Concat(GetTraitMethods(type, name));
	
	private IEnumerable<MethodSymbol> GetTraitMethods(TypeSymbol type, string name)
	{
		if (type is DynType dyn)
			return GetDynTraits(dyn)
				.SelectMany(trait => GetInstanceMethods(trait, name))
				.Where(method => IsDynMember(method.Function));
		
		if (type is TypeParameterSymbol parameter)
			return _typePool.GetBounds(parameter).SelectMany(trait => GetInstanceMethods(trait, name));
		
		var conformances = GetVisibleConformances(type).ToList();
		return GetImpls(conformances, type)
			.SelectMany(impl => impl.Functions.Where(method => method.Name == name))
			.Concat(conformances.SelectMany(conformance =>
				GetInstanceMethods(_typePool.GetConformanceTrait(conformance, type), name)
					.Where(method => method.Function.Visibility == Visibility.Private ||
					                 IsDefaultWitness(conformance, method.Function))));
	}
	
	private static IEnumerable<MethodSymbol> GetInstanceMethods(TraitType trait, string name) => trait.Arguments.IsEmpty
		? trait.Trait.GetFunctions(name)
		: trait.Trait.GetFunctions(name).Select(method => new MethodSymbol(method.Name, method.Function)
		{
			TraitArguments = trait.Arguments
		});
	
	private static PropertySymbol? GetInstanceProperty(TraitType trait, string name) =>
		trait.Trait.GetProperty(name) is { } property && !trait.Arguments.IsEmpty
			? new PropertySymbol(property.Name)
			{
				BackingField = property.BackingField,
				Getter = property.Getter,
				Setter = property.Setter,
				Node = property.Node,
				Visibility = property.Visibility,
				ContainingType = property.ContainingType,
				Trait = property.Trait,
				Impl = property.Impl,
				TraitArguments = trait.Arguments
			}
			: trait.Trait.GetProperty(name);
	
	private PropertySymbol WithDefaultAccessor(PropertySymbol property, TypeSymbol owner, bool isSetter)
	{
		if ((isSetter ? property.Setter : property.Getter) is FunctionAccessor || property.Trait is not null)
			return property;
		
		return GetVisibleConformances(owner)
			.Select(conformance => conformance.Trait.GetProperty(property.Name) is { } traitProperty &&
			                       (isSetter ? traitProperty.Setter : traitProperty.Getter) is FunctionAccessor
			                       {
				                       Function: var accessor
			                       } && IsDefaultWitness(conformance, accessor)
				? GetInstanceProperty(_typePool.GetConformanceTrait(conformance, owner), property.Name)
				: null)
			.OfType<PropertySymbol>()
			.FirstOrDefault() ?? property;
	}
	
	private static bool IsDefaultWitness(Conformance conformance, FunctionSymbol requirement) =>
		conformance.Witnesses.GetValueOrDefault(requirement) is FunctionWitness { Function: var witness } &&
		witness == requirement;
	
	private IEnumerable<Conformance> GetVisibleConformances(TypeSymbol type) => _typePool.FindConformances(type)
		.Where(conformance => CurrentResolutionContext.IsVisible(conformance.Trait));
	
	private IEnumerable<ImplSymbol> GetImpls(IEnumerable<Conformance> conformances, TypeSymbol type) => conformances
		.Select(static conformance => conformance.Impl)
		.OfType<ImplSymbol>()
		.Concat(_typePool.FindMemberBlocks(type))
		.Distinct();
	
	private PropertySymbol? GetPropertyMember(TypeSymbol type, string name) =>
		FindPropertyCandidates(type, name).FirstOrDefault();
	
	private bool IsAmbiguousProperty(TypeSymbol type, string name) =>
		FindPropertyCandidates(type, name).Skip(1).Any();
	
	private IEnumerable<PropertySymbol> FindPropertyCandidates(TypeSymbol type, string name)
	{
		if (type.GetProperty(name) is { } property)
			return [property];
		
		if (type is DynType dyn)
			return GetDynTraits(dyn)
				.Select(trait => GetInstanceProperty(trait, name))
				.OfType<PropertySymbol>();
		
		if (type is TypeParameterSymbol parameter)
			return _typePool.GetBounds(parameter)
				.Select(trait => GetInstanceProperty(trait, name))
				.OfType<PropertySymbol>();
		
		var conformances = GetVisibleConformances(type).ToList();
		return GetImpls(conformances, type)
			.Select(impl => impl.Properties.FirstOrDefault(p => p.Name == name))
			.OfType<PropertySymbol>()
			.Concat(conformances
				.Select(conformance => conformance.Trait.GetProperty(name) is { } traitProperty &&
				                       (traitProperty.Visibility == Visibility.Private ||
				                        GetAccessors(traitProperty).Any(accessor =>
					                        IsDefaultWitness(conformance, accessor)))
					? GetInstanceProperty(_typePool.GetConformanceTrait(conformance, type), name)
					: null)
				.OfType<PropertySymbol>());
	}
	
	private static IEnumerable<FunctionSymbol> GetAccessors(PropertySymbol property) =>
		new[] { property.Getter, property.Setter }.OfType<FunctionAccessor>().Select(static a => a.Function);
	
	private (TraitType? Trait, IExpressionNode Argument)? FindTraitView(IExpressionNode node)
	{
		if (node is not CallExpressionNode { Arguments: [var argument] } call)
			return null;
		
		var context = CurrentResolutionContext;
		var symbol = (call.Target is IndexerExpressionNode indexer ? indexer.Target : call.Target) switch
		{
			VarExpressionNode name => context.Resolve(name.Identifier.Text),
			AccessExpressionNode access when context.ResolveModule(access.Target) is { } module =>
				context.ResolveMember(module, access.Member.Text),
			_ => null
		};
		
		return symbol is TraitSymbol ? (context.ResolveTraitArgument(call.Target) as TraitType, argument) : null;
	}
	
	private static string DescribeMissingTraitMember(TraitType trait, string name, MethodSymbol[] functions,
		bool throughValue) => functions.Length == 0 ? $"Trait '{trait.Name}' has no member '{name}'"
		: throughValue ? "Cannot use static functions through values"
		: "Cannot use methods through types";
	
	private static Diagnostic ReportHiddenMember(SourceLocation location, string name,
		IEnumerable<FunctionSymbol> functions)
	{
		var widest = functions.MaxBy(static function => function.Visibility)!;
		return ReportHidden(location, name, widest.Visibility, widest.Trait, widest.Impl);
	}
	
	private static Diagnostic ReportHidden(SourceLocation location, string name, Visibility visibility,
		TraitSymbol? trait, ImplSymbol? impl) => (trait, impl) switch
	{
		(not null, _) when visibility == Visibility.Private => DiagnosticReporter.ReportHidden(location, name, "trait"),
		(_, not null) when visibility == Visibility.Private =>
			DiagnosticReporter.ReportHidden(location, name, "impl block"),
		_ => DiagnosticReporter.ReportHidden(location, name, visibility, true)
	};
	
	private Diagnostic ReportUndefinedSymbol(ISyntaxNode node, string name) =>
		CurrentResolutionContext.ReportHidden(name, node.SourceLocation) ??
		ReportHiddenStatic(name, node.SourceLocation) ??
		ReportMisplacedName(node.SourceLocation, name) ??
		DiagnosticReporter.ReportUndefinedSymbol(node, name, GetVisibleSymbolNames().Concat(_literalKeywords));
	
	private Diagnostic? ReportMisplacedName(SourceLocation location, string name)
	{
		var message = name switch
		{
			"never" => "'never' can only be a return type",
			"self" when CurrentResolutionContext.ContainingType is not null => "Cannot use 'self' in static functions",
			_ when CurrentResolutionContext.ContainingType is { } type &&
			       GetMethods(type, name).Any(static method => method.HasReceiver) =>
				"Cannot call methods without a receiver",
			_ when CurrentResolutionContext.ContainingFunction is not null &&
			       CurrentResolutionContext.GetAllSymbols().OfType<EnumSymbol>().Any(type => HasCase(type, name)) =>
				$"Cannot infer the type of '{name}'",
			_ => null
		};
		
		return message is null ? null : new(DiagnosticSeverity.Error, location, message);
	}
	
	private Diagnostic? ReportHiddenStatic(string name, SourceLocation location)
	{
		for (var type = CurrentResolutionContext.ContainingType; type is not null; type = type.ContainingType)
		{
			var hidden = FindStatics(type, name)
				.Where(method => !CanAccess(type, method.Function))
				.Select(static method => method.Function)
				.ToList();
			
			if (hidden.Count > 0)
				return ReportHiddenMember(location, name, hidden);
		}
		
		return null;
	}
	
	private DiagnosticText DescribeMissingMember(TypeSymbol type, string name) =>
		type.GetStaticField(name) is not null ? "Cannot use static fields through values"
		: FindFunctions(type, name).Length > 0 ? "Cannot use static functions through values"
		: DescribeAbsentMember(type, name);
	
	private DiagnosticText DescribeMissingStatic(TypeSymbol type, string name) =>
		FindFunctions(type, name).Length > 0 ? "Cannot use methods through types"
		: FindField(type, name) is not null ? "Cannot use fields through types"
		: DescribeAbsentMember(type, name);
	
	private DiagnosticText DescribeAbsentMember(TypeSymbol type, string name) =>
		_typePool.FindBlockViolation(type, name) ?? DescribeHiddenTraitMember(type, name) ??
		new DiagnosticText($"Type '{type.Name}' has no member '{name}'",
			DiagnosticReporter.FindSuggestion(name, GetMemberNames(type)));
	
	private DiagnosticText? DescribeHiddenTraitMember(TypeSymbol type, string name) => _typePool.FindConformances(type)
		.Select(static conformance => conformance.Trait)
		.Where(trait => !CurrentResolutionContext.IsVisible(trait) &&
		                (trait.GetFunctions(name).Any() || trait.GetProperty(name) is not null))
		.Select(trait =>
			$"'{name}' is private to its {DiagnosticReporter.DescribeScope(ModuleIndex.GetVisibility(trait), false)}")
		.FirstOrDefault();
	
	private IEnumerable<string> GetMemberNames(TypeSymbol type) =>
		(type is RecordSymbol record ? record.Members.Select(static member => member.Name) : [])
		.Concat(type.OriginalDefinition is NamedTypeSymbol named
			? named.Properties.Select(static property => property.Name)
			: [])
		.Concat(_typePool.GetMemberBlocks(type.OriginalDefinition)
			.SelectMany(static block => block.Functions.Select(static method => method.Name)
				.Concat(block.Properties.Select(static property => property.Name))))
		.Distinct();
	
	private MethodSymbol[] FindStatics(TypeSymbol type, string name) =>
		[..FindFunctions(type, name).Where(static function => !function.HasReceiver)];
	
	private MethodSymbol[] FindFunctions(TypeSymbol type, string name) =>
		[..GetMethods(type, name).Where(static method => !method.Function.IsConversion)];
	
	private FieldSymbol? FindField(TypeSymbol type, string name) =>
		_typePool.ResolveMember(GetMemberOwner(type, name), name) as FieldSymbol;
	
	private TypeSymbol GetMemberOwner(TypeSymbol type, string name)
	{
		if (type is PointerType { BaseType: var baseType } && baseType != NativeSymbols.Void)
			return baseType;
		
		if (type is DynType && (DeclaresMember(type, name) || GetDereferenceTarget(type) is not { } dynTarget ||
		                        !DeclaresMember(dynTarget, name)))
			return type;
		
		if (DeclaresAccessibleMember(type, name))
			return type;
		
		return GetDereferenceTarget(type) is { } target && (!DeclaresMember(type, name) || DeclaresMember(target, name))
			? target
			: type;
	}
	
	private bool DeclaresMember(TypeSymbol type, string name) =>
		_typePool.ResolveMember(type, name) is not null || DeclaresNonCaseMember(type, name);
	
	private bool DeclaresAccessibleMember(TypeSymbol type, string name) => _typePool.ResolveMember(type, name) switch
	{
		FieldSymbol field => CanAccess(type, field.Visibility),
		null when type.GetStaticField(name) is { } global => CanAccess(type, global.Visibility),
		null when GetPropertyMember(type, name) is { } property => CanAccess(type, property),
		null => FindFunctions(type, name).Any(function => CanAccess(type, function.Function)),
		_ => true
	};
}