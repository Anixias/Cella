using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private TypeSymbol? GetDereferenceTarget(TypeSymbol type) => FindDereferences(type)
		.Select(dereference => GetFunctionInfo(dereference, type).Signature.ReturnType)
		.Select(static result => result is BorrowType borrow ? borrow.Target : result)
		.FirstOrDefault();
	
	private IEnumerable<MethodSymbol> FindDereferences(TypeSymbol type) => FindPlaceOperators(type, "*");
	
	private IEnumerable<MethodSymbol> FindPlaceOperators(TypeSymbol type, string name) => GetMethods(type, name)
		.Where(method => method.HasReceiver && CanAccess(type, method.Function));
	
	private MethodSymbol? FindDereference(TypeSymbol type, ParameterMode mode) => FindPlaceOperator(type, "*", mode);
	
	private MethodSymbol? FindIndexer(TypeSymbol type, ParameterMode mode) => FindPlaceOperator(type, "[]", mode);
	
	private MethodSymbol? FindPlaceOperator(TypeSymbol type, string name, ParameterMode mode) =>
		FindPlaceOperators(type, name).FirstOrDefault(method => TakesReceiver(method, type, mode));
	
	private bool IsAmbiguousOperator(TypeSymbol type, MethodSymbol method) =>
		FindPlaceOperators(type, method.Name).Count(other => TakesReceiver(other, type, ReceiverMode(method, type))) >
		1;
	
	private ParameterMode ReceiverMode(MethodSymbol method, TypeSymbol type) =>
		GetFunctionInfo(method, type).Signature.GetMode(0);
	
	private static string DescribeAmbiguousOperator(string name, TypeSymbol type) =>
		$"Operation '{name}' on '{type.Name}' is ambiguous";
	
	private bool TakesReceiver(MethodSymbol method, TypeSymbol type, ParameterMode mode) =>
		GetFunctionInfo(method, type).Signature.GetMode(0) is var actual &&
		(actual == mode || mode.IsReadOnly() && actual.IsReadOnly());
	
	private ResolvedFunctionCallExpressionNode CallOperator(IResolvedExpressionNode receiver, MethodSymbol method,
		IExpressionNode syntax, PlaceOperator? place = null)
	{
		var info = GetFunctionInfo(method, receiver.Type);
		TrackFunctionUse(info, syntax);
		return new ResolvedFunctionCallExpressionNode(info, [CreateReceiver(receiver, info, place)], syntax)
		{
			PlaceOperator = place
		};
	}
	
	private IResolvedExpressionNode MakeWritable(IResolvedExpressionNode place)
	{
		switch (place)
		{
			case ResolvedAccessExpressionNode { Member: FieldSymbol } node:
			{
				var target = MakeWritable(node.Target);
				return target == node.Target
					? node
					: new ResolvedAccessExpressionNode(target, node.Member, node.Type, node.Syntax);
			}
			
			case ResolvedIndexerExpressionNode node:
			{
				var target = MakeWritable(node.Target);
				return target == node.Target
					? node
					: new ResolvedIndexerExpressionNode(node.Type, target, node.Index, node.Syntax);
			}
			
			case ResolvedUnaryOpExpressionNode
			{
				Operation.Op: TokenType.OpStar,
				Operand: ResolvedFunctionCallExpressionNode call
			} when FindReader(call) is { } reader:
				return WriteThrough(place, call, reader);
			
			case ResolvedFunctionCallExpressionNode { Type: not BorrowType } call when FindReader(call) is { } reader:
				return WriteThrough(place, call, reader);
			
			default:
				return place;
		}
	}
	
	private PlaceOperator? FindReader(ResolvedFunctionCallExpressionNode call) =>
		call is { PlaceOperator: { } op, Arguments: [var receiver, ..] } &&
		FindPlaceOperator(receiver.Type, op.GetName(), ParameterMode.ReadOnly)?.Function == call.Function.Symbol
			? op
			: null;
	
	private IResolvedExpressionNode WriteThrough(IResolvedExpressionNode place, ResolvedFunctionCallExpressionNode call,
		PlaceOperator op)
	{
		var name = op.GetName();
		var receiver = call.Arguments[0];
		if (FindPlaceOperator(receiver.Type, name, ParameterMode.Mut) is { } writer)
		{
			if (IsAmbiguousOperator(receiver.Type, writer))
				return Error(call.Syntax, DescribeAmbiguousOperator(name, receiver.Type), place.Type);
			
			var info = InstantiateLike(GetFunctionInfo(writer, receiver.Type), call.Function);
			TrackFunctionUse(info, call.Syntax);
			return Decay(new ResolvedFunctionCallExpressionNode(info,
				[CreateReceiver(receiver, info, op), ..call.Arguments.Skip(1)], call.Syntax) { PlaceOperator = op });
		}
		
		var hidden = GetMethods(receiver.Type, name)
			.Where(method => method.HasReceiver && TakesReceiver(method, receiver.Type, ParameterMode.Mut))
			.ToList();
		
		if (hidden.Count > 0)
			Diagnostics.Add(DiagnosticReporter.ReportReadOnly(call.Syntax.SourceLocation, name,
				hidden.Max(static method => method.Function.Visibility)));
		
		return place;
	}
	
	private IResolvedExpressionNode ResolveIndexing(IndexerExpressionNode node, IResolvedExpressionNode target)
	{
		target = Decay(target);
		if (!IsInvalid(target) && target.Type is UntypedType)
			target = MaterializeAsDefault(target);
		
		if (IsInvalid(target))
			return RejectIndexing(node, new ResolvedInvalidExpressionNode(node, CurrentTargetType));
		
		return target.Type switch
		{
			ArrayType { ElementType: var elementType } => ResolveBuiltInIndexing(node, target, elementType),
			StringType => ResolveBuiltInIndexing(node, target, NativeSymbols.UInt8),
			PointerType { BaseType: var baseType } pointer when baseType != NativeSymbols.Void &&
			                                                    baseType is not DynType =>
				ResolvePointerIndexing(node, target, pointer),
			var type when GetMethods(type, "[]").Where(static method => method.HasReceiver).ToList() is
				{ Count: > 0 } declared => ResolveDeclaredIndexing(node, target, declared),
			_ => RejectIndexing(node, Error(node, $"Cannot index into type '{target.Type.Name}'", CurrentTargetType,
				target.Syntax))
		};
	}
	
	private ResolvedInvalidExpressionNode RejectIndexing(IndexerExpressionNode node,
		ResolvedInvalidExpressionNode error)
	{
		foreach (var argument in node.Arguments)
		{
			if (argument is not TypeExpressionNode && CurrentResolutionContext.FindNamed(argument) is not TypeSymbol)
				VisitNode(argument);
		}
		
		return error;
	}
	
	private IResolvedExpressionNode ResolveBuiltInIndexing(IndexerExpressionNode node, IResolvedExpressionNode target,
		TypeSymbol elementType)
	{
		if (node.Arguments.Length != 1)
			return RejectIndexing(node, Error(node, DescribeIndexCount(target.Type, 1), elementType));
		
		var index = VisitNode(node.Arguments[0], NativeSymbols.UIntSize);
		return FindElement(target) is { } element
			? Substitute(element)
			: new ResolvedIndexerExpressionNode(elementType, target, index, node);
	}
	
	private IResolvedExpressionNode ResolvePointerIndexing(IndexerExpressionNode node, IResolvedExpressionNode target,
		PointerType pointer)
	{
		if (node.Arguments.Length != 1)
			return RejectIndexing(node, Error(node, DescribeIndexCount(pointer, 1), pointer.BaseType));
		
		var index = VisitNode(node.Arguments[0], null);
		var offsetType = index.Type is IntegerType { IsSigned: false } ? NativeSymbols.UIntSize : NativeSymbols.IntSize;
		index = CoerceToType(index, offsetType);
		if (IsInvalid(index))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		var offset = new PointerOffsetImpl(TokenType.OpPlus, pointer, offsetType);
		var address = new ResolvedBinaryOpExpressionNode(target, index, offset, node);
		return new ResolvedUnaryOpExpressionNode(address, new NativeImpl(TokenType.OpStar, pointer.BaseType), node);
	}
	
	private IResolvedExpressionNode ResolveDeclaredIndexing(IndexerExpressionNode node, IResolvedExpressionNode target,
		List<MethodSymbol> declared)
	{
		var type = target.Type;
		if ((FindIndexer(type, ParameterMode.ReadOnly) ?? FindIndexer(type, ParameterMode.Mut)) is not { } indexer)
			return RejectIndexing(node, Error(node, ReportHiddenMember(node.SourceLocation, "[]",
				declared.Select(static method => method.Function)), CurrentTargetType));
		
		if (IsAmbiguousOperator(type, indexer))
			return RejectIndexing(node, Error(node, DescribeAmbiguousOperator("[]", type), CurrentTargetType));
		
		var info = GetFunctionInfo(indexer, type);
		var parameterTypes = info.Signature.ParameterTypes;
		if (node.Arguments.Length != parameterTypes.Length - 1)
			return RejectIndexing(node, Error(node, DescribeIndexCount(type, parameterTypes.Length - 1),
				CurrentTargetType));
		
		if (!GetOpenTypeParameters(info).IsEmpty)
		{
			var checkpoint = OpenCheckpoint();
			var trial = node.Arguments.Select(argument => VisitNode(argument, null)).ToList();
			if (AnyInvalid([..trial]))
			{
				Commit(checkpoint);
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			}
			
			Rollback(checkpoint);
			var failures = new List<Diagnostic>();
			if (Specialize(new ReceiverCallable(info, info.Signature.ReturnType), "[]", trial, null,
				    node.SourceLocation, failures) is not ReceiverCallable specialized)
			{
				Diagnostics.AddRange(failures);
				return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
			}
			
			info = specialized.Info;
			parameterTypes = info.Signature.ParameterTypes;
		}
		
		var indices = node.Arguments.Select((argument, i) => VisitNode(argument, parameterTypes[i + 1])).ToList();
		if (AnyInvalid([..indices]))
			return new ResolvedInvalidExpressionNode(node, CurrentTargetType);
		
		TrackFunctionUse(info, node);
		return Decay(new ResolvedFunctionCallExpressionNode(info,
				[CreateReceiver(target, info, PlaceOperator.Indexer), ..indices], node)
			{ PlaceOperator = PlaceOperator.Indexer });
	}
	
	private static string DescribeIndexCount(TypeSymbol type, int count) => count == 1
		? $"Indexing '{type.Name}' takes one argument"
		: $"Indexing '{type.Name}' takes {count} arguments";
}