using System.Collections.Immutable;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private IResolvedExpressionNode VisitDynConversion(CallExpressionNode node, DynType dyn)
	{
		if (node.Arguments.Length != 1)
			return VisitConstructorCall(node, dyn, null);
		
		var argument = VisitArgument(node.Arguments[0]);
		if (IsInvalid(argument))
			return new ResolvedInvalidExpressionNode(node, dyn);
		
		if (argument is ResolvedMutArgumentExpressionNode mutArgument)
		{
			var place = Decay(mutArgument.Place);
			if (place.Type == dyn)
				return place;
			
			if (CanErase(place.Type, dyn))
				return Decay(Erase(new ResolvedBorrowExpressionNode(place, _typePool.GetBorrowType(place.Type, true),
					false, mutArgument.Syntax), _typePool.GetBorrowType(dyn, true)));
			
			return Error(node, $"'{place.Type.Name}' doesn't implement '{dyn.TraitName}'", dyn, node.Arguments[0]);
		}
		
		return ConvertToDyn(argument, dyn) ?? Error(node,
			$"'{Decay(argument).Type.Name}' doesn't implement '{dyn.TraitName}'", dyn, node.Arguments[0]);
	}
	
	private static bool IsDynTarget(TypeSymbol type) =>
		type is DynType or BorrowType { Target: DynType } or PointerType { BaseType: DynType };
	
	private bool CanErase(TypeSymbol type, DynType dyn) => type switch
	{
		DynType { Instance: { } from } => dyn.Instance is { } to &&
		                                  _typePool.FindDynPath(from, to) is { IsEmpty: false },
		DynType or InvalidType => false,
		_ => _typePool.Conforms(type, dyn)
	};
	
	private bool CanConvertToDyn(TypeSymbol source, TypeSymbol target) => target switch
	{
		DynType dyn => source == dyn || source switch
		{
			BorrowType { Target: var objectType } => objectType == dyn || CanErase(objectType, dyn),
			var type => CanErase(type, dyn)
		},
		BorrowType { Target: DynType dyn, IsMutable: var isMutable } => source switch
		{
			BorrowType { Target: var objectType } borrow => (borrow.IsMutable || !isMutable) &&
			                                                (objectType == dyn || CanErase(objectType, dyn)),
			var type => !isMutable && (type == dyn || CanErase(type, dyn))
		},
		PointerType { BaseType: DynType dyn } => source is PointerType { BaseType: var baseType } &&
		                                         CanErase(baseType, dyn),
		_ => false
	};
	
	private IResolvedExpressionNode? ConvertToDyn(IResolvedExpressionNode source, TypeSymbol target)
	{
		if (source.Type is UntypedType && CanConvertToDyn(GetDefaultType(source), target))
			source = MaterializeAsDefault(source);
		else if (source is ResolvedLiteralExpressionNode { Type: UntypedType } literal &&
		         (target is BorrowType { Target: DynType inner } ? inner : target) is DynType dyn &&
		         FindLiteralConformer(literal, dyn) is { } conformer)
			source = MaterializeExpression(literal, conformer);
		
		if (!CanConvertToDyn(source.Type, target))
			return null;
		
		var syntax = source.Syntax;
		switch (target)
		{
			case DynType dyn when source.Type == dyn:
				return source;
			
			case DynType dyn when source.Type is BorrowType { Target: var objectType, IsMutable: var isMutable }:
				return Decay(objectType == dyn ? source : Erase(source, _typePool.GetBorrowType(dyn, isMutable)));
			
			case DynType dyn:
				var borrow = _typePool.GetBorrowType(source.Type, false);
				var borrowed = new ResolvedBorrowExpressionNode(source, borrow, true, syntax);
				return Decay(Erase(borrowed, _typePool.GetBorrowType(dyn, false)));
			
			case BorrowType { Target: DynType dyn } borrowTarget when source.Type is BorrowType { Target: var t }:
				return t == dyn
					? new ResolvedConversionExpressionNode(source,
						_conversionTable.FindImplicit(source.Type, borrowTarget)!, syntax)
					: Erase(source, borrowTarget);
			
			case BorrowType { Target: DynType dyn } borrowTarget when source.Type == dyn:
				return new ResolvedBorrowExpressionNode(source, borrowTarget, true, syntax);
			
			case BorrowType borrowTarget:
				return Erase(new ResolvedBorrowExpressionNode(source, _typePool.GetBorrowType(source.Type, false), true,
					syntax), borrowTarget);
			
			default:
				return Erase(source, target);
		}
	}
	
	private IResolvedExpressionNode Erase(IResolvedExpressionNode pointer, TypeSymbol target)
	{
		var objectType = pointer.Type switch
		{
			BorrowType borrow => borrow.Target,
			PointerType p => p.BaseType,
			_ => throw new InvalidOperationException()
		};
		
		var dyn = target switch
		{
			BorrowType { Target: DynType d } => d,
			PointerType { BaseType: DynType d } => d,
			_ => throw new InvalidOperationException()
		};
		
		if (objectType is DynType)
			return new ResolvedConversionExpressionNode(pointer, new DynUpcastConversion(pointer.Type, target),
				pointer.Syntax);
		
		ImmutableArray<FunctionInfo> members = dyn.Trait is { } trait
			? [..GetDynMembers(trait).Select(member => GetFunctionInfo(member, objectType, dyn.TraitArguments))]
			: [];
		
		return new ResolvedConversionExpressionNode(pointer,
			new DynConversion(pointer.Type, target, objectType, members), pointer.Syntax);
	}
	
	private ImmutableArray<FunctionSymbol> GetDynMembers(TraitSymbol trait)
	{
		if (_typePool.FindDynMembers(trait) is { } existing)
			return existing;
		
		ImmutableArray<FunctionInfo> members =
		[
			..trait.Functions
				.Select(static method => method.Function)
				.Concat(trait.Properties.SelectMany(GetAccessors))
				.Where(IsDynCallable)
				.Select(GetFunctionInfo)
		];
		
		_typePool.SetDynMembers(trait, members);
		return _typePool.FindDynMembers(trait)!.Value;
	}
	
	private bool IsDynMember(FunctionSymbol function) =>
		function.Trait is { } trait && GetDynMembers(trait).Contains(function);
	
	private IEnumerable<TraitType> GetDynTraits(DynType dyn) =>
		dyn.Instance is { } trait ? _typePool.GetDynTraits(trait) : [];
	
	private bool IsDynCallable(FunctionSymbol function)
	{
		if (function.Visibility == Visibility.Private || function.Kind != FunctionKind.Method ||
		    !function.DeclaredTypeParameters.IsEmpty)
			return false;
		
		var signature = GetFunctionInfo(function).Signature;
		return signature.ParameterTypes.Length > 0 &&
		       (signature.GetMode(0) != ParameterMode.Value || _typePool.IsCopy(function.Trait!.Self)) && !signature
			       .ParameterTypes.Skip(1)
			       .Append(signature.ReturnType)
			       .Any(type => TypePool.FindTypeParameters(type).Contains(function.Trait!.Self));
	}
	
	private IResolvedExpressionNode VisitDynCall(CallExpressionNode node, AccessExpressionNode access,
		IResolvedExpressionNode target, DynType dyn, IndexerExpressionNode? indexer, TraitType? picked = null)
	{
		var name = access.Member.Text;
		var member = access.Member.SourceLocation;
		if (picked is null && GetPropertyMember(dyn, name) is not null)
			return VisitIndirectCall(node, Index(indexer, ResolveAccess(access, target)));
		
		IEnumerable<TraitType> traits = picked is null ? GetDynTraits(dyn) : [picked];
		var functions = traits.SelectMany(trait => GetInstanceMethods(trait, name)).ToArray();
		if (functions.Length == 0)
			return Error(node, $"Type '{dyn.Name}' has no member '{name}'", CurrentTargetType, member);
		
		var accessible = functions.Where(method => CanAccess(dyn, method.Function)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(member, name, functions.Select(static method => method.Function)),
				CurrentTargetType);
		
		var callable = accessible.Where(method => IsDynMember(method.Function)).ToArray();
		if (callable.Length == 0)
			return Error(node, accessible.Any(static method => method.HasReceiver)
				? $"Cannot call '{name}' through '{dyn.Name}'"
				: "Cannot use static functions through values", CurrentTargetType, member);
		
		ICallable[] candidates =
		[
			..callable
				.Select(method => GetFunctionInfo(method, dyn))
				.Select(static info => new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (indexer is null)
			return ResolveCall(node, name, candidates, target);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, name, candidates, target, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private IResolvedExpressionNode VisitTraitCall(CallExpressionNode node, AccessExpressionNode access,
		TraitType? trait, IExpressionNode argument, IndexerExpressionNode? indexer)
	{
		if (trait is null)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (argument is BorrowExpressionNode borrow)
			return RejectPickBorrow(node, borrow);
		
		var name = access.Member.Text;
		var member = access.Member.SourceLocation;
		IResolvedExpressionNode? receiver = null;
		TypeSymbol self;
		if (CurrentResolutionContext.TryResolveExpressionAsType(argument) is { } type)
			self = RequireTypeArguments(type, argument);
		else
		{
			receiver = Decay(VisitNode(argument, null));
			self = receiver.Type;
		}
		
		if (self is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (receiver is not null && self is DynType dyn && GetDynTraits(dyn).Any(reached =>
			    reached.Trait == trait.Trait && reached.Arguments.SequenceEqual(trait.Arguments)))
			return VisitDynCall(node, access, receiver, dyn, indexer, trait);
		
		if (!_typePool.Conforms(self, trait))
			return Error(node, $"'{self.Name}' doesn't implement '{trait.Name}'", CurrentTargetType, argument);
		
		if (trait.Trait.GetProperty(name) is not null)
			return VisitIndirectCall(node, Index(indexer, VisitTraitMember(access, trait, argument)));
		
		var functions = GetInstanceMethods(trait, name).ToArray();
		var matching = functions.Where(method => method.HasReceiver == receiver is not null).ToArray();
		if (matching.Length == 0)
			return Error(node, DescribeMissingTraitMember(trait, name, functions, receiver is not null),
				CurrentTargetType, member);
		
		var accessible = matching.Where(method => CanAccess(self, method.Function)).ToArray();
		if (accessible.Length == 0)
			return Error(node, ReportHiddenMember(member, name,
				matching.Select(static method => method.Function)), CurrentTargetType);
		
		ICallable[] candidates =
		[
			..accessible
				.Select(method => GetFunctionInfo(method, self))
				.Select(info => receiver is null
					? (ICallable)new FunctionCallable(info)
					: new ReceiverCallable(info, info.Signature.ReturnType))
		];
		
		if (indexer is null)
			return ResolveCall(node, name, candidates, receiver);
		
		return ResolveTypeArguments(indexer, candidates) is { } typeArguments
			? ResolveCall(node, name, candidates, receiver, typeArguments)
			: new ResolvedInvalidExpressionNode(node, CurrentTargetType);
	}
	
	private ResolvedInvalidExpressionNode RejectPickBorrow(IExpressionNode node, BorrowExpressionNode borrow)
	{
		VisitNode(borrow.Value, null);
		return Error(node, $"Cannot use '{borrow.Keyword.Text}' in trait picks", CurrentTargetType,
			borrow.Keyword.SourceLocation);
	}
	
	private IResolvedExpressionNode VisitTraitMember(AccessExpressionNode node, TraitType? trait,
		IExpressionNode argument)
	{
		if (trait is null)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (argument is BorrowExpressionNode borrow)
			return RejectPickBorrow(node, borrow);
		
		var name = node.Member.Text;
		var member = node.Member.SourceLocation;
		IResolvedExpressionNode? receiver = null;
		TypeSymbol self;
		if (CurrentResolutionContext.TryResolveExpressionAsType(argument) is { } type)
			self = RequireTypeArguments(type, argument);
		else
		{
			receiver = Decay(VisitNode(argument, null));
			self = receiver.Type;
		}
		
		if (self is InvalidType)
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		if (!_typePool.Conforms(self, trait))
			return Error(node, $"'{self.Name}' doesn't implement '{trait.Name}'", CurrentTargetType, argument);
		
		if (GetInstanceProperty(trait, name) is { } property)
			return ResolvePropertyUse(node, member, property, self, receiver);
		
		var functions = GetInstanceMethods(trait, name).ToArray();
		if (receiver is not null)
			return Error(node, functions.Any(static method => method.HasReceiver)
				? "Cannot use methods as values"
				: DescribeMissingTraitMember(trait, name, functions, true), CurrentTargetType, member);
		
		var statics = functions.Where(static method => !method.HasReceiver).ToArray();
		if (statics.Length == 0)
			return Error(node, DescribeMissingTraitMember(trait, name, functions, false), CurrentTargetType, member);
		
		var accessible = FindAccessible(self, statics);
		return accessible.Length > 0
			? ResolveFunctionValue(node, accessible, self)
			: Error(node, ReportHiddenMember(member, name, statics.Select(static method => method.Function)),
				CurrentTargetType);
	}
}