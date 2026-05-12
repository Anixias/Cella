using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed class Lowerer : IResolvedDeclarationNodeVisitor
{
	public IReadOnlyCollection<LoweredModule> Modules => _modules.Values;
	public IReadOnlyCollection<LoweredFile> Files => _files.Values;
	
	private readonly Dictionary<ModuleSymbol, LoweredModule> _modules = [];
	private readonly Dictionary<FileSymbol, LoweredFile> _files = [];
	private LoweredFile? currentFile;
	
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
		
		currentFile = new LoweredFile(node.Symbol);
		_files[node.Symbol] = currentFile;
		loweredModule.Files.Add(currentFile);
		currentFile.ImportedFunctions.AddRange(node.ImportedFunctions);
		
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
		
		currentFile = null;
	}
	
	public void Visit(ResolvedFunctionNode node) => currentFile?.Functions.Add(FunctionLowerer.Lower(node));
	public void Visit(ResolvedInvalidDeclarationNode node) => throw new InvalidOperationException();
	
	public void Visit(ResolvedRecordNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentFile?.Types.Add(node.Symbol);
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
	
	public void Visit(ResolvedExternalFunctionNode node) => currentFile?.ExternalFunctions.Add(node.FunctionInfo);
	
	private sealed class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value>
	{
		private readonly record struct LoopContext(BasicBlock BreakTarget, BasicBlock ContinueTarget, int ScopeDepth);
		
		private readonly LoweredFunction _function;
		private readonly Stack<LoopContext> _loopStack = [];
		private readonly Dictionary<LabelSymbol, LoopContext> _loopsByLabel = [];
		private readonly Stack<ActiveScope> _activeScopes = [];
		private BasicBlock? currentBlock;
		private ulong nextLoopId;
		private ulong nextTempId;
		private int nextScopeId;
		private int CurrentScopeId => _activeScopes.TryPeek(out var scope) ? scope.Id : 0;
		
		private readonly record struct ActiveScope(int Id, SourceLocation Location);
		
		private FunctionLowerer(LoweredFunction function)
		{
			_function = function;
			currentBlock = CreateBlock("entry");
		}
		
		private ulong NextLoopId() => nextLoopId++;
		private ulong NextTempId() => nextTempId++;
		
		private BasicBlock GetOrMakeBlock() => currentBlock ??= CreateBlock("block");
		
		private BasicBlock CreateBlock(string hint)
		{
			var block = new BasicBlock($"{hint}_{_function.Blocks.Count}", _function.Blocks);
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
					// If void return, we do not return the expression itself
					if (node.FunctionInfo.Signature.ReturnType == NativeSymbols.Void)
						lower.VisitNode(expression);
					else
						lower.Visit(new ResolvedReturnStatementNode(expression,
							new ReturnStatementNode(expression.Syntax.SourceLocation, expression.Syntax)));
					
					break;
			}
			
			return function;
		}
		
		private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
		
		private Value VisitNode(IResolvedExpressionNode node) =>
			((IResolvedExpressionNodeVisitor<Value>)this).Visit(node);
		
		private void BeginScope(SourceLocation location)
		{
			var id = ++nextScopeId;
			GetOrMakeBlock().Instructions.Add(new BeginScopeInstruction(id, location));
			_activeScopes.Push(new ActiveScope(id, location));
		}
		
		private void EndCurrentScope()
		{
			var scope = _activeScopes.Pop();
			
			if (currentBlock is { Terminator: UndefinedTerminator } block)
				block.Instructions.Add(new EndScopeInstruction(scope.Id, scope.Location.End));
		}
		
		private void EmitScopeEndsToDepth(int targetDepth)
		{
			var block = GetOrMakeBlock();
			foreach (var scope in _activeScopes.Take(Math.Max(0, _activeScopes.Count - targetDepth)))
				block.Instructions.Add(new EndScopeInstruction(scope.Id, scope.Location.End));
		}
		
		public void Visit(ResolvedBlockStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation);
			foreach (var statement in node.Statements)
				VisitNode(statement);
			
			EndCurrentScope();
		}
		
		public void Visit(ResolvedBreakStatementNode node)
		{
			var context = GetLoopContext(node.Label);
			EmitScopeEndsToDepth(context.ScopeDepth);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(context.BreakTarget, node.Syntax.SourceLocation));
			currentBlock = null;
		}
		
		public void Visit(ResolvedContinueStatementNode node)
		{
			var context = GetLoopContext(node.Label);
			EmitScopeEndsToDepth(context.ScopeDepth);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(context.ContinueTarget, node.Syntax.SourceLocation));
			currentBlock = null;
		}
		
		public void Visit(ResolvedExpressionStatementNode node)
		{
			var expression = VisitNode(node.Expression);
			GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(expression));
		}
		
		public void Visit(ResolvedIfStatementNode node)
		{
			// Condition & terminate block
			var condition = VisitNode(node.Condition);
			
			var thenBlock = CreateBlock("then");
			var elseBlock = node.Else is null ? null : CreateBlock("else");
			var mergeBlock = CreateBlock("merge");
			
			GetOrMakeBlock().SetTerminator(new ConditionalBranchTerminator(condition, thenBlock, elseBlock ?? mergeBlock,
				node.Condition.Syntax.SourceLocation));
			
			// Then block
			currentBlock = thenBlock;
			VisitNode(node.Then);
			currentBlock?.FillTerminator(new BranchTerminator(mergeBlock, node.Syntax.SourceLocation));
			
			// Else block
			if (node.Else is { } @else)
			{
				currentBlock = elseBlock!;
				VisitNode(@else);
				currentBlock?.FillTerminator(new BranchTerminator(mergeBlock, node.Syntax.SourceLocation));
			}
			
			// Finish
			ContinueWith(mergeBlock);
		}
		
		public void Visit(ResolvedInvalidStatementNode node) =>
			throw new InvalidOperationException();
		
		public void Visit(ResolvedReturnStatementNode node)
		{
			var value = node.Expression is null ? null : VisitNode(node.Expression);
			
			var block = GetOrMakeBlock();
			if (value is not null)
			{
				var returnSymbol = CreateTempSymbol(value.Type, "return");
				block.Instructions.Add(new LocalVarInstruction(returnSymbol, value,
					node.Syntax.SourceLocation, scopeId: 0));
				
				value = new VariableValue(new(returnSymbol, value.Type), node.Syntax.SourceLocation);
			}
			
			EmitScopeEndsToDepth(0);
			block.SetTerminator(ReturnTerminator.FromValue(value, node.Syntax.SourceLocation));
			currentBlock = null;
		}
		
		public void Visit(ResolvedVarStatementNode node)
		{
			var value = node.Initializer is null ? new ZeroValue(node.Symbol.Type) : VisitNode(node.Initializer);
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(node.Symbol, value, node.Syntax.SourceLocation,
				CurrentScopeId));
		}
		
		private void VisitInLoop(IResolvedStatementNode body, BasicBlock breakBlock, BasicBlock continueBlock,
			LabelSymbol? label)
		{
			var loopContext = new LoopContext(breakBlock, continueBlock, _activeScopes.Count);
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
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label);
			currentBlock?.FillTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			// Condition
			currentBlock = condBlock;
			var condition = VisitNode(node.Condition);
			currentBlock?.FillTerminator(new ConditionalBranchTerminator(condition, bodyBlock, exitBlock,
					node.Condition.Syntax.SourceLocation));
			
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
			VisitInLoop(node.Body, exitBlock, bodyBlock, node.Label);
			currentBlock?.FillTerminator(new BranchTerminator(bodyBlock, node.Syntax.SourceLocation));
			
			ContinueWith(exitBlock);
		}
		
		public void Visit(ResolvedRepeatStatementNode node)
		{
			var id = NextLoopId();
			
			// Create implicit counter variable initialized with count
			var countValue = VisitNode(node.Count);
			var counterSymbol = CreateTempSymbol(countValue.Type, $"repeat{id}$i");
			var counterLocation = node.Count.Syntax.SourceLocation;
			var block = GetOrMakeBlock();
			block.Instructions.Add(new LocalVarInstruction(counterSymbol, countValue, counterLocation,
				CurrentScopeId));
			
			var counterVar = new VariableValue(new(counterSymbol, countValue.Type), counterLocation);
			var one = MakeConstant(countValue.Type, BigInteger.One);
			var zero = MakeConstant(countValue.Type, BigInteger.Zero);
			
			var condBlock = CreateBlock($"repeat{id}_cond");
			var bodyBlock = CreateBlock($"repeat{id}_body");
			var latchBlock = CreateBlock($"repeat{id}_latch");
			var exitBlock = CreateBlock($"repeat{id}_exit");
			
			block.SetTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			// Condition: counter > 0
			currentBlock = condBlock;
			var condition = new BinOpValue(countValue.Type, counterVar, zero, BinaryOperation.Greater, counterLocation);
			currentBlock.SetTerminator(new ConditionalBranchTerminator(condition, bodyBlock, exitBlock,
				node.Syntax.SourceLocation));
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, latchBlock, node.Label);
			currentBlock?.FillTerminator(new BranchTerminator(latchBlock, node.Syntax.SourceLocation));
			
			// Latch: decrement counter, jump back to condition
			if (latchBlock.HasPredecessor())
			{
				currentBlock = latchBlock;
				var decrement = new AssignValue(countValue.Type, counterVar,
					new BinOpValue(countValue.Type, counterVar, one, BinaryOperation.Subtraction, counterLocation),
					counterLocation);
				
				latchBlock.Instructions.Add(new ExpressionInstruction(decrement));
				latchBlock.SetTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			}
			else
			{
				_function.Blocks.Remove(latchBlock);
			}
			
			ContinueWith(exitBlock);
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
			
			GetOrMakeBlock().SetTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			// Condition
			currentBlock = condBlock;
			var condition = VisitNode(node.Condition);
			currentBlock?.FillTerminator(new ConditionalBranchTerminator(condition, bodyBlock, exitBlock,
				node.Condition.Syntax.SourceLocation));
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label);
			currentBlock?.FillTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			ContinueWith(exitBlock);
		}
		
		public Value Visit(ResolvedConversionExpressionNode node) =>
			new ConversionValue(VisitNode(node.Source), node.Conversion, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedFunctionCallExpressionNode node) =>
			new CallValue(node.Function, node.Arguments.Select(VisitNode), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedHeapExpressionNode node) =>
			new HeapValue(node.Type, node.Initializer is { } initializer
				? VisitNode(initializer)
				: new ZeroValue(((PointerType)node.Type).BaseType), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedIndexerExpressionNode node) =>
			new IndexerValue(node.Type, VisitNode(node.Target), VisitNode(node.Index), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedInvalidExpressionNode node) =>
			throw new InvalidOperationException();
		
		public Value Visit(ResolvedAccessExpressionNode node) =>
			new AccessValue(node.Type, VisitNode(node.Target), node.Member, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedLiteralExpressionNode node) =>
			MakeConstant(node.Type, node.Value);
		
		public Value Visit(ResolvedArrayExpressionNode node) =>
			new ArrayValue((ArrayType)node.Type, node.Values.Select(VisitNode), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedUnaryOpExpressionNode node) =>
			LowerUnaryOp(VisitNode(node.Operand), node.Operation);
		
		public Value Visit(ResolvedUndefExpressionNode node) =>
			new UndefValue(node.Type);
		
		public Value Visit(ResolvedVarExpressionNode node) =>
			new VariableValue(new(node.Symbol, node.Type), node.Syntax.SourceLocation);
		
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
			var sourceLocation = Join(leftNode.Syntax.SourceLocation, rightNode.Syntax.SourceLocation);
			
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "sc_result");
			var block = GetOrMakeBlock();
			block.Instructions.Add(new LocalVarInstruction(resultSymbol, new UndefValue(NativeSymbols.Bool),
				leftNode.Syntax.SourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), sourceLocation);
			
			var rightBlock = CreateBlock("sc_right");
			var mergeBlock = CreateBlock("sc_merge");
			
			var left = VisitNode(leftNode);
			
			block = GetOrMakeBlock();
			block.Instructions.Add(
				new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, left, sourceLocation)));
			
			block.SetTerminator(op == TokenType.OpAmpersandAmpersand
				? new ConditionalBranchTerminator(result, rightBlock, mergeBlock, leftNode.Syntax.SourceLocation)
				: new ConditionalBranchTerminator(result, mergeBlock, rightBlock, leftNode.Syntax.SourceLocation));
			
			currentBlock = rightBlock;
			var right = VisitNode(rightNode);
			
			if (currentBlock is not null)
			{
				currentBlock.Instructions.Add(
					new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result, right, sourceLocation)));
				
				currentBlock.FillTerminator(new BranchTerminator(mergeBlock, rightNode.Syntax.SourceLocation));
			}
			
			ContinueWith(mergeBlock);
			return result;
		}
		
		private LocalVariableSymbol CreateTempSymbol(TypeSymbol type, string name)
		{
			name = $".t{NextTempId()}__{name}";
			var node = new VarStatementNode(SourceLocation.None, new(TokenType.Identifier, SourceLocation.None, name),
				null, null);
			
			return new LocalVariableSymbol(node, type);
		}
		
		public Value Visit(ResolvedAssignmentExpressionNode node)
		{
			var left = VisitNode(node.Left);
			
			// Need to stabilize the left side first so compound assignments don't double-evaluate
			if (node.Op.Type != TokenType.OpEqual)
				left = StabilizeStorage(left);
			
			var right = VisitNode(node.Right);
			return LowerAssignment(left, node.Op, right, node.Type);
		}
		
		public Value Visit(ResolvedChainedExpressionNode node)
		{
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "chain_result");
			var block = GetOrMakeBlock();
			block.Instructions.Add(new LocalVarInstruction(resultSymbol, new UndefValue(NativeSymbols.Bool),
				node.Syntax.SourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), node.Syntax.SourceLocation);
			
			var mergeBlock = CreateBlock("chain_merge");
			var left = VisitNode(node.Operands[0]);
			GetOrMakeBlock();
			
			for (var i = 0; i < node.Ops.Length; i++)
			{
				var op = node.Ops[i];
				var rightNode = node.Operands[i + 1];
				var isLast = i == node.Ops.Length - 1;
				
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
					block.Instructions.Add(new LocalVarInstruction(tempSymbol, rightValue,
						rightNode.Syntax.SourceLocation, CurrentScopeId));
					
					right = new VariableValue(new(tempSymbol, rightNode.Type), rightNode.Syntax.SourceLocation);
				}
				
				var comparison = LowerBinOp(left, op, right);
				
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
		
		private static AssignValue LowerAssignment(Value left, Token op, Value right, TypeSymbol type) => op.Type switch
		{
			TokenType.OpEqual => new AssignValue(type, left, right, op.SourceLocation),
			TokenType.OpPlusEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.Addition, op.SourceLocation), op.SourceLocation),
			TokenType.OpMinusEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.Subtraction, op.SourceLocation), op.SourceLocation),
			TokenType.OpStarEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.Multiplication, op.SourceLocation), op.SourceLocation),
			TokenType.OpSlashEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.Division, op.SourceLocation), op.SourceLocation),
			TokenType.OpPercentEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.Modulo, op.SourceLocation), op.SourceLocation),
			TokenType.OpAmpersandEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.BitwiseAnd, op.SourceLocation), op.SourceLocation),
			TokenType.OpBarEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.BitwiseOr, op.SourceLocation), op.SourceLocation),
			TokenType.OpHatEqual => new AssignValue(type, left, new BinOpValue(type, left, right,
				BinaryOperation.BitwiseXor, op.SourceLocation), op.SourceLocation),
			_ => throw new InvalidOperationException()
		};
		
		private static Value LowerBinOp(Value left, OperationImpl? op, Value right) => op switch
		{
			NativeImpl native => new BinOpValue(native.ReturnType, left, right, MapBinOp(native.Op),
				Join(left.SourceLocation, right.SourceLocation)),
			FunctionImpl function => new CallValue(function.Function, [left, right],
				Join(left.SourceLocation, right.SourceLocation)),
			ConversionImpl conversion => LowerConversionBinOp(left, conversion, right),
			_ => throw new InvalidOperationException()
		};
		
		private static Value LowerConversionBinOp(Value left, ConversionImpl conversion, Value right)
		{
			if (conversion.ParameterConversions[0] is { } leftConversion)
				left = Convert(left, leftConversion);
			
			if (conversion.ParameterConversions[1] is { } rightConversion)
				right = Convert(right, rightConversion);
			
			var intermediateType = conversion.ResultConversion?.From ?? conversion.ReturnType;
			var intermediateResult = new BinOpValue(intermediateType, left, right,
				MapBinOp(conversion.Op), Join(left.SourceLocation, right.SourceLocation));
			
			return conversion.ResultConversion is { } resultConversion
				? Convert(intermediateResult, resultConversion)
				: intermediateResult;
		}
		
		private static Value LowerUnaryOp(Value operand, OperationImpl? op) => op switch
		{
			NativeImpl native => new UnaryOpValue(native.ReturnType, operand, MapUnaryOp(native.Op),
				operand.SourceLocation),
			FunctionImpl function => new CallValue(function.Function, [operand], operand.SourceLocation),
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
			TokenType.OpStar => UnaryOperation.Dereference,
			_ => throw new InvalidOperationException()
		};
		
		private static SourceLocation Join(SourceLocation left, SourceLocation right)
		{
			var (source, range) = left;
			range = range.Join(right.Range);
			return new(source, range);
		}
		
		private static Value Convert(Value value, Conversion conversion)
		{
			if (value.Type == conversion.To)
				return value;
			
			if (value is ConversionValue inner && CanCancelConversions(inner.Conversion, conversion))
				return inner.Source;
			
			return new ConversionValue(value, conversion, value.SourceLocation);
		}
		
		private static bool CanCancelConversions(Conversion inner, Conversion outer)
		{
			if (inner.From != outer.To || inner.To != outer.From)
				return false;
			
			return IsReinterpret(inner) && IsReinterpret(outer);
		}
		
		private static bool IsReinterpret(Conversion conversion)
		{
			// TODO Maybe add a special reinterpret conversion type?
			
			if (conversion.From == NativeSymbols.UIntSize && conversion.To is PointerType)
				return true;
			
			if (conversion.From is PointerType && conversion.To == NativeSymbols.UIntSize)
				return true;
			
			return false;
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
			VariableValue => value,
			IndexerValue or AccessValue or UnaryOpValue { Op: UnaryOperation.Dereference } => StabilizeStorage(value),
			_ => CaptureAsAtomic(value, "target")
		};
		
		private Value CaptureAsAtomic(Value value, string hint)
		{
			if (value is ConstantValue or ZeroValue or UndefValue or VariableValue)
				return value;
			
			var symbol = CreateTempSymbol(value.Type, hint);
			
			GetOrMakeBlock().Instructions
				.Add(new LocalVarInstruction(symbol, value, value.SourceLocation, CurrentScopeId));
			
			return new VariableValue(new(symbol, value.Type), value.SourceLocation);
		}
	}
}