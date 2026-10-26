using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding.Constants;

public sealed partial class ConstantEvaluator
{
	private enum FlowKind
	{
		Normal,
		Break,
		Continue,
		Return
	}
	
	private readonly record struct Flow(FlowKind Kind, LabelSymbol? Label = null, Constant? Value = null);
	
	private sealed class JumpSignal(Flow flow) : Exception
	{
		public Flow Flow { get; } = flow;
	}
	
	private Flow Execute(IResolvedStatementNode statement)
	{
		Step();
		try
		{
			return ExecuteStatement(statement);
		}
		catch (JumpSignal jump)
		{
			return jump.Flow;
		}
	}
	
	private Flow ExecuteStatement(IResolvedStatementNode statement)
	{
		switch (statement)
		{
			case ResolvedBlockStatementNode block:
				foreach (var inner in block.Statements)
				{
					if (Execute(inner) is { Kind: not FlowKind.Normal } flow)
						return flow;
				}
				
				return default;
			
			case ResolvedExpressionStatementNode { Expression: var expression }:
				return ExecuteExpression(expression);
			
			case ResolvedVarStatementNode declaration:
				Declare(declaration.Symbol, declaration.Initializer is { } initializer
					? Value(initializer)
					: declaration.Symbol.IsDeferred
						? null
						: Zero(SubstituteType(declaration.Symbol.Type)));
				
				return default;
			
			case ResolvedIfStatementNode branch:
				if (Condition(branch.Condition))
					return Execute(branch.Then);
				
				return branch.Else is { } otherwise ? Execute(otherwise) : default;
			
			case ResolvedWhileStatementNode loop:
				while (true)
				{
					Step();
					if (!Condition(loop.Condition))
						return default;
					
					if (Exits(Execute(loop.Body), loop.Label, out var exit))
						return exit;
				}
			
			case ResolvedDoWhileStatementNode loop:
				while (true)
				{
					Step();
					if (Exits(Execute(loop.Body), loop.Label, out var exit))
						return exit;
					
					if (!Condition(loop.Condition))
						return default;
				}
			
			case ResolvedLoopStatementNode loop:
				while (true)
				{
					Step();
					if (Exits(Execute(loop.Body), loop.Label, out var exit))
						return exit;
				}
			
			case ResolvedRangeForStatementNode loop:
				return ExecuteRange(loop);
			
			case ResolvedCursorForStatementNode loop:
				return ExecuteCursor(loop);
			
			case ResolvedMatchStatementNode match:
				return SelectArm(match.Value, match.IsMut, match.Arms.Length, i => match.Arms[i].Patterns,
					i => match.Arms[i].Guard) is { } arm
					? Execute(match.Arms[arm].Body)
					: default;
			
			case ResolvedInvalidStatementNode:
				throw new AbortedEvaluation();
			
			default:
				throw new EvaluationFault(FaultKind.Unsupported, "", statement.Syntax.SourceLocation)
				{
					Function = _frames.Peek().Function.Symbol.Name
				};
		}
	}
	
	private Flow ExecuteExpression(IResolvedExpressionNode expression)
	{
		switch (expression)
		{
			case ResolvedReturnExpressionNode { HasUnknownTarget: true }:
				throw new AbortedEvaluation();
			
			case ResolvedReturnExpressionNode returned:
				return new Flow(FlowKind.Return, null, returned.Value is { } value ? Value(value) : null);
			
			case ResolvedBreakExpressionNode jump:
				return new Flow(FlowKind.Break, jump.Label);
			
			case ResolvedContinueExpressionNode jump:
				return new Flow(FlowKind.Continue, jump.Label);
			
			case ResolvedErasedCallExpressionNode:
				return default;
			
			default:
				Value(expression);
				return default;
		}
	}
	
	private static bool Exits(Flow flow, LabelSymbol? label, out Flow exit)
	{
		exit = default;
		switch (flow.Kind)
		{
			case FlowKind.Normal:
				return false;
			
			case FlowKind.Continue when flow.Label is null || flow.Label == label:
				return false;
			
			case FlowKind.Break when flow.Label is null || flow.Label == label:
				return true;
			
			default:
				exit = flow;
				return true;
		}
	}
	
	private Flow ExecuteRange(ResolvedRangeForStatementNode loop)
	{
		if (loop.Source is { Symbol: var source, Value: var sourceValue })
			Declare(source, source.Type is PointerType && sourceValue is not ResolvedMutArgumentExpressionNode
				? Reference(sourceValue)
				: Value(sourceValue));
		
		var counter = Declare(loop.Counter, Value(loop.Start));
		var index = loop.Index is { } indexSymbol ? Declare(indexSymbol, Zero(indexSymbol.Type)) : null;
		var end = Value(loop.End);
		var test = loop.IsInclusive ? BinaryOperation.LessEqual : BinaryOperation.Less;
		var isFirst = true;
		while (true)
		{
			Step();
			if (Apply(test, counter.Value!, end, NativeSymbols.Bool) is not BoolConstant { Value: true })
				return default;
			
			if (!isFirst && index is not null)
				index.Value = Increment(index.Value!);
			
			isFirst = false;
			if (Exits(Iterate(loop.Element, loop.Body), loop.Label, out var exit))
				return exit;
			
			if (loop.IsInclusive && Apply(BinaryOperation.Equal, counter.Value!, end, NativeSymbols.Bool) is
				    BoolConstant { Value: true })
				return default;
			
			counter.Value = Increment(counter.Value!);
		}
	}
	
	private Flow ExecuteCursor(ResolvedCursorForStatementNode loop)
	{
		Declare(loop.Cursor.Symbol, Value(loop.Cursor.Value));
		var index = loop.Index is { } indexSymbol ? Declare(indexSymbol, Zero(indexSymbol.Type)) : null;
		var isFirst = true;
		while (true)
		{
			Step();
			if (!Condition(loop.Step))
				return default;
			
			if (!isFirst && index is not null)
				index.Value = Increment(index.Value!);
			
			isFirst = false;
			if (Exits(Iterate(loop.Element, loop.Body), loop.Label, out var exit))
				return exit;
		}
	}
	
	private Flow Iterate(ResolvedLoopVariable? element, IResolvedStatementNode body)
	{
		if (element is { Symbol: var symbol, Value: var place })
			Declare(symbol, symbol.IsBorrowBinding ? Reference(place) : Value(place));
		
		return Execute(body);
	}
	
	private Constant Increment(Constant value) => value switch
	{
		IntegerConstant { Type: CharType } character => new IntegerConstant(character.Type, character.Value + 1),
		IntegerConstant integer => Integer(integer.Type, integer.Value + 1),
		_ => throw new AbortedEvaluation()
	};
	
	private int? SelectArm(IResolvedExpressionNode subject, bool isMut, int count,
		Func<int, ImmutableArray<ResolvedPattern>> patterns, Func<int, IResolvedExpressionNode?> guard)
	{
		var place = isMut ? EvalPlace(subject) : null;
		var value = place is null ? Value(subject) : Read(place);
		for (var i = 0; i < count; i++)
		{
			ResolvedPattern? matched = null;
			foreach (var pattern in patterns(i))
			{
				if (MatchPattern(pattern, value) ?? throw Fault(subject))
				{
					matched = pattern;
					break;
				}
			}
			
			if (matched is null && !patterns(i).IsEmpty)
				continue;
			
			if (matched is not null)
				Bind(matched, value, place);
			
			if (guard(i) is { } condition && !Condition(condition))
				continue;
			
			return i;
		}
		
		return null;
	}
	
	private void Bind(ResolvedPattern pattern, Constant value, Place? place)
	{
		if (place is null || !pattern.HasMutBindings)
		{
			Bind(pattern, value);
			return;
		}
		
		for (var i = 0; i < pattern.Bindings.Length; i++)
		{
			PlaceStep? step = pattern.Case is { } enumCase ? new PayloadStep(enumCase, i)
				: pattern is { Record: not null, TestedType: null } ? new FieldStep(pattern.Fields[i])
				: null;
			
			if (step is null)
				continue;
			
			var slot = place.Then(step);
			if (pattern.Elements.Length > i && pattern.Elements[i] is { } element)
				Bind(element, Read(slot), slot);
			
			if (pattern.Bindings[i] is not { } binding)
				continue;
			
			if (binding.IsMutBinding)
				Declare(binding, new ReferenceConstant(binding.Type, slot));
			else
				_bindings[binding] = Read(slot);
		}
	}
	
	private Constant FoldMatchInFrame(ResolvedMatchExpressionNode node) =>
		SelectArm(node.Value, node.IsMut, node.Arms.Length, i => node.Arms[i].Patterns, i => node.Arms[i].Guard) is
			{ } arm
			? Value(node.Arms[arm].Value)
			: throw Fault(node);
	
	private BoolConstant FoldMutIs(ResolvedIsExpressionNode node)
	{
		var place = EvalPlace(node.Value);
		var value = Read(place);
		var matches = MatchPattern(node.Pattern, value) ?? throw Fault(node);
		if (matches)
			Bind(node.Pattern, value, place);
		
		return BoolConstant.From(matches);
	}
	
	private BoolConstant FoldShortCircuit(ResolvedBinaryOpExpressionNode node)
	{
		var isAnd = ((NativeImpl)node.Operation!).Op == TokenType.OpAmpersandAmpersand;
		if (Condition(node.Left) != isAnd)
			return BoolConstant.From(!isAnd);
		
		return BoolConstant.From(Condition(node.Right));
	}
	
	private static bool IsShortCircuit(ResolvedBinaryOpExpressionNode node) =>
		node.Operation is NativeImpl { Op: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar };
	
	private Constant? FoldDeclaredChain(ResolvedChainedExpressionNode node)
	{
		if (Evaluate(node.Operands[0]) is not { } left)
			return null;
		
		for (var i = 0; i < node.Links.Length; i++)
		{
			var link = node.Links[i];
			if (Evaluate(node.Operands[i + 1]) is not { } right)
				return null;
			
			if (left is InvalidConstant || right is InvalidConstant)
				return InvalidConstant.Instance;
			
			var result = link.Function is { } function
				? Invoke(SubstituteFunction(function), [Dereference(left), Dereference(right)],
					node.Syntax.SourceLocation)
				: link.Operation is { } operation &&
				  ConvertOperand(Dereference(left), link.LeftConversion) is { } leftValue &&
				  ConvertOperand(Dereference(right), link.RightConversion) is { } rightValue
					? Apply(operation, leftValue, rightValue, node.Type)
					: null;
			
			switch (result)
			{
				case BoolConstant { Value: false }:
					return BoolConstant.False;
				
				case InvalidConstant:
					return result;
				
				case not BoolConstant:
					return null;
			}
			
			left = right;
		}
		
		return BoolConstant.True;
	}
	
	private Constant FoldQuestion(ResolvedQuestionExpressionNode node)
	{
		if (node.Body is not { } body)
			throw Fault(node);
		
		Declare(node.Self, Value(node.Operand));
		return Value(body);
	}
	
	private Constant Assign(ResolvedAssignmentExpressionNode node)
	{
		var place = EvalPlace(node.Left);
		var value = Value(node.Right);
		if (node.Operation is { } operation)
			value = Apply(operation, Read(place), Dereference(value), SubstituteType(node.Left.Type)) ??
			        throw Fault(node);
		
		Write(place, value);
		return value;
	}
	
	private Constant AssignProperty(ResolvedPropertyAssignmentExpressionNode node)
	{
		var location = node.Syntax.SourceLocation;
		var receiver = node.Receiver is null ? null : EvalPlace(node.Receiver);
		var current = CallAccessor(SubstituteFunction(node.Getter), receiver, [], location);
		Constant value;
		if (node.Compound is { } compound)
		{
			var function = SubstituteFunction(compound);
			var target = Temporary(current);
			var right = TypePool.PassesByPointer(function.DeclaredSignature.GetMode(1))
				? Reference(node.Right) ?? throw Fault(node.Right)
				: Value(node.Right);
			
			Expect(Invoke(function, [new ReferenceConstant(current.Type, target), right], location), node);
			value = Read(target);
		}
		else
			value = Apply(node.Operation!, current, Dereference(Value(node.Right)), current.Type) ?? throw Fault(node);
		
		CallAccessor(SubstituteFunction(node.Setter), receiver, [value], location);
		return _void;
	}
	
	private Constant CallAccessor(FunctionInfo accessor, Place? receiver, Constant[] values, SourceLocation location)
	{
		Constant[] arguments = receiver is null
			? values
			:
			[
				accessor.DeclaredSignature.GetMode(0) is var mode &&
				(TypePool.PassesByPointer(mode) || mode == ParameterMode.Mut)
					? new ReferenceConstant(accessor.DeclaredSignature.ParameterTypes[0], receiver)
					: Read(receiver),
				..values
			];
		
		return Expect(Invoke(accessor, arguments, location), null);
	}
	
	private Constant Expect(Constant? result, IResolvedExpressionNode? node) => result switch
	{
		InvalidConstant => throw new AbortedEvaluation(),
		{ } value => value,
		null when node is not null => throw Fault(node),
		null => throw new AbortedEvaluation()
	};
	
	private bool Condition(IResolvedExpressionNode condition) => Value(condition) switch
	{
		BoolConstant flag => flag.Value,
		_ => throw Fault(condition)
	};
	
	private Constant Value(IResolvedExpressionNode node) => Evaluate(node) switch
	{
		InvalidConstant => throw new AbortedEvaluation(),
		{ } value => value,
		null => throw Fault(node)
	};
	
	private Cell Declare(VariableSymbol symbol, Constant? value)
	{
		var cell = new Cell(value);
		_frames.Peek().Locals[symbol] = cell;
		return cell;
	}
	
	private Cell? FindCell(VariableSymbol symbol) =>
		_frames.TryPeek(out var frame) && frame.Locals.TryGetValue(symbol, out var cell) ? cell : null;
	
	private Constant ReadVariable(Cell cell, TypeSymbol type) => cell.Value switch
	{
		null => throw new AbortedEvaluation(),
		ReferenceConstant reference when type is not (BorrowType or PointerType) => Read(reference.Place),
		var value => value
	};
	
	private Constant? Reference(IResolvedExpressionNode node)
	{
		if (IsInterpreting)
			return new ReferenceConstant(node.Type, EvalPlace(node));
		
		return Evaluate(node) is { } value and not InvalidConstant
			? new ReferenceConstant(node.Type, Temporary(value))
			: Evaluate(node);
	}
	
	private static Place Temporary(Constant value) => new(new Cell(value), []);
	
	private Constant Dereference(Constant value) =>
		value is ReferenceConstant reference ? Read(reference.Place) : value;
	
	private Place EvalPlace(IResolvedExpressionNode node)
	{
		switch (node)
		{
			case ResolvedVarExpressionNode variable when FindCell(variable.Symbol) is { } cell:
				return cell.Value is ReferenceConstant reference && variable.Type is not (BorrowType or PointerType)
					? reference.Place
					: new Place(cell, []);
			
			case ResolvedVarExpressionNode { Symbol: LocalVariableSymbol local } when
				_bindings.Remove(local, out var bound):
				return new Place(Declare(local, bound), []);
			
			case ResolvedAccessExpressionNode { Member: FieldSymbol field } access:
				return Through(EvalPlace(access.Target), access.Target.Type).Then(new FieldStep(field));
			
			case ResolvedIndexerExpressionNode indexer when indexer.Target.Type is not StringType:
				return IndexPlace(indexer);
			
			case ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } dereference:
				return Value(dereference.Operand) is ReferenceConstant target ? target.Place : throw Fault(node);
			
			case ResolvedOwnExpressionNode owned:
				return EvalPlace(owned.Value);
			
			case ResolvedFunctionCallExpressionNode { PlaceOperator: not null } call:
				return Value(call) is ReferenceConstant result ? result.Place : Temporary(Value(call));
			
			default:
				return Temporary(Value(node));
		}
	}
	
	private Place Through(Place place, TypeSymbol type) =>
		type is BorrowType or PointerType && Read(place) is ReferenceConstant reference ? reference.Place : place;
	
	private Place IndexPlace(ResolvedIndexerExpressionNode node)
	{
		var target = Through(EvalPlace(node.Target), node.Target.Type);
		if (Value(node.Index) is not IntegerConstant { Value: var position } ||
		    SubstituteType(ArrayOf(node.Target.Type)) is not ArrayType { Length: var length } ||
		    position.Sign < 0 || position >= length)
			throw Fault(node);
		
		return target.Then(new ElementStep((int)position));
	}
	
	private static TypeSymbol ArrayOf(TypeSymbol type) => type switch
	{
		PointerType { BaseType: var target } => target,
		BorrowType { Target: var target } => target,
		_ => type
	};
	
	private Constant Read(Place place)
	{
		var value = place.Root.Value ?? throw new AbortedEvaluation();
		foreach (var step in place.Path)
		{
			value = step switch
			{
				FieldStep { Field: var field } => GetField(value, field),
				ElementStep { Position: var index } => Index(value, new IntegerConstant(NativeSymbols.UIntSize, index)),
				PayloadStep { Case: var enumCase, Position: var index } => GetPayload(value, enumCase, index),
				_ => null
			} ?? throw new AbortedEvaluation();
		}
		
		return value;
	}
	
	private void Write(Place place, Constant value) =>
		place.Root.Value = Replace(place.Root.Value, place.Path, 0, value);
	
	private Constant Replace(Constant? current, ImmutableArray<PlaceStep> path, int depth, Constant value)
	{
		if (depth == path.Length)
			return value;
		
		if (current is null)
			throw new AbortedEvaluation();
		
		switch (path[depth])
		{
			case FieldStep { Field: var field } when current.Type is RecordSymbol { IsUnion: true }:
				var member = typePool.GetTypeOfMember(field);
				var existing = current is UnionConstant union ? ReadUnion(union, member) ?? Zero(member) : Zero(member);
				return WriteUnion(current, Replace(existing, path, depth + 1, value));
			
			case FieldStep { Field: var field }:
				var record = current switch
				{
					RecordConstant constant => constant,
					ZeroConstant => new RecordConstant(current.Type,
						typePool.GetMembers(current.Type).OfType<TypedMemberSymbol>()
							.Select(m => Zero(typePool.GetTypeOfMember(m)))),
					_ => throw new AbortedEvaluation()
				};
				
				var fields = record.Fields.ToArray();
				var fieldIndex = typePool.GetFieldIndex(record.Type, field);
				fields[fieldIndex] = Replace(fields[fieldIndex], path, depth + 1, value);
				return new RecordConstant(record.Type, fields);
			
			case ElementStep { Position: var index }:
				var array = current switch
				{
					ArrayConstant constant => constant,
					ZeroConstant { Type: ArrayType type } => new ArrayConstant(type,
						Enumerable.Repeat(Zero(type.ElementType), (int)type.Length)),
					_ => throw new AbortedEvaluation()
				};
				
				var elements = array.Elements.ToArray();
				elements[index] = Replace(elements[index], path, depth + 1, value);
				return new ArrayConstant(array.ArrayType, elements);
			
			case PayloadStep { Case: var enumCase, Position: var index }:
				var payload = current is EnumConstant { Case: var stored } enumValue && stored == enumCase
					? enumValue.Payload.ToArray()
					: typePool.GetPayloadTypes((EnumSymbol)current.Type, enumCase).Select(Zero).ToArray();
				
				payload[index] = Replace(payload[index], path, depth + 1, value);
				return new EnumConstant(current.Type, enumCase, payload);
			
			default:
				throw new AbortedEvaluation();
		}
	}
	
	private UnionConstant WriteUnion(Constant current, Constant member)
	{
		var size = CountBytes(member.Type) ?? throw new AbortedEvaluation();
		var previous = current is UnionConstant union ? union.Bytes : ImmutableArray<byte>.Empty;
		var bytes = new byte[Math.Max(size, previous.Length)];
		previous.CopyTo(bytes);
		if (!Write(member, bytes.AsSpan(0, size)))
			throw new AbortedEvaluation();
		
		return new UnionConstant(current.Type, bytes);
	}
}