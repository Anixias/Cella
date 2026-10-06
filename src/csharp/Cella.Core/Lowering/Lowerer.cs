using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;
using static Cella.Core.Binding.Operations.OperationMapping;

namespace Cella.Core.Lowering;

public sealed class Lowerer
(
	ConstantEvaluator evaluator,
	TypePool typePool,
	Func<GlobalSymbol, GlobalInfo> getGlobalInfo
) : IResolvedDeclarationNodeVisitor
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
	
	public void Visit(ResolvedFunctionNode node) =>
		currentFile?.Functions.Add(FunctionLowerer.Lower(node, evaluator, typePool, getGlobalInfo));
	
	public void Visit(ResolvedInvalidDeclarationNode node) => throw new InvalidOperationException();
	
	public void Visit(ResolvedRecordNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentFile?.Types.Add(node.Symbol);
	}
	
	public void Visit(ResolvedEnumNode node) => currentFile?.Types.Add(node.Symbol);
	
	public void Visit(ResolvedFieldNode node)
	{
	}
	
	public void Visit(ResolvedMethodNode node)
	{
		// TODO Handle self-reference?
		VisitNode(node.FunctionNode);
	}
	
	public void Visit(ResolvedExternalFunctionNode node) => currentFile?.ExternalFunctions.Add(node.FunctionInfo);
	
	public void Visit(ResolvedGlobalNode node) => currentFile?.Globals.Add(node.Info);
	
	private sealed class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value>
	{
		private readonly record struct LoopContext(BasicBlock BreakTarget, BasicBlock ContinueTarget, int ScopeDepth);
		
		private readonly LoweredFunction _function;
		private readonly ConstantEvaluator _evaluator;
		private readonly TypePool _typePool;
		private readonly Func<GlobalSymbol, GlobalInfo> _getGlobalInfo;
		private readonly Stack<LoopContext> _loopStack = [];
		private readonly Dictionary<LabelSymbol, LoopContext> _loopsByLabel = [];
		private readonly Stack<ActiveScope> _activeScopes = [];
		private readonly HashSet<int> _temporaryScopes = [];
		private readonly Dictionary<int, List<VariableInfo>> _scopeDrops = [];
		private readonly HashSet<LocalVariableSymbol> _temporaries = [];
		private BasicBlock? currentBlock;
		private ulong nextLoopId;
		private ulong nextTempId;
		private int nextScopeId;
		private int CurrentScopeId => _activeScopes.TryPeek(out var scope) ? scope.Id : 0;
		private int BlockScopeId => _activeScopes.FirstOrDefault(static scope => !scope.IsTemporary).Id;
		
		private readonly record struct ActiveScope(int Id, SourceLocation Location, bool IsTemporary);
		
		private FunctionLowerer(LoweredFunction function, ConstantEvaluator evaluator, TypePool typePool,
			Func<GlobalSymbol, GlobalInfo> getGlobalInfo)
		{
			_function = function;
			_evaluator = evaluator;
			_typePool = typePool;
			_getGlobalInfo = getGlobalInfo;
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
		
		public static LoweredFunction Lower(ResolvedFunctionNode node, ConstantEvaluator evaluator, TypePool typePool,
			Func<GlobalSymbol, GlobalInfo> getGlobalInfo)
		{
			var function = new LoweredFunction(node.FunctionInfo);
			var lower = new FunctionLowerer(function, evaluator, typePool, getGlobalInfo);
			
			switch (node.Body)
			{
				case IResolvedStatementNode statement:
					lower.LowerBody(node, statement);
					lower.PruneEmptyScopes();
					break;
				
				// If expression body, synthesize a return statement
				case IResolvedExpressionNode expression:
					// If void return, we do not return the expression itself
					if (node.FunctionInfo.Signature.ReturnType == NativeSymbols.Void)
						lower.VisitNode(expression);
					else
						lower.LowerReturn(expression, expression.Syntax.SourceLocation);
					
					break;
			}
			
			return function;
		}
		
		private void LowerBody(ResolvedFunctionNode node, IResolvedStatementNode body)
		{
			var signature = node.FunctionInfo.Signature;
			var parameters = node.FunctionInfo.Symbol.Parameters
				.Select((p, i) => new VariableInfo(p, signature.ParameterTypes[i]))
				.Where((p, i) => signature.GetMode(i) == ParameterMode.Own && _typePool.NeedsDrop(p.Type))
				.ToArray();
			
			if (parameters.Length == 0)
			{
				VisitNode(body);
				return;
			}
			
			var scope = BeginScope(node.Syntax.SourceLocation);
			_scopeDrops[scope.Id].AddRange(parameters);
			VisitNode(body);
			EndCurrentScope();
		}
		
		private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
		
		private Value VisitNode(IResolvedExpressionNode node) =>
			LowerConstant(_evaluator.Evaluate(node), node.Syntax.SourceLocation) ?? VisitPlace(node);
		
		private Value VisitPlace(IResolvedExpressionNode node) =>
			((IResolvedExpressionNodeVisitor<Value>)this).Visit(node);
		
		private Value? LowerConstant(Constant? constant, SourceLocation location) => constant switch
		{
			IntegerConstant { Type: IntegerType } c => MakeConstant(c.Type, c.Value),
			FloatConstant { Type: FloatType } c => new ConstantValue(c.Type, c.Value),
			BoolConstant c => c.Value ? ConstantValue.True : ConstantValue.False,
			NullConstant { Type: not UntypedType } c => new ConstantValue(c.Type, null),
			StringConstant { Type: StringType } c => new ConstantValue(c.Type, c.Value),
			FunctionConstant c => new FunctionReferenceValue(c.Function, c.Type, location),
			_ => null
		};
		
		private ActiveScope BeginScope(SourceLocation location, bool isTemporary = false)
		{
			var scope = new ActiveScope(++nextScopeId, location, isTemporary);
			GetOrMakeBlock().Instructions.Add(new BeginScopeInstruction(scope.Id, location));
			_activeScopes.Push(scope);
			_scopeDrops[scope.Id] = [];
			if (isTemporary)
				_temporaryScopes.Add(scope.Id);
			
			return scope;
		}
		
		private void ExitScope(BasicBlock block, ActiveScope scope, BasicBlock target)
		{
			EndScope(block, scope);
			block.SetTerminator(new BranchTerminator(target, scope.Location));
		}
		
		private void EndScope(BasicBlock block, ActiveScope scope)
		{
			var location = scope.Location.End;
			foreach (var variable in Enumerable.Reverse(_scopeDrops[scope.Id]))
				block.Instructions.Add(new DropInstruction(new VariableValue(variable, location), location));
			
			block.Instructions.Add(new EndScopeInstruction(scope.Id, location));
		}
		
		private void Declare(BasicBlock block, LocalVarInstruction declaration)
		{
			block.Instructions.Add(declaration);
			var symbol = declaration.Symbol;
			if (_typePool.NeedsDrop(symbol.Type) && _scopeDrops.TryGetValue(declaration.ScopeId, out var drops))
				drops.Add(new(symbol, symbol.Type));
		}
		
		private void PruneEmptyScopes()
		{
			var used = _function.Blocks
				.SelectMany(static block => block.Instructions)
				.OfType<LocalVarInstruction>()
				.Select(static instruction => instruction.ScopeId)
				.ToHashSet();
			
			foreach (var block in _function.Blocks)
				block.Instructions.RemoveAll(instruction => instruction switch
				{
					BeginScopeInstruction i => IsEmptyTemporary(i.ScopeId),
					EndScopeInstruction i => IsEmptyTemporary(i.ScopeId),
					_ => false
				});
			
			return;
			
			bool IsEmptyTemporary(int scopeId) => _temporaryScopes.Contains(scopeId) && !used.Contains(scopeId);
		}
		
		private void EndCurrentScope()
		{
			var scope = _activeScopes.Pop();
			
			if (currentBlock is { Terminator: UndefinedTerminator } block)
				EndScope(block, scope);
		}
		
		private void EmitScopeEndsToDepth(int targetDepth)
		{
			var block = GetOrMakeBlock();
			foreach (var scope in _activeScopes.Take(Math.Max(0, _activeScopes.Count - targetDepth)))
				EndScope(block, scope);
		}
		
		public void Visit(ResolvedBlockStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation);
			foreach (var statement in node.Statements)
				VisitNode(statement);
			
			EndCurrentScope();
		}
		
		public Value Visit(ResolvedBreakExpressionNode node)
		{
			var context = GetLoopContext(node.Label);
			EmitScopeEndsToDepth(context.ScopeDepth);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(context.BreakTarget, node.Syntax.SourceLocation));
			currentBlock = null;
			return new UndefValue(node.Type);
		}
		
		public Value Visit(ResolvedContinueExpressionNode node)
		{
			var context = GetLoopContext(node.Label);
			EmitScopeEndsToDepth(context.ScopeDepth);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(context.ContinueTarget, node.Syntax.SourceLocation));
			currentBlock = null;
			return new UndefValue(node.Type);
		}
		
		public void Visit(ResolvedExpressionStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation, true);
			var expression = node.Expression is ResolvedAssignmentExpressionNode assignment
				? LowerAssignment(assignment, false)
				: VisitNode(node.Expression);
			
			if (currentBlock is not null && !IsPlaceValue(expression))
			{
				if (expression is CallValue or IndirectCallValue && _typePool.NeedsDrop(expression.Type))
					StoreTemporary(expression, "discard");
				else
					currentBlock.Instructions.Add(new ExpressionInstruction(expression));
			}
			
			EndCurrentScope();
		}
		
		public void Visit(ResolvedIfStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation, true);
			var thenBlock = CreateBlock("then");
			var elseBlock = node.Else is null ? null : CreateBlock("else");
			var mergeBlock = CreateBlock("merge");
			
			LowerBranch(node.Condition, thenBlock, elseBlock ?? mergeBlock);
			
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
			EndCurrentScope();
		}
		
		public void Visit(ResolvedInvalidStatementNode node) =>
			throw new InvalidOperationException();
		
		public void Visit(ResolvedMatchStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation, true);
			LowerMatch(node.Value, [..node.Arms.Select(static arm => (arm.Pattern, arm.Body.Syntax.SourceLocation))],
				node.Syntax.SourceLocation, node.OwnsValue, index => VisitNode(node.Arms[index].Body));
			
			EndCurrentScope();
		}
		
		public Value Visit(ResolvedMatchExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			VariableValue? result = null;
			if (node.Type is not NeverType)
			{
				var symbol = CreateTempSymbol(node.Type, "match_result");
				Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, new UndefValue(node.Type), location,
					CurrentScopeId));
				
				result = new VariableValue(new(symbol, node.Type), location);
			}
			
			LowerMatch(node.Value, [..node.Arms.Select(static arm => (arm.Pattern, arm.Value.Syntax.SourceLocation))],
				location, node.OwnsValue, index =>
				{
					var arm = node.Arms[index].Value;
					var value = Consume(VisitNode(arm));
					if (result is not null)
						currentBlock?.Instructions.Add(new ExpressionInstruction(
							new AssignValue(node.Type, result, value, arm.Syntax.SourceLocation)));
				});
			
			return result ?? (Value)new UndefValue(node.Type);
		}
		
		private void LowerMatch(IResolvedExpressionNode value,
			IReadOnlyList<(ResolvedPattern? Pattern, SourceLocation Location)> arms, SourceLocation location,
			bool ownsValue, Action<int> lowerArm)
		{
			var scrutinee = LowerScrutinee(value);
			var enumType = (EnumSymbol)scrutinee.Type;
			var tagType = _typePool.GetTagType(enumType);
			var tag = CaptureAsAtomic(new EnumTagValue(tagType, scrutinee, location), "tag");
			var mergeBlock = CreateBlock("match_end");
			var caseValues = enumType.Cases.Select(c => _typePool.GetCaseValue(enumType, c)).ToHashSet();
			var coversEveryCase = !enumType.IsExternal && caseValues.SetEquals(arms
				.Where(static arm => arm.Pattern is not null)
				.Select(arm => _typePool.GetCaseValue(enumType, arm.Pattern!.Case)));
			
			var testBlock = GetOrMakeBlock();
			for (var i = 0; i < arms.Count; i++)
			{
				var (pattern, armLocation) = arms[i];
				var isLast = i == arms.Count - 1;
				var armBlock = CreateBlock("match_arm");
				BasicBlock? nextBlock = null;
				
				if (pattern is null || coversEveryCase && isLast)
					testBlock.SetTerminator(new BranchTerminator(armBlock, location));
				else
				{
					nextBlock = isLast ? mergeBlock : CreateBlock("match_next");
					var caseTag = MakeConstant(tagType, _typePool.GetCaseValue(enumType, pattern.Case));
					var test = new BinOpValue(NativeSymbols.Bool, tag, caseTag, BinaryOperation.Equal, location);
					testBlock.SetTerminator(new ConditionalBranchTerminator(test, armBlock, nextBlock, location));
				}
				
				currentBlock = armBlock;
				BeginScope(armLocation);
				if (pattern is not null)
					BindPayload(scrutinee, pattern, ownsValue);
				else if (ownsValue)
					StoreTemporary(Consume(scrutinee), "unbound");
				
				lowerArm(i);
				EndCurrentScope();
				currentBlock?.FillTerminator(new BranchTerminator(mergeBlock, location));
				
				if (nextBlock is null)
					break;
				
				testBlock = nextBlock;
			}
			
			if (arms.Count == 0)
				testBlock.SetTerminator(new BranchTerminator(mergeBlock, location));
			
			ContinueWith(mergeBlock);
		}
		
		private void LowerBranch(IResolvedExpressionNode condition, BasicBlock trueBlock, BasicBlock falseBlock)
		{
			var location = condition.Syntax.SourceLocation;
			if (_evaluator.Evaluate(condition) is null)
			{
				switch (condition)
				{
					case ResolvedBinaryOpExpressionNode
					{
						Operation: NativeImpl { Op: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar } operation
					} node:
					{
						var rightBlock = CreateBlock("cond_right");
						if (operation.Op == TokenType.OpAmpersandAmpersand)
							LowerBranch(node.Left, rightBlock, falseBlock);
						else
							LowerBranch(node.Left, trueBlock, rightBlock);
						
						currentBlock = rightBlock;
						LowerBranch(node.Right, trueBlock, falseBlock);
						return;
					}
					
					case ResolvedUnaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpBang } } node:
						LowerBranch(node.Operand, falseBlock, trueBlock);
						return;
					
					case ResolvedIsExpressionNode { Pattern.HasBindings: true } node:
					{
						var scrutinee = LowerScrutinee(node.Value);
						var bindBlock = CreateBlock("is_bind");
						GetOrMakeBlock().SetTerminator(new ConditionalBranchTerminator(
							TestCase(scrutinee, node.Pattern.Case), bindBlock, falseBlock, location));
						
						currentBlock = bindBlock;
						BindPayload(scrutinee, node.Pattern, node.OwnsValue);
						GetOrMakeBlock().SetTerminator(new BranchTerminator(trueBlock, location));
						currentBlock = null;
						return;
					}
				}
			}
			
			GetOrMakeBlock();
			var value = VisitNode(condition);
			currentBlock?.SetTerminator(new ConditionalBranchTerminator(value, trueBlock, falseBlock, location));
			currentBlock = null;
		}
		
		private Value LowerScrutinee(IResolvedExpressionNode node)
		{
			var value = VisitNode(node);
			return value is VariableValue or GlobalValue or AccessValue or IndexerValue
				or UnaryOpValue { Op: UnaryOperation.Dereference }
				? StabilizeStorage(value)
				: CaptureAsAtomic(value, "scrutinee");
		}
		
		private BinOpValue TestCase(Value scrutinee, EnumCaseSymbol enumCase)
		{
			var location = scrutinee.SourceLocation;
			var enumType = (EnumSymbol)scrutinee.Type;
			var tagType = _typePool.GetTagType(enumType);
			var tag = new EnumTagValue(tagType, scrutinee, location);
			var caseTag = MakeConstant(tagType, _typePool.GetCaseValue(enumType, enumCase));
			return new BinOpValue(NativeSymbols.Bool, tag, caseTag, BinaryOperation.Equal, location);
		}
		
		private void BindPayload(Value scrutinee, ResolvedPattern pattern, bool ownsValue)
		{
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)scrutinee.Type, pattern.Case);
			for (var i = 0; i < pattern.Bindings.Length; i++)
			{
				var binding = pattern.Bindings[i];
				var location = binding?.Identifier.SourceLocation ?? scrutinee.SourceLocation;
				var payload = new EnumPayloadValue(payloadTypes[i], scrutinee, pattern.Case, i, location);
				var isMoved = ownsValue && _typePool.NeedsDrop(payloadTypes[i]);
				switch (binding)
				{
					case { IsBorrowBinding: true, Type: PointerType pointer }:
						GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding,
							new UnaryOpValue(pointer, payload, UnaryOperation.AddressOf, location), location,
							CurrentScopeId));
						
						break;
					
					case not null when isMoved:
						Declare(GetOrMakeBlock(),
							new LocalVarInstruction(binding, new MoveValue(payload), location, CurrentScopeId));
						
						break;
					
					case not null:
						GetOrMakeBlock().Instructions.Add(
							new LocalVarInstruction(binding, payload, location, CurrentScopeId));
						
						break;
					
					case null when isMoved:
						StoreTemporary(new MoveValue(payload), "unbound");
						break;
				}
			}
			
			if (ownsValue)
				GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(Consume(scrutinee)));
		}
		
		public Value Visit(ResolvedReturnExpressionNode node)
		{
			LowerReturn(node.Value, node.Syntax.SourceLocation);
			return new UndefValue(node.Type);
		}
		
		private void LowerReturn(IResolvedExpressionNode? expression, SourceLocation location)
		{
			GetOrMakeBlock();
			var value = expression is null ? null : Consume(VisitNode(expression));
			if (currentBlock is not { } block)
				return;
			
			if (value is not null)
			{
				var returnSymbol = CreateTempSymbol(value.Type, "return");
				block.Instructions.Add(new LocalVarInstruction(returnSymbol, value, location, scopeId: 0));
				value = Consume(new VariableValue(new(returnSymbol, value.Type), location));
			}
			
			EmitScopeEndsToDepth(0);
			block.SetTerminator(ReturnTerminator.FromValue(value, location));
			currentBlock = null;
		}
		
		public void Visit(ResolvedVarStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation, true);
			var value = node switch
			{
				{ Initializer: { } initializer } => Consume(VisitNode(initializer)),
				{ Symbol.IsDeferred: true } => new UndefValue(node.Symbol.Type),
				_ => new DefaultValue(node.Symbol.Type)
			};
			
			if (currentBlock is { } block)
				Declare(block, new LocalVarInstruction(node.Symbol, value, node.Syntax.SourceLocation, BlockScopeId));
			
			EndCurrentScope();
		}
		
		private void VisitInLoop(IResolvedStatementNode body, BasicBlock breakBlock, BasicBlock continueBlock,
			LabelSymbol? label, int scopeDepth)
		{
			var loopContext = new LoopContext(breakBlock, continueBlock, scopeDepth);
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
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label, _activeScopes.Count);
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
			VisitInLoop(node.Body, exitBlock, bodyBlock, node.Label, _activeScopes.Count);
			currentBlock?.FillTerminator(new BranchTerminator(bodyBlock, node.Syntax.SourceLocation));
			
			ContinueWith(exitBlock);
		}
		
		public void Visit(ResolvedRepeatStatementNode node)
		{
			var id = NextLoopId();
			
			// Create implicit counter variable initialized with count
			BeginScope(node.Count.Syntax.SourceLocation, true);
			var countValue = VisitNode(node.Count);
			var counterSymbol = CreateTempSymbol(countValue.Type, $"repeat{id}$i");
			var counterLocation = node.Count.Syntax.SourceLocation;
			var block = GetOrMakeBlock();
			block.Instructions.Add(new LocalVarInstruction(counterSymbol, countValue, counterLocation, BlockScopeId));
			EndCurrentScope();
			
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
			VisitInLoop(node.Body, exitBlock, latchBlock, node.Label, _activeScopes.Count);
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
				PrimitiveTypeKind.Char => new(type, (uint)v),
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
			var scopeDepth = _activeScopes.Count;
			var scope = BeginScope(node.Condition.Syntax.SourceLocation, true);
			var doneBlock = CreateBlock($"while{id}_done");
			LowerBranch(node.Condition, bodyBlock, doneBlock);
			ExitScope(doneBlock, scope, exitBlock);
			
			// Body
			currentBlock = bodyBlock;
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label, scopeDepth);
			EndCurrentScope();
			currentBlock?.FillTerminator(new BranchTerminator(condBlock, node.Syntax.SourceLocation));
			
			ContinueWith(exitBlock);
		}
		
		public Value Visit(ResolvedConstructorCallExpressionNode node)
		{
			var sourceLocation = node.Syntax.SourceLocation;
			
			var resultSymbol = CreateTempSymbol(node.Type, ".cons__mem");
			Declare(GetOrMakeBlock(),
				new LocalVarInstruction(resultSymbol, new ZeroValue(node.Type), sourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, node.Type), sourceLocation);
			
			var selfType = node.Function.Signature.ParameterTypes[0];
			var self = new UnaryOpValue(selfType, result, UnaryOperation.AddressOf, sourceLocation);
			
			LowerConstructor(node.Function, self, node.Arguments, sourceLocation);
			
			return result;
		}
		
		public Value Visit(ResolvedRecordExpressionNode node)
		{
			if (node.Fields.IsEmpty)
				return new ZeroValue(node.Type);
			
			var sourceLocation = node.Syntax.SourceLocation;
			var resultSymbol = CreateTempSymbol(node.Type, ".record");
			Declare(GetOrMakeBlock(),
				new LocalVarInstruction(resultSymbol, new ZeroValue(node.Type), sourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, node.Type), sourceLocation);
			foreach (var (field, value) in node.Fields)
			{
				var fieldValue = Consume(VisitNode(value));
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
			var scrutinee = LowerScrutinee(node.Value);
			var test = TestCase(scrutinee, node.Pattern.Case);
			if (!node.Pattern.HasBindings)
				return test;
			
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "is_result");
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(resultSymbol, test, location, CurrentScopeId));
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), location);
			
			var bindBlock = CreateBlock("is_bind");
			var mergeBlock = CreateBlock("is_merge");
			GetOrMakeBlock().SetTerminator(new ConditionalBranchTerminator(result, bindBlock, mergeBlock, location));
			
			currentBlock = bindBlock;
			BindPayload(scrutinee, node.Pattern, node.OwnsValue);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(mergeBlock, location));
			
			ContinueWith(mergeBlock);
			return result;
		}
		
		public Value Visit(ResolvedConversionExpressionNode node) =>
			new ConversionValue(VisitNode(node.Source), node.Conversion, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedFunctionCallExpressionNode node) => new CallValue(node.Function,
			LowerArguments(node.Arguments, node.Function, 0), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedFunctionGroupExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedFunctionReferenceExpressionNode node) =>
			new FunctionReferenceValue(node.Function, node.Type, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedIndirectCallExpressionNode node)
		{
			var operands = LowerOperands([node.Target, ..node.Arguments], GetOperandPassing(node.FunctionType));
			return new IndirectCallValue(operands[0], operands.Skip(1), node.FunctionType, node.Syntax.SourceLocation);
		}
		
		private Func<int, Passing> GetOperandPassing(FunctionType type) => i => i == 0
			? Passing.Read
			: GetPassing(type.ParameterTypes[i - 1], type.ParameterModes[i - 1]);
		
		private void LowerConstructor(FunctionInfo constructor, Value self,
			IReadOnlyList<IResolvedExpressionNode> arguments, SourceLocation sourceLocation)
		{
			var args = new List<Value> { self };
			args.AddRange(LowerArguments(arguments, constructor, 1));
			
			GetOrMakeBlock().Instructions
				.Add(new ExpressionInstruction(new CallValue(constructor, args, sourceLocation)));
		}
		
		public Value Visit(ResolvedIndexerExpressionNode node)
		{
			var target = MaterializeTemporary(VisitNode(node.Target));
			if (MayEmit(node.Index))
				target = node.Target.Type is ArrayType
					? StabilizeStorageBase(target)
					: CaptureAsAtomic(target, "target");
			
			return new IndexerValue(node.Type, target, VisitNode(node.Index), node.Syntax.SourceLocation);
		}
		
		public Value Visit(ResolvedInvalidExpressionNode node) =>
			throw new InvalidOperationException();
		
		public Value Visit(ResolvedAccessExpressionNode node) => new AccessValue(node.Type,
			MaterializeTemporary(VisitNode(node.Target)), node.Member, node.Syntax.SourceLocation);
		
		private Value MaterializeTemporary(Value value) =>
			!IsPlaceValue(value) && _typePool.NeedsDrop(value.Type) ? StoreTemporary(value, "temporary") : value;
		
		public Value Visit(ResolvedLiteralExpressionNode node) =>
			MakeConstant(node.Type, node.Value);
		
		public Value Visit(ResolvedArrayExpressionNode node) => new ArrayValue((ArrayType)node.Type,
			LowerOperands(node.Values, static _ => Passing.Consume), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedUnaryOpExpressionNode node)
		{
			var operand = node.Operation?.Op == TokenType.OpAt ? VisitPlace(node.Operand) : VisitNode(node.Operand);
			return LowerUnaryOp(operand, node.Operation);
		}
		
		public Value Visit(ResolvedUndefExpressionNode node) =>
			new UndefValue(node.Type);
		
		public Value Visit(ResolvedOwnExpressionNode node) => new MoveValue(VisitPlace(node.Value));
		
		public Value Visit(ResolvedMutArgumentExpressionNode node) => new UnaryOpValue(node.Type,
			VisitPlace(node.Place), UnaryOperation.AddressOf, node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedVarExpressionNode node) =>
			new VariableValue(new(node.Symbol, node.Type), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedGlobalExpressionNode node) =>
			new GlobalValue(_getGlobalInfo(node.Symbol), node.Syntax.SourceLocation);
		
		public Value Visit(ResolvedBinaryOpExpressionNode node)
		{
			if (IsShortCircuitOp(node.Operation))
				return LowerShortCircuit(node.Left, node.Operation!.Op, node.Right);
			
			var operands = LowerOperands([node.Left, node.Right]);
			return LowerBinOp(operands[0], node.Operation, operands[1]);
		}
		
		private static bool IsShortCircuitOp(OperationImpl? op) =>
			op is NativeImpl { Op: TokenType.OpAmpersandAmpersand or TokenType.OpBarBar };
		
		private VariableValue LowerShortCircuit(IResolvedExpressionNode leftNode, TokenType op,
			IResolvedExpressionNode rightNode)
		{
			var sourceLocation = Join(leftNode.Syntax.SourceLocation, rightNode.Syntax.SourceLocation);
			
			var resultSymbol = CreateTempSymbol(NativeSymbols.Bool, "sc_result");
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(resultSymbol, new UndefValue(NativeSymbols.Bool),
				leftNode.Syntax.SourceLocation, CurrentScopeId));
			
			var result = new VariableValue(new(resultSymbol, NativeSymbols.Bool), sourceLocation);
			
			var rightBlock = CreateBlock("sc_right");
			var skipBlock = CreateBlock("sc_skip");
			var mergeBlock = CreateBlock("sc_merge");
			
			var isAnd = op == TokenType.OpAmpersandAmpersand;
			LowerBranch(leftNode, isAnd ? rightBlock : skipBlock, isAnd ? skipBlock : rightBlock);
			
			skipBlock.Instructions.Add(new ExpressionInstruction(new AssignValue(NativeSymbols.Bool, result,
				isAnd ? ConstantValue.False : ConstantValue.True, sourceLocation)));
			
			skipBlock.SetTerminator(new BranchTerminator(mergeBlock, leftNode.Syntax.SourceLocation));
			
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
			var symbol = new LocalVariableSymbol(new(TokenType.Identifier, SourceLocation.None, name), type, true);
			_temporaries.Add(symbol);
			return symbol;
		}
		
		private enum Passing
		{
			Read,
			Borrow,
			Consume
		}
		
		private List<Value> LowerOperands(IReadOnlyList<IResolvedExpressionNode> operands,
			Func<int, Passing>? passing = null)
		{
			var values = new List<Value>(operands.Count);
			var passings = new List<Passing>(operands.Count);
			for (var i = 0; i < operands.Count; i++)
			{
				var operand = operands[i];
				if (MayEmit(operand))
				{
					for (var j = 0; j < values.Count; j++)
					{
						var captured = CaptureAsAtomic(values[j], "operand");
						values[j] = passings[j] == Passing.Consume ? Consume(captured) : captured;
					}
				}
				
				passings.Add(passing?.Invoke(i) ?? Passing.Read);
				values.Add(passings[i] switch
				{
					Passing.Borrow => LowerBorrow(operand),
					Passing.Consume => Consume(VisitNode(operand)),
					_ => VisitNode(operand)
				});
			}
			
			return values;
		}
		
		private List<Value> LowerArguments(IReadOnlyList<IResolvedExpressionNode> arguments, FunctionInfo function,
			int firstParameter) => LowerOperands(arguments, GetArgumentPassing(function, firstParameter));
		
		private Func<int, Passing> GetArgumentPassing(FunctionInfo function, int firstParameter)
		{
			var signature = function.Signature;
			return i => firstParameter + i < signature.ParameterTypes.Length
				? GetPassing(signature.ParameterTypes[firstParameter + i], signature.GetMode(firstParameter + i))
				: Passing.Read;
		}
		
		private Passing GetPassing(TypeSymbol type, ParameterMode mode) =>
			_typePool.PassesByPointer(type, mode) ? Passing.Borrow
			: mode == ParameterMode.Own ? Passing.Consume
			: Passing.Read;
		
		private Value Consume(Value value) =>
			!_typePool.IsCopy(value.Type) && IsPlaceValue(value) ? new MoveValue(value) : value;
		
		private static bool IsPlaceValue(Value value) => value switch
		{
			VariableValue or GlobalValue or UnaryOpValue { Op: UnaryOperation.Dereference } => true,
			AccessValue v => IsPlaceValue(v.Target),
			IndexerValue v => IsPlaceValue(v.Target),
			EnumPayloadValue v => IsPlaceValue(v.Target),
			_ => false
		};
		
		private UnaryOpValue LowerBorrow(IResolvedExpressionNode argument)
		{
			var value = VisitNode(argument);
			var place = IsPlaceValue(value) ? value : StoreTemporary(value, "borrow");
			return new UnaryOpValue(_typePool.GetPointerType(argument.Type), place, UnaryOperation.AddressOf,
				argument.Syntax.SourceLocation);
		}
		
		private static bool IsPlace(IResolvedExpressionNode node) => node switch
		{
			ResolvedVarExpressionNode => true,
			ResolvedAccessExpressionNode { Member: FieldSymbol } n => IsPlace(n.Target),
			ResolvedIndexerExpressionNode n => IsPlace(n.Target),
			ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
			_ => false
		};
		
		private bool MayEmit(IResolvedExpressionNode node) => node switch
		{
			ResolvedLiteralExpressionNode or ResolvedVarExpressionNode or ResolvedGlobalExpressionNode
				or ResolvedFunctionReferenceExpressionNode or ResolvedUndefExpressionNode => false,
			ResolvedConversionExpressionNode n => MayEmit(n.Source),
			ResolvedAccessExpressionNode n => IsMaterialized(n.Target) || MayEmit(n.Target),
			ResolvedIndexerExpressionNode n => IsMaterialized(n.Target) || MayEmit(n.Target) || MayEmit(n.Index),
			ResolvedUnaryOpExpressionNode n => MayEmit(n.Operand),
			ResolvedMutArgumentExpressionNode n => MayEmit(n.Place),
			ResolvedOwnExpressionNode n => MayEmit(n.Value),
			ResolvedBinaryOpExpressionNode n => IsShortCircuitOp(n.Operation) || MayEmit(n.Left) || MayEmit(n.Right),
			ResolvedFunctionCallExpressionNode n => MayEmitOperands(n.Arguments, GetArgumentPassing(n.Function, 0)),
			ResolvedIndirectCallExpressionNode n => MayEmitOperands([n.Target, ..n.Arguments],
				GetOperandPassing(n.FunctionType)),
			ResolvedArrayExpressionNode n => n.Values.Any(MayEmit),
			ResolvedEnumCaseExpressionNode n => n.Payload.Any(MayEmit),
			_ => true
		};
		
		private bool MayEmitOperands(IReadOnlyList<IResolvedExpressionNode> operands, Func<int, Passing> passing) =>
			operands.Where((operand, i) => passing(i) == Passing.Borrow && !IsPlace(operand) || MayEmit(operand)).Any();
		
		private bool IsMaterialized(IResolvedExpressionNode target) =>
			!IsPlace(target) && _typePool.NeedsDrop(target.Type);
		
		public Value Visit(ResolvedAssignmentExpressionNode node) => LowerAssignment(node, true);
		
		private Value LowerAssignment(ResolvedAssignmentExpressionNode node, bool isValue)
		{
			var left = VisitPlace(node.Left);
			var dropsOld = _typePool.NeedsDrop(node.Type);
			
			// Need to stabilize the left side first so it doesn't double-evaluate
			var emits = MayEmit(node.Right);
			if (isValue || node.Operation is not null || emits || dropsOld)
				left = StabilizeStorage(left);
			
			var current = node.Operation is not null && emits ? CaptureAsAtomic(left, "current") : left;
			var right = Consume(VisitNode(node.Right));
			var value = node.Operation is null ? right : LowerBinOp(current, node.Operation, right);
			if (currentBlock is null)
				return left;
			
			if (dropsOld)
			{
				value = Consume(CaptureAsAtomic(value, "assigned"));
				currentBlock.Instructions.Add(new DropInstruction(left, node.Op.SourceLocation));
			}
			
			currentBlock.Instructions.Add(
				new ExpressionInstruction(new AssignValue(node.Type, left, value, node.Op.SourceLocation)));
			
			return left;
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
		
		private Value LowerBinOp(Value left, OperationImpl? op, Value right) => op switch
		{
			NativeImpl { ParameterTypes: [EnumSymbol enumType, _] } i => new BinOpValue(i.ReturnType,
				new EnumTagValue(_typePool.GetTagType(enumType), left, left.SourceLocation),
				new EnumTagValue(_typePool.GetTagType(enumType), right, right.SourceLocation),
				ToBinaryOperation(i.Op), Join(left.SourceLocation, right.SourceLocation)),
			NativeImpl i => new BinOpValue(i.ReturnType, left, right, ToBinaryOperation(i.Op),
				Join(left.SourceLocation, right.SourceLocation)),
			FunctionImpl i => new CallValue(i.Function, [left, right],
				Join(left.SourceLocation, right.SourceLocation)),
			PointerOffsetImpl i => new PointerOffsetValue(i.PointerType, left, right, ToBinaryOperation(i.Op),
				Join(left.SourceLocation, right.SourceLocation)),
			PointerDifferenceImpl i => new PointerDifferenceValue(left, right, i.PointerType,
				Join(left.SourceLocation, right.SourceLocation)),
			ConversionImpl i => LowerConversionBinOp(left, i, right),
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
				ToBinaryOperation(conversion.Op), Join(left.SourceLocation, right.SourceLocation));
			
			return conversion.ResultConversion is { } resultConversion
				? Convert(intermediateResult, resultConversion)
				: intermediateResult;
		}
		
		private static Value LowerUnaryOp(Value operand, OperationImpl? op) => op switch
		{
			NativeImpl native => new UnaryOpValue(native.ReturnType, operand, ToUnaryOperation(native.Op),
				operand.SourceLocation),
			FunctionImpl function => new CallValue(function.Function, [operand], operand.SourceLocation),
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
			VariableValue or GlobalValue => value,
			IndexerValue or AccessValue or UnaryOpValue { Op: UnaryOperation.Dereference } => StabilizeStorage(value),
			_ => CaptureAsAtomic(value, "target")
		};
		
		private Value CaptureAsAtomic(Value value, string hint) =>
			IsStable(value) ? value : StoreTemporary(value, hint);
		
		private VariableValue StoreTemporary(Value value, string hint)
		{
			var symbol = CreateTempSymbol(value.Type, hint);
			
			Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, value, value.SourceLocation, CurrentScopeId));
			
			return new VariableValue(new(symbol, value.Type), value.SourceLocation);
		}
		
		private bool IsStable(Value value) => value switch
		{
			ConstantValue or ZeroValue or DefaultValue or UndefValue or FunctionReferenceValue => true,
			VariableValue { Variable.Symbol: LocalVariableSymbol symbol } =>
				!symbol.IsMutable && !symbol.IsDeferred || _temporaries.Contains(symbol),
			VariableValue { Variable.Symbol: ParameterSymbol { Mode: ParameterMode.Mut } } => true,
			MoveValue { Place: VariableValue { Variable.Symbol: LocalVariableSymbol symbol } } =>
				_temporaries.Contains(symbol),
			_ => false
		};
	}
}