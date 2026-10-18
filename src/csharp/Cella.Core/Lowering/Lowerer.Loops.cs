using System.Numerics;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed partial class Lowerer
{
	private sealed partial class FunctionLowerer
	{
		public Value Visit(ResolvedReturnExpressionNode node)
		{
			LowerReturn(node.Value, node.Syntax.SourceLocation);
			return new UndefValue(node.Type);
		}
		
		private void LowerReturn(IResolvedExpressionNode? expression, SourceLocation location)
		{
			GetOrMakeBlock();
			if (expression is not null)
				_returnedValues.Add(expression);
			
			var value = expression is null ? null : Consume(VisitNode(expression));
			if (currentBlock is not { } block)
				return;
			
			if (expression is not null && value is not null)
			{
				var returnSymbol = CreateTempSymbol(value.Type, "return");
				block.Instructions.Add(new LocalVarInstruction(returnSymbol, value, location, scopeId: 0));
				value = Consume(new VariableValue(new(returnSymbol, value.Type), expression.Syntax.SourceLocation));
			}
			
			EmitScopeEndsToDepth(0);
			block.SetTerminator(ReturnTerminator.FromValue(value,
				_function.ExitLocation == SourceLocation.None ? location : _function.ExitLocation));
			
			currentBlock = null;
		}
		
		public void Visit(ResolvedVarStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation, true);
			var value = node switch
			{
				{ Initializer: { } initializer } => Consume(VisitNode(initializer), NamesPlace(initializer)),
				{ Symbol.IsDeferred: true } => new UndefValue(node.Symbol.Type),
				_ => new DefaultValue(node.Symbol.Type)
			};
			
			if (currentBlock is { } block)
				Declare(block, new LocalVarInstruction(node.Symbol, value, node.Syntax.SourceLocation, BlockScopeId));
			
			EndCurrentScope();
		}
		
		private void VisitInLoop(IResolvedStatementNode body, BasicBlock breakBlock, BasicBlock continueBlock,
			LabelSymbol? label, int breakDepth, int continueDepth)
		{
			var loopContext = new LoopContext(breakBlock, continueBlock, breakDepth, continueDepth);
			if (label is not null)
				_loopsByLabel[label] = loopContext;
			
			_loopStack.Push(loopContext);
			VisitNode(body);
			_loopStack.Pop();
		}
		
		public void Visit(ResolvedDoWhileStatementNode node)
		{
			var id = NextLoopId();
			var bodyBlock = CreateBlock($"dowhile{id}_body");
			var condBlock = CreateBlock($"dowhile{id}_cond");
			var exitBlock = CreateBlock($"dowhile{id}_exit");
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(bodyBlock, node.Syntax.SourceLocation));
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label, _activeScopes.Count, _activeScopes.Count);
			currentBlock?.FillTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			// Condition
			currentBlock = condBlock;
			var scope = BeginScope(node.Condition.Syntax.SourceLocation, true);
			var againBlock = CreateBlock($"dowhile{id}_again");
			var doneBlock = CreateBlock($"dowhile{id}_done");
			LowerBranch(node.Condition, againBlock, doneBlock);
			ExitScope(againBlock, scope, bodyBlock);
			ExitScope(doneBlock, scope, exitBlock);
			
			EndCurrentScope();
			ContinueWith(exitBlock);
		}
		
		public void Visit(ResolvedLoopStatementNode node)
		{
			var id = NextLoopId();
			var bodyBlock = CreateBlock($"loop{id}_body");
			var exitBlock = CreateBlock($"loop{id}_exit");
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(bodyBlock, node.Syntax.SourceLocation));
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, bodyBlock, node.Label, _activeScopes.Count, _activeScopes.Count);
			currentBlock?.FillTerminator(new BranchTerminator(bodyBlock, node.Syntax.SourceLocation));
			
			ContinueWith(exitBlock);
		}
		
		public void Visit(ResolvedRangeForStatementNode node)
		{
			var id = NextLoopId();
			var location = node.Syntax.SourceLocation;
			var breakDepth = _activeScopes.Count;
			var scope = BeginScope(location, true);
			if (node.Source is { Symbol: var source, Value: var sourceValue })
			{
				var address = source.Type is not PointerType || sourceValue is ResolvedMutArgumentExpressionNode
					? VisitNode(sourceValue)
					: LowerBorrow(sourceValue);
				
				Declare(GetOrMakeBlock(), new LocalVarInstruction(source, address, location, scope.Id));
			}
			
			var type = node.Counter.Type;
			var counter = new VariableValue(new(node.Counter, type), location);
			var start = VisitNode(node.Start);
			Declare(GetOrMakeBlock(), new LocalVarInstruction(node.Counter, start, location, scope.Id));
			var end = CaptureAsAtomic(VisitNode(node.End), "end");
			
			var condBlock = CreateBlock($"for{id}_cond");
			var bodyBlock = CreateBlock($"for{id}_body");
			var latchBlock = CreateBlock($"for{id}_latch");
			var doneBlock = CreateBlock($"for{id}_done");
			var exitBlock = CreateBlock($"for{id}_exit");
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(condBlock, location));
			condBlock.SetTerminator(new ConditionalBranchTerminator(new BinOpValue(NativeSymbols.Bool, counter, end,
					node.IsInclusive ? BinaryOperation.LessEqual : BinaryOperation.Less, location), bodyBlock,
				doneBlock,
				location));
			
			currentBlock = bodyBlock;
			LowerIteration(node.Element, node.Body, exitBlock, latchBlock, node.Label, breakDepth);
			currentBlock?.FillTerminator(new BranchTerminator(latchBlock, location));
			
			if (!latchBlock.HasPredecessor())
			{
				_function.Blocks.Remove(latchBlock);
			}
			else if (node.IsInclusive)
			{
				var stepBlock = CreateBlock($"for{id}_step");
				latchBlock.SetTerminator(new ConditionalBranchTerminator(new BinOpValue(NativeSymbols.Bool, counter,
					end, BinaryOperation.Equal, location), doneBlock, stepBlock, location));
				
				Increment(stepBlock, counter, bodyBlock);
			}
			else
			{
				Increment(latchBlock, counter, condBlock);
			}
			
			currentBlock = doneBlock;
			EndCurrentScope();
			doneBlock.SetTerminator(new BranchTerminator(exitBlock, location));
			ContinueWith(exitBlock);
		}
		
		private void Increment(BasicBlock block, VariableValue counter, BasicBlock next)
		{
			var (type, location) = (counter.Type, counter.SourceLocation);
			var sum = type == NativeSymbols.Char
				? new BinOpValue(type, counter, MakeConstant(NativeSymbols.Int32, BigInteger.One),
					BinaryOperation.Addition, location)
				: new BinOpValue(type, counter, MakeConstant(type, BigInteger.One), BinaryOperation.WrappingAddition,
					location);
			
			block.Instructions.Add(new ExpressionInstruction(new AssignValue(type, counter, sum, location)));
			block.SetTerminator(new BranchTerminator(next, location));
		}
		
		public void Visit(ResolvedCursorForStatementNode node)
		{
			var id = NextLoopId();
			var location = node.Syntax.SourceLocation;
			var breakDepth = _activeScopes.Count;
			var scope = BeginScope(location, true);
			var (cursor, cursorValue) = node.Cursor;
			var value = VisitNode(cursorValue);
			Declare(GetOrMakeBlock(), new LocalVarInstruction(cursor,
				cursor.IsBorrowBinding ? value : Consume(value, NamesPlace(cursorValue)), location, scope.Id));
			
			var condBlock = CreateBlock($"for{id}_cond");
			var bodyBlock = CreateBlock($"for{id}_body");
			var doneBlock = CreateBlock($"for{id}_done");
			var exitBlock = CreateBlock($"for{id}_exit");
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(condBlock, location));
			currentBlock = condBlock;
			LowerBranch(node.Step, bodyBlock, doneBlock);
			
			currentBlock = bodyBlock;
			LowerIteration(node.Element, node.Body, exitBlock, condBlock, node.Label, breakDepth);
			currentBlock?.FillTerminator(new BranchTerminator(condBlock, location));
			
			currentBlock = doneBlock;
			EndCurrentScope();
			doneBlock.SetTerminator(new BranchTerminator(exitBlock, location));
			ContinueWith(exitBlock);
		}
		
		private void LowerIteration(ResolvedLoopVariable? element, IResolvedStatementNode body, BasicBlock exitBlock,
			BasicBlock continueBlock, LabelSymbol? label, int breakDepth)
		{
			var continueDepth = _activeScopes.Count;
			if (element is null)
			{
				VisitInLoop(body, exitBlock, continueBlock, label, breakDepth, continueDepth);
				return;
			}
			
			var (symbol, place) = element;
			var location = symbol.Identifier.SourceLocation;
			var scope = BeginScope(location);
			var value = VisitNode(place);
			Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, symbol.IsBorrowBinding
				? new UnaryOpValue(symbol.Type, value, UnaryOperation.AddressOf, location)
				: value, location, scope.Id));
			
			VisitInLoop(body, exitBlock, continueBlock, label, breakDepth, continueDepth);
			EndCurrentScope();
		}
		
		private ConstantValue MakeConstant(TypeSymbol type, object? value)
		{
			if (type is not IntegerType intType || value is not BigInteger v)
				return new(type, value);
			
			return intType.Kind switch
			{
				PrimitiveTypeKind.Int8 => new(type, (sbyte)v),
				PrimitiveTypeKind.Int16 => new(type, (short)v),
				PrimitiveTypeKind.Int32 => new(type, (int)v),
				PrimitiveTypeKind.Int64 => new(type, (long)v),
				PrimitiveTypeKind.Int128 => new(type, (Int128)v),
				PrimitiveTypeKind.UInt8 => new(type, (byte)v),
				PrimitiveTypeKind.UInt16 => new(type, (ushort)v),
				PrimitiveTypeKind.UInt32 => new(type, (uint)v),
				PrimitiveTypeKind.UInt64 => new(type, (ulong)v),
				PrimitiveTypeKind.UInt128 => new(type, (UInt128)v),
				PrimitiveTypeKind.Char => new(type, (uint)v),
				_ => new(type, v)
			};
		}
	}
}