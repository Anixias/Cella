using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed partial class Lowerer
{
	private sealed partial class FunctionLowerer
	{
		public void Visit(ResolvedWhileStatementNode node)
		{
			var id = NextLoopId();
			var condBlock = CreateBlock($"while{id}_cond");
			var bodyBlock = CreateBlock($"while{id}_body");
			var exitBlock = CreateBlock($"while{id}_exit");
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			// Condition
			currentBlock = condBlock;
			var scopeDepth = _activeScopes.Count;
			var scope = BeginScope(node.Condition.Syntax.SourceLocation, true);
			var doneBlock = CreateBlock($"while{id}_done");
			var endsEarly = !BindsNames(node.Condition);
			var enterBlock = endsEarly ? CreateBlock($"while{id}_enter") : bodyBlock;
			LowerBranch(node.Condition, enterBlock, doneBlock);
			ExitScope(doneBlock, scope, exitBlock);
			if (endsEarly)
			{
				_activeScopes.Pop();
				ExitScope(enterBlock, scope, bodyBlock);
			}
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label, scopeDepth, scopeDepth);
			if (!endsEarly)
				EndCurrentScope();
			
			currentBlock?.FillTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			ContinueWith(exitBlock);
		}
		
		public Value Visit(ResolvedConstructorCallExpressionNode node)
		{
			var sourceLocation = node.Syntax.SourceLocation;
			var arguments = LowerArguments(node.Arguments, node.Function, 1);
			
			var resultSymbol = CreateTempSymbol(node.Type, ".cons__mem");
			Declare(GetOrMakeBlock(),
				new LocalVarInstruction(resultSymbol, new ZeroValue(node.Type), sourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, node.Type), sourceLocation);
			
			var selfType = node.Function.Signature.ParameterTypes[0];
			var self = new UnaryOpValue(selfType, result, UnaryOperation.AddressOf, sourceLocation);
			
			GetOrMakeBlock().Instructions
				.Add(new ExpressionInstruction(new CallValue(node.Function, [self, ..arguments], sourceLocation)));
			
			return result;
		}
		
		public Value Visit(ResolvedRecordExpressionNode node)
		{
			if (node.Fields.IsEmpty)
				return new ZeroValue(node.Type);
			
			var sourceLocation = node.Syntax.SourceLocation;
			var values = LowerOperands([..node.Fields.Select(static f => f.Value)], static _ => Passing.Consume);
			var resultSymbol = CreateTempSymbol(node.Type, ".record");
			Declare(GetOrMakeBlock(),
				new LocalVarInstruction(resultSymbol, new ZeroValue(node.Type), sourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, node.Type), sourceLocation);
			foreach (var ((field, _), fieldValue) in node.Fields.Zip(values))
			{
				var target = new AccessValue(fieldValue.Type, result, field, sourceLocation);
				GetOrMakeBlock().Instructions.Add(
					new ExpressionInstruction(new AssignValue(fieldValue.Type, target, fieldValue, sourceLocation)));
			}
			
			return result;
		}
		
		public Value Visit(ResolvedEnumCaseExpressionNode node) => new EnumValue((EnumSymbol)node.Type, node.Case,
			LowerOperands(node.Payload, static _ => Passing.Consume), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedIsExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var pattern = node.Pattern;
			var scrutinee = pattern.TestedType is not null ? LowerDynAddress(node.Value) : LowerScrutinee(node.Value);
			if (IsSimpleTest(pattern) && pattern.Test is null)
				return pattern.TestedType is not null
					? TestDyn(scrutinee, pattern)
					: TestCase(scrutinee, pattern.Case!);
			
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "is_result");
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(resultSymbol, ConstantValue.False, location,
				CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), location);
			var bindBlock = CreateBlock("is_bind");
			var mergeBlock = CreateBlock("is_merge");
			LowerPatternTest(scrutinee, pattern, bindBlock, mergeBlock);
			currentBlock = bindBlock;
			BindPattern(scrutinee, pattern, node.OwnsValue);
			if (node.OwnsValue)
				ReleaseScrutinee(scrutinee, pattern);
			
			GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result,
				ConstantValue.True, location)));
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(mergeBlock, location));
			ContinueWith(mergeBlock);
			return result;
		}
		
		public Value Visit(ResolvedConversionExpressionNode node) => new ConversionValue(
			node.Conversion is CallableConversion ? BorrowCallable(node.Source) : VisitNode(node.Source),
			node.Conversion, node.Syntax.SourceLocation);
		
		private UnaryOpValue BorrowCallable(IResolvedExpressionNode callable)
		{
			var value = VisitPlace(callable);
			var place = IsPlaceValue(value) ? value : StoreTemporary(value, "callable");
			return new(_typePool.GetBorrowType(callable.Type, false), place, UnaryOperation.AddressOf,
				callable.Syntax.SourceLocation);
		}
		
		public Value Visit(ResolvedFunctionCallExpressionNode node) => Diverge(new CallValue(node.Function,
			node.IsConversion
				? LowerOperands(node.Arguments, GetArgumentPassing(node.Function, 0), _returnedValues.Contains(node))
				: LowerArguments(node.Arguments, node.Function, 0), node.Syntax.SourceLocation));
		
		private Value Diverge(Value call)
		{
			if (call.Type is not NeverType)
				return call;
			
			var block = GetOrMakeBlock();
			block.Instructions.Add(new ExpressionInstruction(call));
			block.SetTerminator(new UnreachableTerminator(call.SourceLocation));
			currentBlock = null;
			return new UndefValue(call.Type);
		}
		
		public Value Visit(ResolvedFunctionGroupExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedCaseNameExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedFunctionReferenceExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedClosureExpressionNode node) => node.Type is ClosureType closure
			? new OwnClosureValue(node.Function,
				node.Captures.Select((capture, i) => CaptureValue(capture, node.Function.Symbol.Captures[i])), closure,
				node.Syntax.SourceLocation)
			: new ClosureValue(node.Function, node.Captures.Select(CaptureAddress), node.Type,
				node.Syntax.SourceLocation)
			{
				Environment = DeclareIterationEnvironment(node.Syntax.SourceLocation)
			};
		
		private LocalVariableSymbol? DeclareIterationEnvironment(SourceLocation location)
		{
			if (!_loopStack.TryPeek(out var loop) || _activeScopes.Count <= loop.ContinueDepth)
				return null;
			
			var scope = _activeScopes.Reverse().ElementAt(loop.ContinueDepth);
			var symbol = new LocalVariableSymbol(new(TokenType.Identifier, location, ".closure"),
				NativeSymbols.Bool, false)
			{
				IsClosureEnvironment = true
			};
			
			Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, new UndefValue(NativeSymbols.Bool), location,
				scope.Id));
			
			return symbol;
		}
		
		private Value CaptureValue(IResolvedExpressionNode capture, LocalVariableSymbol binding)
		{
			var value = VisitPlace(capture);
			return binding.IsMutBinding || !_typePool.IsCopy(capture.Type) ? new MoveValue(value) : value;
		}
		
		private UnaryOpValue CaptureAddress(IResolvedExpressionNode capture) =>
			new(_typePool.GetPointerType(capture.Type), VisitPlace(capture), UnaryOperation.AddressOf,
				capture.Syntax.SourceLocation);
		
		public Value Visit(ResolvedIndirectCallExpressionNode node)
		{
			var operands = LowerOperands([node.Target, ..node.Arguments], GetOperandPassing(node.FunctionType));
			return Diverge(new IndirectCallValue(operands[0], operands.Skip(1), node.FunctionType,
				node.Syntax.SourceLocation));
		}
		
		private Func<int, Passing> GetOperandPassing(FunctionType type) => i => i == 0 || i > type.ParameterTypes.Length
			? Passing.Read
			: GetPassing(type.ParameterModes[i - 1]);
		
		public Value Visit(ResolvedIndexerExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var target = MaterializeTemporary(VisitNode(node.Target));
			if (node.Target.Type is not ArrayType)
			{
				if (MayEmit(node.Index))
					target = CaptureAsAtomic(target, "target");
				
				return new IndexerValue(node.Type, target, VisitNode(node.Index), location);
			}
			
			var defers = HasAccessWork(target);
			if (defers || MayEmit(node.Index))
				target = StabilizeOperandsBase(target);
			
			var index = VisitNode(node.Index);
			return new IndexerValue(node.Type, target, defers ? CaptureAsAtomic(index, "index") : index, location);
		}
		
		public Value Visit(ResolvedInvalidExpressionNode node) =>
			throw new InvalidOperationException();
		
		public Value Visit(ResolvedAccessExpressionNode node) => new AccessValue(node.Type,
			MaterializeTemporary(VisitNode(node.Target)), node.Member, node.Syntax.SourceLocation);
		
		private Value MaterializeTemporary(Value value) =>
			!IsPlaceValue(value) && _typePool.NeedsDrop(value.Type) ? StoreTemporary(value, "temporary") : value;
		
		public Value Visit(ResolvedLiteralExpressionNode node) =>
			MakeConstant(node.Type, node.Value);
		
		public Value Visit(ResolvedSizeOfExpressionNode node) =>
			new SizeOfValue(node.Target, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedValueParameterExpressionNode node) =>
			new ValueParameterValue(node.Parameter, node.Type, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedAlignOfExpressionNode node) =>
			new AlignOfValue(node.Target, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedNewExpressionNode node) => new NewValue(node.Type, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedErasedCallExpressionNode node)
		{
			LowerErasedCall(node);
			return new UndefValue(node.Type);
		}
		
		private void LowerErasedCall(ResolvedErasedCallExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var erasedBlock = CreateBlock("erased");
			var afterBlock = CreateBlock("after_erased");
			GetOrMakeBlock().SetTerminator(new ConditionalBranchTerminator(new ConstantValue(NativeSymbols.Bool, false),
				erasedBlock, afterBlock, location) { IsErasedCall = true });
			
			currentBlock = erasedBlock;
			BeginScope(location, true);
			var call = VisitNode(node.Call);
			if (currentBlock is not null)
			{
				if (_typePool.NeedsDrop(call.Type))
					StoreTemporary(call, "discard");
				else
					currentBlock.Instructions.Add(new ExpressionInstruction(call));
			}
			
			EndCurrentScope();
			currentBlock?.FillTerminator(new BranchTerminator(afterBlock, location));
			ContinueWith(afterBlock);
		}
		
		public Value Visit(ResolvedArrayExpressionNode node) => new ArrayValue((ArrayType)node.Type,
			LowerOperands(node.Values, static _ => Passing.Consume), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedArrayBuildExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var id = NextLoopId();
			var make = StoreTemporary(Consume(VisitNode(node.Make)), "make", location);
			var resultSymbol = new LocalVariableSymbol(
				new(TokenType.Identifier, SourceLocation.None, $".t{NextTempId()}__built"), node.Type, true)
			{
				IsFilledInPlace = true
			};
			
			_temporaries.Add(resultSymbol);
			Declare(GetOrMakeBlock(),
				new LocalVarInstruction(resultSymbol, new UndefValue(node.Type), location, CurrentScopeId));
			
			var index = StoreTemporary(MakeConstant(NativeSymbols.UIntSize, BigInteger.Zero), "index", location);
			var length = CaptureAsAtomic(VisitNode(node.Length), "length");
			var result = new VariableValue(new(resultSymbol, node.Type), location);
			var condBlock = CreateBlock($"build{id}_cond");
			var bodyBlock = CreateBlock($"build{id}_body");
			var doneBlock = CreateBlock($"build{id}_done");
			GetOrMakeBlock().SetTerminator(new BranchTerminator(condBlock, location));
			condBlock.SetTerminator(new ConditionalBranchTerminator(
				new BinOpValue(NativeSymbols.Bool, index, length, BinaryOperation.Less, location), bodyBlock, doneBlock,
				location));
			
			currentBlock = bodyBlock;
			var element = ((ArrayType)node.Type).ElementType;
			var value = Diverge(new IndirectCallValue(make, [index], node.MakeType, location));
			if (currentBlock is { } block)
			{
				block.Instructions.Add(new ExpressionInstruction(new AssignValue(element,
					new IndexerValue(element, result, index, location), Consume(value), location)));
				
				Increment(block, index, condBlock);
			}
			
			currentBlock = doneBlock;
			return result;
		}
		
		public Value Visit(ResolvedPackExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var arrayType = _typePool.GetArrayType(node.ElementType, node.Values.Length);
			var values = StoreTemporary(new ArrayValue(arrayType,
				LowerOperands(node.Values, static _ => Passing.Consume),
				location), "pack");
			
			var pointer = new UnaryOpValue(_typePool.GetPointerType(arrayType), values, UnaryOperation.AddressOf,
				location);
			
			return new ConversionValue(pointer, _typePool.ConversionTable.FindImplicit(pointer.Type, node.Type)!,
				location);
		}
		
		public Value Visit(ResolvedInterpolatedStringExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedAtomicExpressionNode node)
		{
			var operands = new[] { node.Pointer, node.Expected, node.Value };
			var values = new Queue<Value>(LowerOperands([..operands.OfType<IResolvedExpressionNode>()]));
			var lowered = operands.Select(operand => operand is null ? null : values.Dequeue()).ToArray();
			
			return new AtomicValue(node.Access, node.Ordering, lowered[0], node.Operation, lowered[1], lowered[2],
				node.Type, node.Syntax.SourceLocation);
		}
		
		public Value Visit(ResolvedFStrExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var type = (FStrType)node.Type;
			if (node.Text is { } text)
				return new FStrValue(type, [], LowerBorrow(text), null, 0, location);
			
			if (node.Values.IsEmpty)
				return new FStrValue(type, node.Texts, null, null, 0, location);
			
			var arrayType = _typePool.GetArrayType(_typePool.GetBorrowType(type.Value, false), node.Values.Length);
			var symbol = CreateTempSymbol(arrayType, "fstr");
			var elements = new ArrayValue(arrayType, LowerOperands(node.Values), location);
			Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, elements, location, BlockScopeId));
			
			var values = new UnaryOpValue(_typePool.GetPointerType(arrayType),
				new VariableValue(new(symbol, arrayType), location), UnaryOperation.AddressOf, location);
			
			return new FStrValue(type, node.Texts, null, values, node.Values.Length, location);
		}
		
		public Value Visit(ResolvedFStrPartExpressionNode node)
		{
			var target = VisitNode(node.Target);
			if (MayEmit(node.Index))
				target = CaptureAsAtomic(target, "target");
			
			return new FStrPartValue(node.Type, target, node.Part, VisitNode(node.Index), node.Syntax.SourceLocation);
		}
		
		public Value Visit(ResolvedUnaryOpExpressionNode node)
		{
			var isAddress = node.Operation?.Op == TokenType.OpAt;
			var operand = isAddress ? VisitPlace(node.Operand) : VisitNode(node.Operand);
			return Diverge(LowerUnaryOp(operand, node.Operation, isAddress));
		}
		
		public Value Visit(ResolvedUndefExpressionNode node) =>
			new UndefValue(node.Type);
		
		public Value Visit(ResolvedOwnExpressionNode node) => new MoveValue(VisitPlace(node.Value));
		
		public Value Visit(ResolvedQuestionExpressionNode node)
		{
			var operand = Consume(VisitNode(node.Operand));
			Declare(GetOrMakeBlock(),
				new LocalVarInstruction(node.Self, operand, node.Syntax.SourceLocation, CurrentScopeId));
			
			_returnedValues.Add(node.Body);
			return VisitNode(node.Body);
		}
		
		public Value Visit(ResolvedMutArgumentExpressionNode node) => new UnaryOpValue(node.Type,
			VisitPlace(node.Place), UnaryOperation.AddressOf, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedBorrowExpressionNode node)
		{
			var value = VisitPlace(node.Place);
			var place = IsPlaceValue(value) ? value : StoreTemporary(value, "borrow", node.Place.Syntax.SourceLocation);
			return new UnaryOpValue(node.Type, place, UnaryOperation.AddressOf, node.Syntax.SourceLocation);
		}
		
		public Value Visit(ResolvedVarExpressionNode node) =>
			new VariableValue(new(node.Symbol, node.Type), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedGlobalExpressionNode node) =>
			new GlobalValue(_getGlobalInfo(node.Symbol), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedBinaryOpExpressionNode node)
		{
			if (IsShortCircuitOp(node.Operation))
				return LowerShortCircuit(node.Left, node.Operation!.Op, node.Right);
			
			var operands = LowerOperands([node.Left, node.Right]);
			return Diverge(LowerBinOp(operands[0], node.Operation, operands[1], node.Syntax.SourceLocation));
		}
	}
}