using System.Numerics;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed class Lowerer : IResolvedDeclarationNodeVisitor
{
	public IReadOnlyCollection<LoweredModule> Modules => _modules.Values;
	
	private readonly Dictionary<ModuleSymbol, LoweredModule> _modules = [];
	private readonly Stack<LoweredModule> _moduleStack = [];
	private LoweredModule CurrentModule => _moduleStack.Peek();
	
	public void Lower(IResolvedDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IResolvedDeclarationNode node) => ((IResolvedDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(ResolvedFileNode node)
	{
		var moduleSymbol = node.Symbol.Module;
		if (!_modules.TryGetValue(moduleSymbol, out var loweredModule))
		{
			loweredModule = new(moduleSymbol);
			_modules[moduleSymbol] = loweredModule;
		}
		
		loweredModule.ImportedFunctions.AddRange(node.ImportedFunctions);
		
		_moduleStack.Push(loweredModule);
		
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
		
		_moduleStack.Pop();
	}
	
	public void Visit(ResolvedFunctionNode node) => CurrentModule.Functions.Add(FunctionLowerer.Lower(node));
	public void Visit(ResolvedInvalidDeclarationNode node) => throw new InvalidOperationException();
	
	public void Visit(ResolvedRecordNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
		
		CurrentModule.Types.Add(node.Symbol);
	}
	
	public void Visit(ResolvedFieldNode node)
	{
		// TODO Do anything?
	}
	
	public void Visit(ResolvedMethodNode node)
	{
		// TODO Handle self-reference?
		VisitNode(node.FunctionNode);
	}
	
	public void Visit(ResolvedExternalFunctionNode node) => CurrentModule.ExternalFunctions.Add(node.FunctionInfo);
	
	private sealed class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value>
	{
		private readonly record struct LoopContext(BasicBlock BreakTarget, BasicBlock ContinueTarget);
		
		private readonly LoweredFunction _function;
		private readonly Stack<LoopContext> _loopStack = [];
		private readonly Dictionary<LabelSymbol, LoopContext> _loopsByLabel = [];
		private BasicBlock currentBlock;
		private ulong nextLoopId;
		private ulong nextTempId;
		
		private FunctionLowerer(LoweredFunction function)
		{
			_function = function;
			currentBlock = CreateBlock("entry");
		}
		
		private ulong NextLoopId() => nextLoopId++;
		private ulong NextTempId() => nextTempId++;
		
		private BasicBlock CreateBlock(string hint)
		{
			var block = new BasicBlock($"{hint}_{_function.Blocks.Count}");
			_function.Blocks.Add(block);
			return block;
		}
		
		private LoopContext GetLoopContext(LabelSymbol? label) => label is null
			? _loopStack.Peek()
			: _loopsByLabel[label];
		
		public static LoweredFunction Lower(ResolvedFunctionNode node)
		{
			var function = new LoweredFunction(node.FunctionInfo);
			var lower = new FunctionLowerer(function);
			
			switch (node.Body)
			{
				case IResolvedStatementNode statement:
					lower.VisitNode(statement);
					break;
				
				// If expression body, synthesize a return statement
				case IResolvedExpressionNode expression:
					lower.Visit(new ResolvedReturnStatementNode(expression,
						new ReturnStatementNode(expression.Syntax.SourceLocation, expression.Syntax)));
					
					break;
			}
			
			// Normalize blocks
			switch (function.Blocks.Count)
			{
				case 0:
					function.Blocks.Add(new("entry") { Terminator = ReturnTerminator.Void });
					break;
				
				case 1:
				{
					var block = function.Blocks[0];
					
					if (block.Terminator == UndefinedTerminator.Instance)
						block.Terminator = ReturnTerminator.Void;
					
					break;
				}
				
				default:
				{
					var reachableBlocks = FindReachableBlocks(function);
					
					for (var i = function.Blocks.Count - 1; i >= 0; i--)
					{
						var block = function.Blocks[i];
						
						if (block.Terminator != UndefinedTerminator.Instance)
							continue;
						
						var isReachable = reachableBlocks.Contains(block);
						
						// Remove unused blocks
						if (block.Instructions.Count == 0 && !isReachable)
						{
							function.Blocks.RemoveAt(i);
							continue;
						}
						
						// TODO Warn about unreachable code
						
						block.Terminator = ReturnTerminator.Void;
					}
					
					break;
				}
			}
			
			// TODO Warn about unreachable code & remove
			// TODO Add control flow/data flow analysis before codegen
			
			return function;
		}
		
		private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
		
		private Value VisitNode(IResolvedExpressionNode node) =>
			((IResolvedExpressionNodeVisitor<Value>)this).Visit(node);
		
		public void Visit(ResolvedBlockStatementNode node)
		{
			foreach (var statement in node.Statements)
				VisitNode(statement);
		}
		
		public void Visit(ResolvedBreakStatementNode node)
		{
			var context = GetLoopContext(node.Label);
			currentBlock.Terminator = new BranchTerminator(context.BreakTarget);
			currentBlock = CreateBlock("unreachable");
		}
		
		public void Visit(ResolvedContinueStatementNode node)
		{
			var context = GetLoopContext(node.Label);
			currentBlock.Terminator = new BranchTerminator(context.ContinueTarget);
			currentBlock = CreateBlock("unreachable");
		}
		
		public void Visit(ResolvedExpressionStatementNode node)
		{
			var expression = VisitNode(node.Expression);
			currentBlock.Instructions.Add(new ExpressionInstruction(expression));
		}
		
		public void Visit(ResolvedIfStatementNode node)
		{
			// Condition & terminate block
			var condition = VisitNode(node.Condition);
			
			var thenBlock = CreateBlock("then");
			var elseBlock = node.Else is null ? null : CreateBlock("else");
			var mergeBlock = CreateBlock("merge");
			
			currentBlock.Terminator = new ConditionalBranchTerminator(condition, thenBlock, elseBlock ?? mergeBlock);
			
			// Then block
			currentBlock = thenBlock;
			VisitNode(node.Then);
			
			if (currentBlock.Terminator is UndefinedTerminator)
				currentBlock.Terminator = new BranchTerminator(mergeBlock);
			
			// Else block
			if (node.Else is { } @else)
			{
				currentBlock = elseBlock!;
				VisitNode(@else);
				
				if (currentBlock.Terminator is UndefinedTerminator)
					currentBlock.Terminator = new BranchTerminator(mergeBlock);
			}
			
			// Finish
			currentBlock = mergeBlock;
		}
		
		public void Visit(ResolvedInvalidStatementNode node) =>
			throw new InvalidOperationException();
		
		public void Visit(ResolvedReturnStatementNode node)
		{
			var value = node.Expression is null ? null : VisitNode(node.Expression);
			currentBlock.Terminator = ReturnTerminator.FromValue(value);
			
			// We don't want to add further instructions to this terminated block, so create a dummy block
			currentBlock = CreateBlock("unreachable");
		}
		
		public void Visit(ResolvedVarStatementNode node)
		{
			var value = node.Initializer is null ? null : VisitNode(node.Initializer);
			currentBlock.Instructions.Add(new LocalVarInstruction(node.Symbol, value));
		}
		
		private void VisitInLoop(IResolvedStatementNode body, BasicBlock breakBlock, BasicBlock continueBlock,
			LabelSymbol? label)
		{
			var loopContext = new LoopContext(breakBlock, continueBlock);
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
			
			currentBlock.Terminator = new BranchTerminator(bodyBlock);
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label);
			
			if (currentBlock.Terminator is UndefinedTerminator)
				currentBlock.Terminator = new BranchTerminator(condBlock);
			
			// Condition
			currentBlock = condBlock;
			var condition = VisitNode(node.Condition);
			currentBlock.Terminator = new ConditionalBranchTerminator(condition, bodyBlock, exitBlock);
			
			currentBlock = exitBlock;
		}
		
		public void Visit(ResolvedLoopStatementNode node)
		{
			var id = NextLoopId();
			var bodyBlock = CreateBlock($"loop{id}_body");
			var exitBlock = CreateBlock($"loop{id}_exit");
			
			currentBlock.Terminator = new BranchTerminator(bodyBlock);
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, bodyBlock, node.Label);
			
			if (currentBlock.Terminator is UndefinedTerminator)
				currentBlock.Terminator = new BranchTerminator(bodyBlock);
			
			currentBlock = exitBlock;
		}
		
		public void Visit(ResolvedRepeatStatementNode node)
		{
			var id = NextLoopId();
			
			// Create implicit counter variable initialized with count
			var countValue = VisitNode(node.Count);
			var counterSymbol = CreateTempSymbol(countValue.Type, $"repeat{id}$i");
			currentBlock.Instructions.Add(new LocalVarInstruction(counterSymbol, countValue));
			
			var counterVar = new VariableValue(new(counterSymbol, countValue.Type));
			var one = MakeConstant(countValue.Type, BigInteger.One);
			var zero = MakeConstant(countValue.Type, BigInteger.Zero);
			
			var condBlock = CreateBlock($"repeat{id}_cond");
			var bodyBlock = CreateBlock($"repeat{id}_body");
			var latchBlock = CreateBlock($"repeat{id}_latch");
			var exitBlock = CreateBlock($"repeat{id}_exit");
			
			currentBlock.Terminator = new BranchTerminator(condBlock);
			
			// Condition: counter > 0
			currentBlock = condBlock;
			var condition = new BinOpValue(countValue.Type, counterVar, zero, BinaryOperation.Greater);
			currentBlock.Terminator = new ConditionalBranchTerminator(condition, bodyBlock, exitBlock);
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, latchBlock, node.Label);
			
			if (currentBlock.Terminator is UndefinedTerminator)
				currentBlock.Terminator = new BranchTerminator(latchBlock);
			
			// Latch: decrement counter, jump back to condition
			currentBlock = latchBlock;
			var decrement = new AssignValue(countValue.Type, counterVar,
				new BinOpValue(countValue.Type, counterVar, one, BinaryOperation.Subtraction));
			latchBlock.Instructions.Add(new ExpressionInstruction(decrement));
			latchBlock.Terminator = new BranchTerminator(condBlock);
			
			currentBlock = exitBlock;
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
				_ => new(type, v)
			};
		}
		
		public void Visit(ResolvedWhileStatementNode node)
		{
			var id = NextLoopId();
			var condBlock = CreateBlock($"while{id}_cond");
			var bodyBlock = CreateBlock($"while{id}_body");
			var exitBlock = CreateBlock($"while{id}_exit");
			
			currentBlock.Terminator = new BranchTerminator(condBlock);
			
			// Condition
			currentBlock = condBlock;
			var condition = VisitNode(node.Condition);
			currentBlock.Terminator = new ConditionalBranchTerminator(condition, bodyBlock, exitBlock);
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label);
			
			if (currentBlock.Terminator is UndefinedTerminator)
				currentBlock.Terminator = new BranchTerminator(condBlock);
			
			currentBlock = exitBlock;
		}
		
		public Value Visit(ResolvedConversionExpressionNode node) =>
			new ConversionValue(VisitNode(node.Source), node.Conversion);
		
		public Value Visit(ResolvedFunctionCallExpressionNode node) =>
			new CallValue(node.Function, node.Arguments.Select(VisitNode));
		
		public Value Visit(ResolvedIndexerExpressionNode node) =>
			new IndexerValue(node.Type, VisitNode(node.Target), VisitNode(node.Index));
		
		public Value Visit(ResolvedInvalidExpressionNode node) =>
			throw new InvalidOperationException();
		
		public Value Visit(ResolvedAccessExpressionNode node) =>
			new AccessValue(node.Type, VisitNode(node.Target), node.Member);
		
		public Value Visit(ResolvedLiteralExpressionNode node) =>
			MakeConstant(node.Type, node.Value);
		
		public Value Visit(ResolvedArrayExpressionNode node) =>
			new ArrayValue((ArrayType)node.Type, node.Values.Select(VisitNode));
		
		public Value Visit(ResolvedUnaryOpExpressionNode node) =>
			LowerUnaryOp(VisitNode(node.Operand), node.Operation);
		
		public Value Visit(ResolvedUndefExpressionNode node) =>
			null!;
		
		public Value Visit(ResolvedVarExpressionNode node) =>
			new VariableValue(new(node.Symbol, node.Type));
		
		public Value Visit(ResolvedBinaryOpExpressionNode node)
		{
			if (IsShortCircuitOp(node.Operation))
				return LowerShortCircuit(node.Left, node.Operation!.Op, node.Right);
			
			return LowerBinOp(VisitNode(node.Left), node.Operation, VisitNode(node.Right));
		}
		
		private static bool IsShortCircuitOp(OperationImpl? op) =>
			op is NativeImpl { Op: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar };
		
		private VariableValue LowerShortCircuit(IResolvedExpressionNode leftNode, TokenType op,
			IResolvedExpressionNode rightNode)
		{
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "sc_result");
			currentBlock.Instructions.Add(new LocalVarInstruction(resultSymbol, null));
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool));
			
			var rightBlock = CreateBlock("sc_right");
			var mergeBlock = CreateBlock("sc_merge");
			
			var left = VisitNode(leftNode);
			
			currentBlock.Instructions.Add(
				new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, left)));
			
			currentBlock.Terminator = op == TokenType.OpAmpersandAmpersand ?
				new ConditionalBranchTerminator(result, rightBlock, mergeBlock)
				: new ConditionalBranchTerminator(result, mergeBlock, rightBlock);
			
			currentBlock = rightBlock;
			var right = VisitNode(rightNode);
			currentBlock.Instructions.Add(
				new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, right)));
			currentBlock.Terminator = new BranchTerminator(mergeBlock);
			currentBlock = mergeBlock;
			return result;
		}
		
		private LocalVariableSymbol CreateTempSymbol(TypeSymbol type, string name)
		{
			name = $"t{NextTempId()}__{name}";
			var node = new VarStatementNode(SourceLocation.None, new(TokenType.Identifier, SourceLocation.None, name),
				null, null);
			
			return new LocalVariableSymbol(node, type);
		}
		
		public Value Visit(ResolvedAssignmentExpressionNode node) =>
			LowerAssignment(VisitNode(node.Left), node.Op, VisitNode(node.Right), node.Type);
		
		public Value Visit(ResolvedChainedExpressionNode node)
		{
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "chain_result");
			currentBlock.Instructions.Add(new LocalVarInstruction(resultSymbol, null));
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool));
			
			var mergeBlock = CreateBlock("chain_merge");
			var left = VisitNode(node.Operands[0]);
			
			for (var i = 0; i < node.Ops.Length; i++)
			{
				var op = node.Ops[i];
				var rightNode = node.Operands[i + 1];
				var isLast = i == node.Ops.Length - 1;
				
				Value right;
				if (isLast)
				{
					right = VisitNode(rightNode);
				}
				else
				{
					var tempSymbol = CreateTempSymbol(rightNode.Type, $"chain_inner{i}");
					var rightValue = VisitNode(rightNode);
					currentBlock.Instructions.Add(new LocalVarInstruction(tempSymbol, rightValue));
					right = new VariableValue(new(tempSymbol, rightNode.Type));
				}
				
				var comparison = LowerBinOp(left, op, right);
				
				if (isLast)
				{
					currentBlock.Instructions.Add(
						new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, comparison)));
					
					currentBlock.Terminator = new BranchTerminator(mergeBlock);
				}
				else
				{
					var nextBlock = CreateBlock("chain_next");
					var falseBlock = CreateBlock("chain_false");
					
					currentBlock.Terminator = new ConditionalBranchTerminator(comparison, nextBlock, falseBlock);
					
					currentBlock = falseBlock;
					currentBlock.Instructions.Add(
						new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, ConstantValue.False)));
					
					currentBlock.Terminator = new BranchTerminator(mergeBlock);
					
					currentBlock = nextBlock;
					left = right;
				}
			}
			
			currentBlock = mergeBlock;
			return result;
		}
		
		private static AssignValue LowerAssignment(Value left, Token op, Value right, TypeSymbol type) => op.Type switch
		{
			TokenType.OpEqual => new AssignValue(type, left, right),
			TokenType.OpPlusEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.Addition)),
			TokenType.OpMinusEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.Subtraction)),
			TokenType.OpStarEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.Multiplication)),
			TokenType.OpSlashEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.Division)),
			TokenType.OpPercentEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.Modulo)),
			TokenType.OpAmpersandEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.BitwiseAnd)),
			TokenType.OpBarEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.BitwiseOr)),
			TokenType.OpHatEqual => new AssignValue(type, left,
				new BinOpValue(type, left, right, BinaryOperation.BitwiseXor)),
			_ => throw new InvalidOperationException()
		};
		
		private static Value LowerBinOp(Value left, OperationImpl? op, Value right) => op switch
		{
			NativeImpl native => new BinOpValue(native.Result, left, right, MapBinOp(native.Op)),
			FunctionImpl function => new CallValue(function.Function, [left, right]),
			_ => throw new InvalidOperationException()
		};
		
		private static Value LowerUnaryOp(Value operand, OperationImpl? op) => op switch
		{
			NativeImpl native => new UnaryOpValue(native.Result, operand, MapUnaryOp(native.Op)),
			FunctionImpl function => new CallValue(function.Function, [operand]),
			_ => throw new InvalidOperationException()
		};
		
		private static BinaryOperation MapBinOp(TokenType op) => op switch
		{
			TokenType.OpPlus => BinaryOperation.Addition,
			TokenType.OpMinus => BinaryOperation.Subtraction,
			TokenType.OpStar => BinaryOperation.Multiplication,
			TokenType.OpSlash => BinaryOperation.Division,
			TokenType.OpPercent => BinaryOperation.Modulo,
			TokenType.OpEqualEqual => BinaryOperation.Equal,
			TokenType.OpBangEqual => BinaryOperation.NotEqual,
			TokenType.OpGreater => BinaryOperation.Greater,
			TokenType.OpGreaterEqual => BinaryOperation.GreaterEqual,
			TokenType.OpLess => BinaryOperation.Less,
			TokenType.OpLessEqual => BinaryOperation.LessEqual,
			TokenType.OpAmpersand => BinaryOperation.BitwiseAnd,
			TokenType.OpBar => BinaryOperation.BitwiseOr,
			TokenType.OpHat => BinaryOperation.BitwiseXor,
			TokenType.OpAmpersandAmpersand => BinaryOperation.LogicalAnd,
			TokenType.OpBarBar => BinaryOperation.LogicalOr,
			_ => throw new InvalidOperationException()
		};
		
		private static UnaryOperation MapUnaryOp(TokenType op) => op switch
		{
			TokenType.OpPlus => UnaryOperation.Identity,
			TokenType.OpMinus => UnaryOperation.Negation,
			TokenType.OpTilde => UnaryOperation.BitwiseNot,
			TokenType.OpBang => UnaryOperation.LogicalNot,
			TokenType.OpAt => UnaryOperation.AddressOf,
			_ => throw new InvalidOperationException()
		};
	}
	
	private static HashSet<BasicBlock> FindReachableBlocks(LoweredFunction function)
	{
		if (function.Blocks.Count == 0)
			return [];
		
		var reachable = new HashSet<BasicBlock>();
		var queue = new Queue<BasicBlock>();
		queue.Enqueue(function.Blocks[0]);
		
		while (queue.Count > 0)
		{
			var block = queue.Dequeue();
			if (!reachable.Add(block))
				continue;
			
			switch (block.Terminator)
			{
				case BranchTerminator t:
					queue.Enqueue(t.Target);
					break;
				
				case ConditionalBranchTerminator t:
					queue.Enqueue(t.TrueTarget);
					queue.Enqueue(t.FalseTarget);
					break;
			}
		}
		
		return reachable;
	}
}