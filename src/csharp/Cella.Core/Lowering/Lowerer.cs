using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
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
	public void Visit(ResolvedExternalFunctionNode node) => CurrentModule.ExternalFunctions.Add(node.FunctionInfo);
	
	private sealed class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value>
	{
		private readonly record struct LoopContext(BasicBlock BreakTarget, BasicBlock ContinueTarget);
		
		private readonly LoweredFunction _function;
		private readonly Stack<LoopContext> _loopStack = [];
		private readonly Dictionary<LabelSymbol, LoopContext> _loopsByLabel = [];
		private BasicBlock currentBlock;
		private ulong nextLoopId;
		
		private FunctionLowerer(LoweredFunction function)
		{
			_function = function;
			currentBlock = CreateBlock("entry");
		}
		
		private ulong NextLoopId() => nextLoopId++;
		
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
				
				// If expression body, fabricate a return statement
				case IResolvedExpressionNode expression:
					lower.Visit(new ResolvedReturnStatementNode(expression));
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
			
			if (currentBlock is { Terminator: UndefinedTerminator, Instructions.Count: > 0 })
				currentBlock.Terminator = new BranchTerminator(mergeBlock);
			
			// Else block
			if (node.Else is { } @else)
			{
				currentBlock = elseBlock!;
				VisitNode(@else);
				
				if (currentBlock is { Terminator: UndefinedTerminator, Instructions.Count: > 0 })
					currentBlock.Terminator = new BranchTerminator(mergeBlock);
			}
			
			// Finish
			currentBlock = mergeBlock;
		}
		
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
			var counterNode = new VarStatementNode(SourceLocation.None,
				new Token(TokenType.Identifier, SourceLocation.None, $"repeat{id}$i"), null, null);
			var counterSymbol = new LocalVariableSymbol(counterNode, countValue.Type);
			currentBlock.Instructions.Add(new LocalVarInstruction(counterSymbol, countValue));
			
			var counterVar = new VariableValue(new(counterSymbol, countValue.Type));
			var one = new ConstantValue(countValue.Type, 1);
			var zero = new ConstantValue(countValue.Type, 0);
			
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
		
		public Value Visit(ResolvedAccessExpressionNode node) =>
			new AccessValue(node.Type, VisitNode(node.Target), node.Member);
		
		public Value Visit(ResolvedLiteralExpressionNode node) =>
			new ConstantValue(node.Type, node.Value);
		
		public Value Visit(ResolvedArrayExpressionNode node) =>
			new ArrayValue((ArrayType)node.Type, node.Values.Select(VisitNode));
		
		public Value Visit(ResolvedUnaryOpExpressionNode node) =>
			LowerUnaryOp(VisitNode(node.Operand), node.Operation);
		
		public Value Visit(ResolvedVarExpressionNode node) =>
			new VariableValue(new(node.Symbol, node.Type));
		
		public Value Visit(ResolvedBinaryOpExpressionNode node) =>
			LowerBinOp(VisitNode(node.Left), node.Operation, VisitNode(node.Right));
		
		public Value Visit(ResolvedAssignmentExpressionNode node) =>
			LowerAssignment(VisitNode(node.Left), node.Op, VisitNode(node.Right), node.Type);
		
		public Value Visit(ResolvedChainedExpressionNode node)
		{
			// TODO Implement short-circuiting
			
			// First and last operands don't need temporaries, so subtract 2
			var tempVarCount = node.Operands.Length - 2;
			var tempValues = new List<Value>(tempVarCount);
			
			for (var i = 1; i < node.Operands.Length - 1; i++)
			{
				var operand = node.Operands[i];
				
				var tempNode = new VarStatementNode(SourceLocation.None,
					new Token(TokenType.Identifier, SourceLocation.None), null, null);
				
				var tempSymbol = new LocalVariableSymbol(tempNode, operand.Type);
				var tempValue = VisitNode(operand);
				
				currentBlock.Instructions.Add(new LocalVarInstruction(tempSymbol, tempValue));
				tempValues.Add(tempValue);
			}
			
			Value? result = null;
			var left = VisitNode(node.Operands[0]);
			
			for (var i = 1; i < node.Operands.Length; i++)
			{
				var right = i == node.Operands.Length - 1
					? VisitNode(node.Operands[i])
					: tempValues[i - 1];
				
				var op = node.Ops[i - 1];
				var comparison = LowerBinOp(left, op, right);
				left = right;
				
				result = result is null
					? comparison
					: new BinOpValue(comparison.Type, result, comparison, BinaryOperation.And);
			}
			
			return result!;
		}
		
		private static AssignValue LowerAssignment(Value left, Token op, Value right, TypeSymbol type)
		{
			if (op.Type == TokenType.OpEqual)
				return new AssignValue(type, left, right);
			
			if (op.Type == TokenType.OpPlusEqual)
				return new AssignValue(type, left, new BinOpValue(type, left, right, BinaryOperation.Addition));
			
			if (op.Type == TokenType.OpMinusEqual)
				return new AssignValue(type, left, new BinOpValue(type, left, right, BinaryOperation.Subtraction));
			
			if (op.Type == TokenType.OpStarEqual)
				return new AssignValue(type, left, new BinOpValue(type, left, right, BinaryOperation.Multiplication));
			
			if (op.Type == TokenType.OpSlashEqual)
				return new AssignValue(type, left, new BinOpValue(type, left, right, BinaryOperation.Division));
			
			throw new InvalidOperationException();
		}
		
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
		
		private static BinaryOperation MapBinOp(TokenType op)
		{
			if (op == TokenType.OpPlus)
				return BinaryOperation.Addition;
			
			if (op == TokenType.OpMinus)
				return BinaryOperation.Subtraction;
			
			if (op == TokenType.OpStar)
				return BinaryOperation.Multiplication;
			
			if (op == TokenType.OpSlash)
				return BinaryOperation.Division;
			
			if (op == TokenType.OpEqualEqual)
				return BinaryOperation.Equal;
			
			if (op == TokenType.OpBangEqual)
				return BinaryOperation.NotEqual;
			
			if (op == TokenType.OpGreater)
				return BinaryOperation.Greater;
			
			if (op == TokenType.OpGreaterEqual)
				return BinaryOperation.GreaterEqual;
			
			if (op == TokenType.OpLess)
				return BinaryOperation.Less;
			
			if (op == TokenType.OpLessEqual)
				return BinaryOperation.LessEqual;
			
			if (op == TokenType.OpAmpersand)
				return BinaryOperation.And;
			
			if (op == TokenType.OpBar)
				return BinaryOperation.Or;
			
			if (op == TokenType.OpHat)
				return BinaryOperation.Xor;
			
			throw new InvalidOperationException();
		}
		
		private static UnaryOperation MapUnaryOp(TokenType op)
		{
			if (op == TokenType.OpPlus)
				return UnaryOperation.Identity;
			
			if (op == TokenType.OpMinus)
				return UnaryOperation.Negation;
			
			if (op == TokenType.OpBang)
				return UnaryOperation.Not;
			
			throw new InvalidOperationException();
		}
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