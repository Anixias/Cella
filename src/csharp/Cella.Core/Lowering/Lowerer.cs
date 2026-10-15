using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
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
	
	private readonly Dictionary<ModuleSymbol, LoweredModule> _modules = [];
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
		loweredModule.Files.Add(currentFile);
		currentFile.ImportedFunctions.AddRange(node.ImportedFunctions);
		
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
		
		currentFile = null;
	}
	
	public void Visit(ResolvedFunctionNode node) =>
		currentFile?.Functions.Add(FunctionLowerer.Lower(node, evaluator, typePool, getGlobalInfo));
	
	public void Visit(ResolvedRecordNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentFile?.Types.Add(node.Symbol);
	}
	
	public void Visit(ResolvedEnumNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
		
		currentFile?.Types.Add(node.Symbol);
	}
	
	public void Visit(ResolvedFieldNode node)
	{
	}
	
	public void Visit(ResolvedTraitNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
	}
	
	public void Visit(ResolvedImplNode node)
	{
		foreach (var member in node.Members)
			VisitNode(member);
	}
	
	public void Visit(ResolvedExternalFunctionNode node) => currentFile?.ExternalFunctions.Add(node.FunctionInfo);
	
	public void Visit(ResolvedGlobalNode node) => currentFile?.Globals.Add(node.Info);
	
	private sealed class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value>
	{
		private readonly record struct LoopContext
		(
			BasicBlock BreakTarget,
			BasicBlock ContinueTarget,
			int BreakDepth,
			int ContinueDepth
		);
		
		private readonly LoweredFunction _function;
		private readonly ConstantEvaluator _evaluator;
		private readonly TypePool _typePool;
		private readonly PatternCoverage _coverage;
		private readonly Func<GlobalSymbol, GlobalInfo> _getGlobalInfo;
		private readonly Stack<LoopContext> _loopStack = [];
		private readonly Dictionary<LabelSymbol, LoopContext> _loopsByLabel = [];
		private readonly Stack<ActiveScope> _activeScopes = [];
		private readonly HashSet<int> _temporaryScopes = [];
		private readonly Dictionary<int, List<VariableInfo>> _scopeDrops = [];
		private readonly HashSet<LocalVariableSymbol> _temporaries = [];
		private readonly HashSet<IResolvedExpressionNode> _returnedValues = [];
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
			_coverage = new PatternCoverage(typePool, evaluator);
			_getGlobalInfo = getGlobalInfo;
			currentBlock = CreateBlock("entry");
		}
		
		private ulong NextLoopId() => nextLoopId++;
		private ulong NextTempId() => nextTempId++;
		
		private StatementStart? currentStatement;
		
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
			var captures = node.FunctionInfo.Symbol.Captures;
			var location = node.Syntax.SourceLocation;
			var fields = captures.Select(static capture => ((PointerType)capture.Type).BaseType).ToArray();
			
			for (var i = 0; i < captures.Length; i++)
				Declare(GetOrMakeBlock(), new LocalVarInstruction(captures[i], node.FunctionInfo.Symbol.OwnsCaptures
					? new ClosureFieldValue(i, fields, captures[i].Type)
					: new EnvironmentValue(i, captures[i].Type), location));
			
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
			LowerConstant(_evaluator.EvaluateWithoutDrops(node), node.Syntax.SourceLocation) ?? VisitPlace(node);
		
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
			var parent = currentStatement;
			StatementStart? previous = null;
			foreach (var statement in node.Statements)
			{
				currentStatement = previous =
					new StatementStart(statement.Syntax.SourceLocation, GetOrMakeBlock(), parent, previous);
				
				_function.Statements.Add(currentStatement);
				VisitNode(statement);
			}
			
			currentStatement = parent;
			EndCurrentScope();
		}
		
		public Value Visit(ResolvedBreakExpressionNode node)
		{
			var context = GetLoopContext(node.Label);
			EmitScopeEndsToDepth(context.BreakDepth);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(context.BreakTarget, node.Syntax.SourceLocation));
			currentBlock = null;
			return new UndefValue(node.Type);
		}
		
		public Value Visit(ResolvedContinueExpressionNode node)
		{
			var context = GetLoopContext(node.Label);
			EmitScopeEndsToDepth(context.ContinueDepth);
			GetOrMakeBlock().SetTerminator(new BranchTerminator(context.ContinueTarget, node.Syntax.SourceLocation));
			currentBlock = null;
			return new UndefValue(node.Type);
		}
		
		public void Visit(ResolvedExpressionStatementNode node)
		{
			if (node.Expression is ResolvedErasedCallExpressionNode erased)
			{
				LowerErasedCall(erased);
				return;
			}
			
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
		
		public void Visit(ResolvedDropStatementNode node)
		{
			BeginScope(node.Syntax.SourceLocation, true);
			var target = VisitPlace(node.Target);
			currentBlock?.Instructions.Add(new DropInstruction(target, node.Syntax.SourceLocation));
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
			LowerMatch(node.Value,
				[
					..node.Arms.Select(static arm =>
						new LoweredArm(arm.Patterns, arm.Guard, arm.Body.Syntax.SourceLocation))
				],
				node.Syntax.SourceLocation, node.OwnsValue, index => VisitNode(node.Arms[index].Body));
			
			EndCurrentScope();
		}
		
		public Value Visit(ResolvedMatchExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var isReturned = _returnedValues.Contains(node);
			VariableValue? result = null;
			if (node.Type is not NeverType)
			{
				var symbol = CreateTempSymbol(node.Type, "match_result");
				Declare(GetOrMakeBlock(), new LocalVarInstruction(symbol, new UndefValue(node.Type), location,
					CurrentScopeId));
				
				result = new VariableValue(new(symbol, node.Type), location);
			}
			
			BeginScope(location, true);
			LowerMatch(node.Value,
				[
					..node.Arms.Select(static arm =>
						new LoweredArm(arm.Patterns, arm.Guard, arm.Value.Syntax.SourceLocation))
				],
				location, node.OwnsValue, index =>
				{
					var arm = node.Arms[index].Value;
					if (isReturned)
						_returnedValues.Add(arm);
					
					var value = Consume(VisitNode(arm), isImplicit: !isReturned && NamesPlace(arm));
					if (result is not null)
						currentBlock?.Instructions.Add(new ExpressionInstruction(
							new AssignValue(node.Type, result, value, arm.Syntax.SourceLocation)));
				});
			
			EndCurrentScope();
			return result ?? (Value)new UndefValue(node.Type);
		}
		
		private readonly record struct LoweredArm
		(
			ImmutableArray<ResolvedPattern> Patterns,
			IResolvedExpressionNode? Guard,
			SourceLocation Location
		);
		
		private void LowerMatch(IResolvedExpressionNode value, IReadOnlyList<LoweredArm> arms, SourceLocation location,
			bool ownsValue, Action<int> lowerArm)
		{
			var scrutinee = value.Type is DynType ? LowerDynAddress(value) : LowerScrutinee(value);
			var mergeBlock = CreateBlock("match_end");
			var fallthrough = !arms.Any(static arm => arm.Patterns.IsEmpty) && _coverage.Covers(value.Type,
				arms.Where(static arm => arm.Guard is null).SelectMany(static arm => arm.Patterns))
				? CreateBlock("match_unreachable")
				: mergeBlock;
			
			var armBlocks = arms.Select(_ => CreateBlock("match_arm")).ToList();
			var nextBlocks = new BasicBlock?[arms.Count];
			GetOrMakeBlock();
			if (!LowerSwitch(scrutinee, arms, armBlocks, fallthrough, location))
			{
				for (var i = 0; i < arms.Count; i++)
				{
					var nextBlock = i == arms.Count - 1 ? fallthrough : CreateBlock("match_next");
					nextBlocks[i] = nextBlock;
					if (arms[i].Patterns.IsEmpty)
					{
						GetOrMakeBlock().SetTerminator(new BranchTerminator(armBlocks[i], location));
						break;
					}
					
					LowerPatternTests(scrutinee, arms[i].Patterns, armBlocks[i], nextBlock);
					currentBlock = nextBlock;
				}
			}
			
			for (var i = 0; i < arms.Count; i++)
			{
				var (patterns, guard, armLocation) = arms[i];
				if (!armBlocks[i].HasPredecessor())
				{
					_function.Blocks.Remove(armBlocks[i]);
					continue;
				}
				
				currentBlock = armBlocks[i];
				var scope = BeginScope(armLocation);
				if (patterns is [var single])
					BindPattern(scrutinee, single, ownsValue);
				
				if (guard is not null)
				{
					var bodyBlock = CreateBlock("match_guarded");
					var failedBlock = CreateBlock("match_guard_failed");
					LowerBranch(guard, bodyBlock, failedBlock);
					if (failedBlock.HasPredecessor())
						ExitScope(failedBlock, scope, nextBlocks[i] ?? fallthrough);
					else
						_function.Blocks.Remove(failedBlock);
					
					currentBlock = bodyBlock;
				}
				
				if (ownsValue)
					ReleaseScrutinee(scrutinee, patterns is [var owned] ? owned : null);
				
				lowerArm(i);
				EndCurrentScope();
				currentBlock?.FillTerminator(new BranchTerminator(mergeBlock, location));
			}
			
			if (fallthrough != mergeBlock)
			{
				if (fallthrough.HasPredecessor())
					fallthrough.SetTerminator(new UnreachableTerminator(location));
				else
					_function.Blocks.Remove(fallthrough);
			}
			
			currentBlock = null;
			ContinueWith(mergeBlock);
		}
		
		private bool LowerSwitch(Value scrutinee, IReadOnlyList<LoweredArm> arms, List<BasicBlock> armBlocks,
			BasicBlock fallthrough, SourceLocation location)
		{
			const int largestRange = 256;
			Value selector;
			if (scrutinee.Type is EnumSymbol enumType)
				selector = new EnumTagValue(_typePool.GetTagType(enumType), scrutinee, location);
			else if (scrutinee.Type == NativeSymbols.Bool ||
			         scrutinee.Type is IntegerType integer && _evaluator.GetLimits(integer) is var (min, max) &&
			         min >= long.MinValue && max <= ulong.MaxValue)
				selector = scrutinee;
			else
				return false;
			
			var cases = new List<(BigInteger Value, BasicBlock Target)>();
			var seen = new HashSet<BigInteger>();
			var defaultTarget = fallthrough;
			for (var i = 0; i < arms.Count; i++)
			{
				var arm = arms[i];
				if (arm.Guard is not null)
					return false;
				
				if (arm.Patterns.IsEmpty)
				{
					defaultTarget = armBlocks[i];
					break;
				}
				
				foreach (var pattern in arm.Patterns)
				{
					IEnumerable<BigInteger> values;
					if (scrutinee.Type is EnumSymbol switched)
					{
						if (pattern is not { Case: { } enumCase, IsConditional: false })
							return false;
						
						values = [_typePool.GetCaseValue(switched, enumCase)];
					}
					else if (pattern is { Spans: [var (low, high)], Test: not null } && high - low < largestRange)
					{
						values = Enumerable.Range(0, (int)(high - low + 1)).Select(offset => low + offset);
					}
					else
					{
						return false;
					}
					
					foreach (var value in values)
					{
						if (seen.Add(value))
							cases.Add((value, armBlocks[i]));
					}
				}
			}
			
			if (cases.Count == 0)
				return false;
			
			GetOrMakeBlock().SetTerminator(new SwitchTerminator(CaptureAsAtomic(selector, "selector"), defaultTarget,
				[..cases], location));
			
			currentBlock = null;
			return true;
		}
		
		private void LowerPatternTests(Value subject, ImmutableArray<ResolvedPattern> patterns, BasicBlock success,
			BasicBlock failure)
		{
			for (var i = 0; i < patterns.Length; i++)
			{
				var next = i == patterns.Length - 1 ? failure : CreateBlock("match_alternative");
				LowerPatternTest(subject, patterns[i], success, next);
				currentBlock = next;
			}
		}
		
		private void LowerPatternTest(Value subject, ResolvedPattern pattern, BasicBlock success, BasicBlock failure)
		{
			var location = subject.SourceLocation;
			var block = GetOrMakeBlock();
			if (pattern.TestedType is not null)
			{
				var tested = pattern.IsConditional ? CreateBlock("match_fields") : success;
				block.SetTerminator(new ConditionalBranchTerminator(TestDyn(subject, pattern), tested, failure,
					location));
				
				currentBlock = null;
				if (tested == success)
					return;
				
				currentBlock = tested;
				LowerFieldTests(CastRecord(subject, pattern), pattern, success, failure);
				return;
			}
			
			if (pattern.Record is not null)
			{
				LowerFieldTests(subject, pattern, success, failure);
				return;
			}
			
			if (pattern.Test is { } test)
			{
				var tested = subject.Type is PointerType { BaseType: DynType dyn }
					? new UnaryOpValue(dyn, subject, UnaryOperation.Dereference, location)
					: subject;
				
				DeclareSubject(pattern.Subject!, tested);
				LowerBranch(test, success, failure);
				return;
			}
			
			var enumCase = pattern.Case!;
			var elements = pattern.Elements.Select(static (element, i) => (Element: element, Index: i))
				.Where(static entry => entry.Element is not null)
				.ToList();
			
			var matched = elements.Count == 0 ? success : CreateBlock("match_payload");
			block.SetTerminator(new ConditionalBranchTerminator(TestCase(subject, enumCase), matched, failure,
				location));
			
			currentBlock = matched;
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)subject.Type, enumCase);
			for (var i = 0; i < elements.Count; i++)
			{
				var (element, index) = elements[i];
				var next = i == elements.Count - 1 ? success : CreateBlock("match_payload");
				LowerPatternTest(new EnumPayloadValue(payloadTypes[index], subject, enumCase, index, location),
					element!,
					next, failure);
				
				currentBlock = next;
			}
			
			currentBlock = null;
		}
		
		private void LowerFieldTests(Value record, ResolvedPattern pattern, BasicBlock success, BasicBlock failure)
		{
			var elements = pattern.Elements.Select(static (element, i) => (Element: element, Index: i))
				.Where(static entry => entry.Element is { IsIrrefutable: false })
				.ToList();
			
			if (elements.Count == 0)
			{
				GetOrMakeBlock().SetTerminator(new BranchTerminator(success, record.SourceLocation));
				currentBlock = null;
				return;
			}
			
			for (var i = 0; i < elements.Count; i++)
			{
				var (element, index) = elements[i];
				var next = i == elements.Count - 1 ? success : CreateBlock("match_field");
				LowerPatternTest(GetField(record, pattern, index, record.SourceLocation), element!, next, failure);
				currentBlock = next;
			}
			
			currentBlock = null;
		}
		
		private AccessValue GetField(Value record, ResolvedPattern pattern, int index, SourceLocation location)
		{
			var field = pattern.Fields[index];
			return new AccessValue(_typePool.GetTypeOfMember(field), record, field, location);
		}
		
		private UnaryOpValue CastRecord(Value address, ResolvedPattern pattern)
		{
			var location = address.SourceLocation;
			var type = pattern.TestedType!;
			var pointer = _typePool.GetPointerType(type);
			return new UnaryOpValue(type,
				new ConversionValue(address, new DynCastConversion(address.Type, pointer), location),
				UnaryOperation.Dereference, location);
		}
		
		private void DeclareSubject(LocalVariableSymbol subject, Value value)
		{
			var location = subject.Identifier.SourceLocation;
			var initializer = subject is { IsBorrowBinding: true, Type: PointerType pointer }
				? new UnaryOpValue(pointer, value, UnaryOperation.AddressOf, location)
				: value;
			
			Declare(GetOrMakeBlock(), new LocalVarInstruction(subject, initializer, location, CurrentScopeId));
		}
		
		private Value LowerDynAddress(IResolvedExpressionNode node)
		{
			var place = LowerScrutinee(node);
			return CaptureAsAtomic(new UnaryOpValue(_typePool.GetPointerType(place.Type), place,
				UnaryOperation.AddressOf, place.SourceLocation), "dyn");
		}
		
		private static ConversionValue TestDyn(Value address, ResolvedPattern pattern) =>
			new(address, new DynTestConversion(address.Type, pattern.TestedType!), address.SourceLocation);
		
		private void BindDyn(Value address, ResolvedPattern pattern)
		{
			if (pattern.TypeBinding is not { } binding)
				return;
			
			var location = binding.Identifier.SourceLocation;
			GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding,
				new ConversionValue(address, new DynCastConversion(address.Type, binding.Type), location), location,
				CurrentScopeId));
		}
		
		private void LowerBranch(IResolvedExpressionNode condition, BasicBlock trueBlock, BasicBlock falseBlock)
		{
			var location = condition.Syntax.SourceLocation;
			if (_evaluator.EvaluateWithoutDrops(condition) is null)
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
					
					case ResolvedIsExpressionNode node when !IsSimpleTest(node.Pattern):
					{
						var scrutinee = node.Pattern.TestedType is not null
							? LowerDynAddress(node.Value)
							: LowerScrutinee(node.Value);
						
						var bindBlock = CreateBlock("is_bind");
						LowerPatternTest(scrutinee, node.Pattern, bindBlock, falseBlock);
						currentBlock = bindBlock;
						BindPattern(scrutinee, node.Pattern, node.OwnsValue);
						if (node.OwnsValue)
							ReleaseScrutinee(scrutinee, node.Pattern);
						
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
		
		private static bool IsSimpleTest(ResolvedPattern pattern) =>
			!pattern.HasBindings && !pattern.IsConditional;
		
		private void BindPattern(Value subject, ResolvedPattern pattern, bool ownsValue)
		{
			if (pattern.TestedType is not null)
			{
				BindDyn(subject, pattern);
				if (pattern.Record is not null)
					BindFields(CastRecord(subject, pattern), pattern, ownsValue);
				
				return;
			}
			
			if (pattern.Record is not null)
			{
				BindFields(subject, pattern, ownsValue);
				return;
			}
			
			if (pattern.Case is not { } enumCase)
				return;
			
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)subject.Type, enumCase);
			for (var i = 0; i < pattern.Bindings.Length; i++)
			{
				var payload = new EnumPayloadValue(payloadTypes[i], subject, enumCase, i, subject.SourceLocation);
				BindSlot(payload, pattern.Bindings[i], pattern.Elements.Length > i ? pattern.Elements[i] : null,
					ownsValue);
			}
		}
		
		private void BindFields(Value record, ResolvedPattern pattern, bool ownsValue)
		{
			for (var i = 0; i < pattern.Fields.Length; i++)
			{
				var location = pattern.Bindings[i]?.Identifier.SourceLocation ?? record.SourceLocation;
				BindSlot(GetField(record, pattern, i, location), pattern.Bindings[i], pattern.Elements[i], ownsValue);
			}
		}
		
		private void BindSlot(Value slot, LocalVariableSymbol? binding, ResolvedPattern? element, bool ownsValue)
		{
			if (element is not null)
				BindPattern(slot, element, ownsValue);
			
			if (binding is null)
				return;
			
			var location = binding.Identifier.SourceLocation;
			if (binding is { IsBorrowBinding: true, Type: PointerType pointer })
				GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding,
					new UnaryOpValue(pointer, slot, UnaryOperation.AddressOf, location), location, CurrentScopeId));
			else if (_typePool.NeedsDrop(slot.Type))
				Declare(GetOrMakeBlock(),
					new LocalVarInstruction(binding, new MoveValue(slot), location, CurrentScopeId));
			else
				GetOrMakeBlock().Instructions.Add(new LocalVarInstruction(binding, slot, location, CurrentScopeId));
		}
		
		private void ReleaseScrutinee(Value scrutinee, ResolvedPattern? pattern)
		{
			if (pattern is { Case: not null })
			{
				ReleasePayload(scrutinee, pattern);
				GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(Consume(scrutinee)));
			}
			else
			{
				StoreTemporary(Consume(scrutinee), "unbound");
			}
		}
		
		private void ReleasePayload(Value subject, ResolvedPattern pattern)
		{
			var enumCase = pattern.Case!;
			var payloadTypes = _typePool.GetPayloadTypes((EnumSymbol)subject.Type, enumCase);
			for (var i = 0; i < pattern.Bindings.Length; i++)
			{
				if (pattern.Bindings[i] is not null)
					continue;
				
				var payload = new EnumPayloadValue(payloadTypes[i], subject, enumCase, i, subject.SourceLocation);
				ReleaseSlot(payload, pattern.Elements.Length > i ? pattern.Elements[i] : null);
			}
		}
		
		private void ReleaseSlot(Value slot, ResolvedPattern? element)
		{
			if (element is { Case: not null })
				ReleasePayload(slot, element);
			else if (element is { Record: not null, TestedType: null } && MovesParts(element))
				ReleaseFields(slot, element);
			else if (_typePool.NeedsDrop(slot.Type))
				StoreTemporary(new MoveValue(slot), "unbound");
		}
		
		private void ReleaseFields(Value record, ResolvedPattern pattern)
		{
			foreach (var field in _typePool.GetMembers(record.Type).OfType<FieldSymbol>())
			{
				var index = pattern.Fields.IndexOf(field);
				var value = new AccessValue(_typePool.GetTypeOfMember(field), record, field, record.SourceLocation);
				if (index < 0)
				{
					if (_typePool.NeedsDrop(value.Type))
						StoreTemporary(new MoveValue(value), "unbound");
				}
				else if (pattern.Bindings[index] is null)
				{
					ReleaseSlot(value, pattern.Elements[index]);
				}
			}
		}
		
		private bool MovesParts(ResolvedPattern pattern) =>
			pattern.AllBindings.Any(binding => !binding.IsBorrowBinding && _typePool.NeedsDrop(binding.Type));
		
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
			block.SetTerminator(ReturnTerminator.FromValue(value, location));
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
				var address = sourceValue is ResolvedMutArgumentExpressionNode
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
			var one = MakeConstant(type, BigInteger.One);
			var sum = new BinOpValue(type, counter, one, BinaryOperation.WrappingAddition, location);
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
			VisitInLoop(node.Body, exitBlock, condBlock, node.Label, scopeDepth, scopeDepth);
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
		
		public Value Visit(ResolvedFunctionReferenceExpressionNode node) =>
			new FunctionReferenceValue(node.Function, node.Type, node.Syntax.SourceLocation);
		
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
			Func<int, Passing>? passing = null, bool hasReceiver = false, bool isPlaceOperator = false)
		{
			var values = new List<Value>(operands.Count);
			var passings = new List<Passing>(operands.Count);
			var deferred = new List<bool>(operands.Count);
			for (var i = 0; i < operands.Count; i++)
			{
				var operand = operands[i];
				passings.Add(passing?.Invoke(i) ?? Passing.Read);
				deferred.Add(DefersAccess(operand, passings[i], i == 0 && isPlaceOperator));
				if (MayEmit(operand) || passings[i] == Passing.Borrow && !IsPlace(operand))
					SettleOperands(values, passings, deferred);
				
				values.Add(passings[i] switch
				{
					Passing.Borrow => LowerBorrow(operand),
					Passing.Consume => Consume(VisitNode(operand), (i > 0 || !hasReceiver) && NamesPlace(operand)),
					_ => VisitNode(operand)
				});
			}
			
			if (values.Count > 1 &&
			    Enumerable.Range(0, values.Count - 1).Any(i => deferred[i] && HasAccessWork(values[i])))
				SettleOperands(values, passings, deferred);
			
			return values;
		}
		
		private static bool DefersAccess(IResolvedExpressionNode operand, Passing passing, bool isReceiver) =>
			isReceiver || passing == Passing.Borrow || operand is ResolvedMutArgumentExpressionNode;
		
		private void SettleOperands(List<Value> values, List<Passing> passings, List<bool> deferred)
		{
			for (var i = 0; i < values.Count; i++)
				values[i] = SettleOperand(values[i], passings[i], deferred[i]);
		}
		
		private List<Value> LowerArguments(IReadOnlyList<IResolvedExpressionNode> arguments, FunctionInfo function,
			int firstParameter) => LowerOperands(arguments, GetArgumentPassing(function, firstParameter),
			firstParameter == 0 && function.Symbol.Kind == FunctionKind.Method,
			firstParameter == 0 && IsPlaceOperator(function));
		
		private Func<int, Passing> GetArgumentPassing(FunctionInfo function, int firstParameter)
		{
			var signature = function.DeclaredSignature;
			return i => firstParameter + i < signature.ParameterTypes.Length
				? GetPassing(signature.GetMode(firstParameter + i))
				: Passing.Read;
		}
		
		private static Passing GetPassing(ParameterMode mode) =>
			TypePool.PassesByPointer(mode) ? Passing.Borrow
			: mode == ParameterMode.Own ? Passing.Consume
			: Passing.Read;
		
		private Value Consume(Value value, bool isImplicit = false) =>
			!_typePool.IsCopy(value.Type) && IsPlaceValue(value)
				? new MoveValue(value) { IsImplicit = isImplicit }
				: value;
		
		private static bool IsPlaceValue(Value value) => value switch
		{
			VariableValue or GlobalValue or UnaryOpValue { Op: UnaryOperation.Dereference } => true,
			AccessValue v => IsPlaceValue(v.Target),
			IndexerValue { Target.Type: StringType } => true,
			IndexerValue v => IsPlaceValue(v.Target),
			EnumPayloadValue v => IsPlaceValue(v.Target),
			_ => false
		};
		
		private UnaryOpValue LowerBorrow(IResolvedExpressionNode argument)
		{
			var value = VisitNode(argument);
			var location = argument.Syntax.SourceLocation;
			var place = IsPlaceValue(value) ? value : StoreTemporary(value, "borrow", location);
			return new UnaryOpValue(_typePool.GetPointerType(argument.Type), place, UnaryOperation.AddressOf, location);
		}
		
		private static bool IsPlace(IResolvedExpressionNode node) => node switch
		{
			ResolvedVarExpressionNode => true,
			ResolvedAccessExpressionNode { Member: FieldSymbol } n => IsPlace(n.Target),
			ResolvedIndexerExpressionNode { Target.Type: StringType } => true,
			ResolvedIndexerExpressionNode n => IsPlace(n.Target),
			ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
			_ => false
		};
		
		private static bool NamesPlace(IResolvedExpressionNode node) =>
			IsPlace(node is ResolvedAssignmentExpressionNode assignment ? assignment.Left : node);
		
		private bool MayEmit(IResolvedExpressionNode node) => node switch
		{
			{ Type: NeverType } => true,
			ResolvedLiteralExpressionNode or ResolvedVarExpressionNode or ResolvedGlobalExpressionNode
				or ResolvedFunctionReferenceExpressionNode or ResolvedUndefExpressionNode
				or ResolvedSizeOfExpressionNode or ResolvedAlignOfExpressionNode
				or ResolvedValueParameterExpressionNode => false,
			ResolvedConversionExpressionNode n => MayEmit(n.Source),
			ResolvedAccessExpressionNode n => IsMaterialized(n.Target) || MayEmit(n.Target),
			ResolvedIndexerExpressionNode n => IsMaterialized(n.Target) || MayEmit(n.Target) || MayEmit(n.Index) ||
			                                   n.Target.Type is ArrayType && HasPlaceAccess(n.Target),
			ResolvedUnaryOpExpressionNode n => MayEmit(n.Operand),
			ResolvedMutArgumentExpressionNode n => MayEmit(n.Place),
			ResolvedBorrowExpressionNode n => !IsPlace(n.Place) || MayEmit(n.Place),
			ResolvedOwnExpressionNode n => MayEmit(n.Value),
			ResolvedBinaryOpExpressionNode n => IsShortCircuitOp(n.Operation) || MayEmit(n.Left) || MayEmit(n.Right),
			ResolvedFunctionCallExpressionNode n => MayEmitOperands(n.Arguments, GetArgumentPassing(n.Function, 0),
				IsPlaceOperator(n.Function)),
			ResolvedIndirectCallExpressionNode n => MayEmitOperands([n.Target, ..n.Arguments],
				GetOperandPassing(n.FunctionType)),
			ResolvedArrayExpressionNode n => n.Values.Any(MayEmit),
			ResolvedFStrExpressionNode n => !n.Values.IsEmpty ||
			                                n.Text is { } text && (!IsPlace(text) || MayEmit(text)),
			ResolvedEnumCaseExpressionNode n => n.Payload.Any(MayEmit),
			_ => true
		};
		
		private bool MayEmitOperands(IReadOnlyList<IResolvedExpressionNode> operands, Func<int, Passing> passing,
			bool isPlaceOperator = false) => operands.Where((operand, i) =>
			passing(i) == Passing.Borrow && !IsPlace(operand) || MayEmit(operand) ||
			i < operands.Count - 1 && DefersAccess(operand, passing(i), i == 0 && isPlaceOperator) &&
			HasPlaceAccess(operand)).Any();
		
		private static bool HasPlaceAccess(IResolvedExpressionNode node) => node switch
		{
			ResolvedMutArgumentExpressionNode n => HasPlaceAccess(n.Place),
			ResolvedIndexerExpressionNode => true,
			ResolvedAccessExpressionNode n => HasPlaceAccess(n.Target),
			ResolvedUnaryOpExpressionNode { Operation.Op: TokenType.OpStar } => true,
			_ => false
		};
		
		private bool IsMaterialized(IResolvedExpressionNode target) =>
			!IsPlace(target) && _typePool.NeedsDrop(target.Type);
		
		public Value Visit(ResolvedAssignmentExpressionNode node) => LowerAssignment(node, true);
		
		public Value Visit(ResolvedPropertyExpressionNode node) => throw new InvalidOperationException();
		
		public Value Visit(ResolvedPropertyAssignmentExpressionNode node)
		{
			var location = node.Syntax.SourceLocation;
			var receiver = node.Receiver is null ? null : LowerReceiver(node.Receiver);
			if (node.Compound is { } compound)
			{
				var valueIndex = node.Setter.DeclaredSignature.ParameterTypes.Length - 1;
				var passing = GetArgumentPassing(compound, 1);
				var args = LowerArguments([node.Right], compound, 1)
					.Select((value, i) => SettleOperand(value, passing(i), passing(i) == Passing.Borrow))
					.ToList();
				
				receiver = receiver is null ? null : StabilizeStorage(receiver);
				var place = StoreTemporary(new CallValue(node.Getter, PassReceiver(node.Getter, receiver), location),
					"current");
				
				args.Insert(0, new UnaryOpValue(_typePool.GetPointerType(place.Type), place, UnaryOperation.AddressOf,
					location));
				
				GetOrMakeBlock().Instructions.Add(new ExpressionInstruction(new CallValue(compound, args, location)));
				return new CallValue(node.Setter,
					[..PassReceiver(node.Setter, receiver), PassChainArgument(place, node.Setter, valueIndex, false)],
					location);
			}
			
			var right = CaptureAsAtomic(Consume(VisitNode(node.Right)), "operand");
			receiver = receiver is null ? null : StabilizeStorage(receiver);
			var current = new CallValue(node.Getter, PassReceiver(node.Getter, receiver), location);
			var value = LowerBinOp(current, node.Operation!, right, location);
			return new CallValue(node.Setter, [..PassReceiver(node.Setter, receiver), value], location);
		}
		
		private Value LowerReceiver(IResolvedExpressionNode node)
		{
			var value = VisitPlace(node);
			return IsPlaceValue(value) ? StabilizeOperands(value) : StoreTemporary(value, "receiver");
		}
		
		private List<Value> PassReceiver(FunctionInfo accessor, Value? receiver)
		{
			if (receiver is null)
				return [];
			
			var signature = accessor.DeclaredSignature;
			var mode = signature.GetMode(0);
			if (mode != ParameterMode.Mut && GetPassing(mode) != Passing.Borrow)
				return [receiver];
			
			return
			[
				new UnaryOpValue(_typePool.GetPointerType(receiver.Type), receiver, UnaryOperation.AddressOf,
					receiver.SourceLocation)
			];
		}
		
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
				or AlignOfValue or ValueParameterValue => true,
			VariableValue { Variable.Symbol: LocalVariableSymbol symbol } =>
				!symbol.IsMutable && !symbol.IsDeferred || _temporaries.Contains(symbol),
			VariableValue { Variable.Symbol: ParameterSymbol { Mode: ParameterMode.Mut } } => true,
			MoveValue { Place: VariableValue { Variable.Symbol: LocalVariableSymbol symbol } } =>
				_temporaries.Contains(symbol),
			_ => false
		};
	}
}