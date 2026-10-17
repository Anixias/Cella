using Cella.Core.Binding;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using static Cella.Core.Binding.Operations.OperationMapping;

namespace Cella.Core.Lowering;

public sealed partial class Lowerer
{
	private sealed partial class FunctionLowerer
	{
		private Value LowerAssignment(ResolvedAssignmentExpressionNode node, bool isValue)
		{
			var left = StabilizeOperands(VisitPlace(node.Left));
			var dropsOld = !node.IsOwnStore && _typePool.NeedsDrop(node.Type);
			var right = Consume(VisitNode(node.Right), NamesPlace(node.Right));
			if (currentBlock is null)
				return left;
			
			if (node.Operation is not null || dropsOld || isValue && AccessesThroughOperator(left))
			{
				right = CaptureAsAtomic(right, "assigned");
				left = StabilizeStorage(left);
			}
			
			var value = node.Operation is null
				? right
				: LowerBinOp(left, node.Operation, right, node.Syntax.SourceLocation);
			
			if (dropsOld)
			{
				value = Consume(value);
				currentBlock.Instructions.Add(new DropInstruction(left, node.Op.SourceLocation, isReassignment: true));
			}
			
			currentBlock.Instructions.Add(
				new ExpressionInstruction(new AssignValue(node.Type, left, value, node.Op.SourceLocation)));
			
			return left;
		}
		
		public Value Visit(ResolvedChainedExpressionNode node)
		{
			if (node.Links.Any(static link =>
				    link.Function is not null || link.LeftConversion is not null || link.RightConversion is not null))
				return LowerDeclaredChain(node);
			
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "chain_result");
			var block = GetOrMakeBlock();
			block.Instructions.Add(new LocalVarInstruction(resultSymbol, new UndefValue(NativeSymbols.Bool),
				node.Syntax.SourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), node.Syntax.SourceLocation);
			
			var mergeBlock = CreateBlock("chain_merge");
			var left = VisitNode(node.Operands[0]);
			GetOrMakeBlock();
			
			for (var i = 0; i < node.Links.Length; i++)
			{
				var op = node.Links[i].Operation!;
				var rightNode = node.Operands[i + 1];
				var isLast = i == node.Links.Length - 1;
				
				if (!isLast || MayEmit(rightNode))
					left = CaptureAsAtomic(left, "chain_left");
				
				Value right;
				if (isLast)
				{
					right = VisitNode(rightNode);
					block = GetOrMakeBlock();
				}
				else
				{
					var tempSymbol = CreateTempSymbol(rightNode.Type, $"chain_inner{i}");
					var rightValue = VisitNode(rightNode);
					block = GetOrMakeBlock();
					Declare(block, new LocalVarInstruction(tempSymbol, rightValue, rightNode.Syntax.SourceLocation,
						CurrentScopeId));
					
					right = new VariableValue(new(tempSymbol, rightNode.Type), rightNode.Syntax.SourceLocation);
				}
				
				var comparison = LowerBinOp(left, op, right, node.Syntax.SourceLocation);
				
				if (isLast)
				{
					block.Instructions.Add(new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result,
						comparison, Join(result.SourceLocation, comparison.SourceLocation))));
					
					block.SetTerminator(new BranchTerminator(mergeBlock, node.Syntax.SourceLocation));
				}
				else
				{
					var nextBlock = CreateBlock("chain_next");
					var falseBlock = CreateBlock("chain_false");
					
					block.SetTerminator(new ConditionalBranchTerminator(comparison, nextBlock, falseBlock,
						comparison.SourceLocation));
					
					currentBlock = falseBlock;
					currentBlock.Instructions.Add(
						new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, ConstantValue.False,
							comparison.SourceLocation)));
					
					currentBlock.SetTerminator(new BranchTerminator(mergeBlock, node.Syntax.SourceLocation));
					
					currentBlock = nextBlock;
					left = right;
				}
			}
			
			ContinueWith(mergeBlock);
			return result;
		}
		
		private Value LowerDeclaredChain(ResolvedChainedExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "chain_result");
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(resultSymbol, new UndefValue(NativeSymbols.Bool),
				location, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), location);
			var mergeBlock = CreateBlock("chain_merge");
			var left = HoldChainOperand(node.Operands[0]);
			Value? convertedLeft = null;
			for (var i = 0; i < node.Links.Length; i++)
			{
				var link = node.Links[i];
				var right = HoldChainOperand(node.Operands[i + 1]);
				var convertedRight = i + 1 < node.Links.Length && SharesConversion(link, node.Links[i + 1])
					? HoldConvertedOperand(ConvertChainOperand(right, link.RightConversion,
						NamesPlace(node.Operands[i + 1])))
					: null;
				
				var comparison = link.Function is { } function
					? new CallValue(function, [
						convertedLeft is not null
							? PassChainArgument(convertedLeft, function, 0, true)
							: PassChainOperand(left, function, 0, link.LeftConversion, NamesPlace(node.Operands[i])),
						convertedRight is not null
							? PassChainArgument(convertedRight, function, 1, true)
							: PassChainOperand(right, function, 1, link.RightConversion,
								NamesPlace(node.Operands[i + 1]))
					], location)
					: LowerBinOp(
						convertedLeft ?? ConvertChainOperand(left, link.LeftConversion, NamesPlace(node.Operands[i])),
						link.Operation!,
						convertedRight ?? ConvertChainOperand(right, link.RightConversion,
							NamesPlace(node.Operands[i + 1])), location);
				
				var block = GetOrMakeBlock();
				if (i == node.Links.Length - 1)
				{
					block.Instructions.Add(new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result,
						comparison, location)));
					
					block.SetTerminator(new BranchTerminator(mergeBlock, location));
					continue;
				}
				
				var nextBlock = CreateBlock("chain_next");
				var falseBlock = CreateBlock("chain_false");
				block.SetTerminator(new ConditionalBranchTerminator(comparison, nextBlock, falseBlock, location));
				
				currentBlock = falseBlock;
				currentBlock.Instructions.Add(new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result,
					ConstantValue.False, location)));
				
				currentBlock.SetTerminator(new BranchTerminator(mergeBlock, location));
				currentBlock = nextBlock;
				left = right;
				convertedLeft = convertedRight;
			}
			
			ContinueWith(mergeBlock);
			return result;
		}
		
		private bool SharesConversion(ChainLink link, ChainLink next)
		{
			if (link.RightConversion is not { } conversion || next.LeftConversion is not { } other ||
			    conversion.GetType() != other.GetType() || conversion.From != other.From || conversion.To != other.To ||
			    conversion is FunctionConversion first && other is FunctionConversion second &&
			    first.Function != second.Function)
				return false;
			
			return _typePool.IsCopy(conversion.To) ||
			       link.Function is { } function && next.Function is { } nextFunction &&
			       GetPassing(function.DeclaredSignature.GetMode(1)) == Passing.Borrow &&
			       GetPassing(nextFunction.DeclaredSignature.GetMode(0)) == Passing.Borrow;
		}
		
		private Value HoldConvertedOperand(Value converted)
		{
			GetOrMakeBlock();
			return _typePool.IsCopy(converted.Type)
				? CaptureAsAtomic(converted, "chain_converted")
				: StoreTemporary(converted, "chain_converted");
		}
		
		private Value HoldChainOperand(IResolvedExpressionNode operand)
		{
			var value = VisitNode(operand);
			GetOrMakeBlock();
			if (_typePool.IsCopy(value.Type))
				return CaptureAsAtomic(value, "chain_operand");
			
			return IsPlaceValue(value) ? value : StoreTemporary(value, "chain_operand");
		}
		
		private Value ConvertChainOperand(Value operand, Conversion? conversion, bool isPlace) => conversion switch
		{
			null => operand,
			FunctionConversion { Function: var function } =>
				new CallValue(function, [PassChainArgument(operand, function, 0, isPlace)], operand.SourceLocation),
			_ => new ConversionValue(operand, conversion, operand.SourceLocation)
		};
		
		private Value PassChainOperand(Value operand, FunctionInfo function, int index, Conversion? conversion,
			bool isPlace) => PassChainArgument(ConvertChainOperand(operand, conversion, isPlace), function, index,
			isPlace && conversion is null);
		
		private Value PassChainArgument(Value value, FunctionInfo function, int index, bool isPlace)
		{
			var signature = function.DeclaredSignature;
			return GetPassing(signature.GetMode(index)) switch
			{
				Passing.Borrow => new UnaryOpValue(_typePool.GetPointerType(value.Type),
					IsPlaceValue(value) ? value : StoreTemporary(value, "borrow"), UnaryOperation.AddressOf,
					value.SourceLocation),
				Passing.Consume => Consume(value, isPlace),
				_ => value
			};
		}
		
		/// <summary>
		/// Continues the current function with the given block if it has any predecessors; otherwise, removes it.
		/// </summary>
		/// <param name="block"></param>
		private void ContinueWith(BasicBlock block)
		{
			if (block.HasPredecessor())
			{
				currentBlock = block;
				return;
			}
			
			_function.Blocks.Remove(block);
			currentBlock = null;
		}
		
		private Value LowerBinOp(Value left, OperationImpl? op, Value right, SourceLocation location) => op switch
		{
			NativeImpl { ParameterTypes: [EnumSymbol enumType, _] } i => new BinOpValue(i.ReturnType,
				new EnumTagValue(_typePool.GetTagType(enumType), left, left.SourceLocation),
				new EnumTagValue(_typePool.GetTagType(enumType), right, right.SourceLocation),
				ToBinaryOperation(i.Op), location),
			NativeImpl i => new BinOpValue(i.ReturnType, left, right, ToBinaryOperation(i.Op), location),
			PointerOffsetImpl { IsReversed: true } i => new PointerOffsetValue(i.PointerType, right, left,
				ToBinaryOperation(i.Op), location),
			PointerOffsetImpl i => new PointerOffsetValue(i.PointerType, left, right, ToBinaryOperation(i.Op),
				location),
			PointerDifferenceImpl i => new PointerDifferenceValue(left, right, i.PointerType, location),
			_ => throw new InvalidOperationException()
		};
		
		private static Value LowerUnaryOp(Value operand, OperationImpl? op, bool isRaw) => op switch
		{
			NativeImpl native => new UnaryOpValue(native.ReturnType, operand, ToUnaryOperation(native.Op),
				operand.SourceLocation) { IsRaw = isRaw },
			_ => throw new InvalidOperationException()
		};
		
		private static SourceLocation Join(SourceLocation left, SourceLocation right)
		{
			var (source, range) = left;
			range = range.Join(right.Range);
			return new(source, range);
		}
		
		private Value StabilizeStorage(Value value) => value switch
		{
			IndexerValue v => new IndexerValue(v.Type, StabilizeStorageBase(v.Target),
				CaptureAsAtomic(v.Index, "index"), v.SourceLocation),
			AccessValue v => new AccessValue(v.Type, StabilizeStorageBase(v.Target), v.Member, v.SourceLocation),
			UnaryOpValue { Op: UnaryOperation.Dereference } v => new UnaryOpValue(v.Type,
				CaptureAsAtomic(v.Operand, "addr"), UnaryOperation.Dereference, v.SourceLocation),
			_ => value
		};
		
		private Value StabilizeStorageBase(Value value) => value switch
		{
			VariableValue or GlobalValue => value,
			IndexerValue or AccessValue or UnaryOpValue { Op: UnaryOperation.Dereference } => StabilizeStorage(value),
			_ => CaptureAsAtomic(value, "target")
		};
		
		private Value StabilizeOperands(Value value) => value switch
		{
			IndexerValue v => new IndexerValue(v.Type, StabilizeOperandsBase(v.Target),
				CaptureAsAtomic(v.Index, "index"), v.SourceLocation),
			AccessValue v => new AccessValue(v.Type, StabilizeOperandsBase(v.Target), v.Member, v.SourceLocation),
			UnaryOpValue { Op: UnaryOperation.Dereference, Operand: CallValue call } v when IsPlaceOperator(
					call.Function) =>
				new UnaryOpValue(v.Type, StabilizeOperatorCall(call), UnaryOperation.Dereference, v.SourceLocation),
			_ => StabilizeStorage(value)
		};
		
		private Value StabilizeOperandsBase(Value value) => value switch
		{
			VariableValue or GlobalValue => value,
			IndexerValue or AccessValue or UnaryOpValue { Op: UnaryOperation.Dereference } => StabilizeOperands(value),
			_ => CaptureAsAtomic(value, "target")
		};
		
		private CallValue StabilizeOperatorCall(CallValue call)
		{
			var passing = GetArgumentPassing(call.Function, 0);
			return new CallValue(call.Function, call.Arguments.Select((argument, i) => SettleOperand(argument,
				passing(i), i == 0 || passing(i) == Passing.Borrow || argument is UnaryOpValue
				{
					Op: UnaryOperation.AddressOf
				})), call.SourceLocation);
		}
		
		private Value SettleOperand(Value operand, Passing passing, bool defersAccess)
		{
			if (defersAccess && operand is UnaryOpValue { Op: UnaryOperation.AddressOf } address)
				return new UnaryOpValue(address.Type, StabilizeOperandsBase(address.Operand), UnaryOperation.AddressOf,
					address.SourceLocation) { IsRaw = address.IsRaw };
			
			if (defersAccess && IsPlaceValue(operand))
				return StabilizeOperandsBase(operand);
			
			var captured = CaptureAsAtomic(operand, "operand");
			return passing == Passing.Consume ? Consume(captured) : captured;
		}
		
		private bool HasAccessWork(Value value) => value switch
		{
			UnaryOpValue { Op: UnaryOperation.AddressOf } v => HasAccessWork(v.Operand),
			IndexerValue => true,
			AccessValue v => HasAccessWork(v.Target),
			UnaryOpValue { Op: UnaryOperation.Dereference } v => !IsStable(v.Operand),
			_ => false
		};
		
		private static bool IsPlaceOperator(FunctionInfo function) =>
			function.Symbol is { Kind: FunctionKind.Method, Syntax: FunctionNode { Identifier.Text: "[]" or "*" } };
		
		private static bool AccessesThroughOperator(Value place) => place switch
		{
			UnaryOpValue { Op: UnaryOperation.Dereference, Operand: CallValue call } => IsPlaceOperator(call.Function),
			IndexerValue v => AccessesThroughOperator(v.Target),
			AccessValue v => AccessesThroughOperator(v.Target),
			_ => false
		};
		
		private Value CaptureAsAtomic(Value value, string hint) =>
			IsStable(value) ? value : StoreTemporary(value, hint);
		
		private VariableValue StoreTemporary(Value value, string hint) =>
			StoreTemporary(value, hint, value.SourceLocation);
		
		private VariableValue StoreTemporary(Value value, string hint, SourceLocation location)
		{
			var symbol = CreateTempSymbol(value.Type, hint);
			
			Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, value, location, CurrentScopeId));
			
			return new VariableValue(new(symbol, value.Type), location);
		}
		
		private bool IsStable(Value value) => value switch
		{
			ConstantValue or ZeroValue or DefaultValue or UndefValue or FunctionReferenceValue or SizeOfValue
				or AlignOfValue or ValueParameterValue or TraitConstantValue => true,
			VariableValue { Variable.Symbol: LocalVariableSymbol symbol } =>
				!symbol.IsMutable && !symbol.IsDeferred || _temporaries.Contains(symbol),
			VariableValue { Variable.Symbol: ParameterSymbol { Mode: ParameterMode.Mut } } => true,
			MoveValue { Place: VariableValue { Variable.Symbol: LocalVariableSymbol symbol } } =>
				_temporaries.Contains(symbol),
			_ => false
		};
	}
}