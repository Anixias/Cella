using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Lowering;

public sealed partial class Lowerer
(
	ConstantEvaluator evaluator,
	TypePool typePool,
	Func<GlobalSymbol, GlobalInfo> getGlobalInfo,
	ErrorContainment containment
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
	
	public void Visit(ResolvedFunctionNode node)
	{
		if (!node.IsInline && !containment.IsBroken(node))
			currentFile?.Functions.Add(FunctionLowerer.Lower(node, evaluator, typePool, getGlobalInfo));
	}
	
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
	
	private sealed partial class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value>
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
			
			if (node.Body is IResolvedStatementNode statement)
			{
				function.ExitLocation = GetClosingBrace(statement);
				lower.LowerBody(node, statement);
				lower.PruneEmptyScopes();
			}
			
			return function;
		}
		
		private static SourceLocation GetClosingBrace(IResolvedStatementNode body) =>
			body.Syntax is BlockStatementNode { SourceLocation: var location } && location.Range.Length > 0
				? location with { Range = new TextRange(location.Range.End - 1, location.Range.End) }
				: SourceLocation.None;
		
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
			var scope = BeginScope(node.Syntax.SourceLocation, true);
			var thenBlock = CreateBlock("then");
			var elseBlock = node.Else is null ? null : CreateBlock("else");
			var mergeBlock = CreateBlock("merge");
			var endsEarly = !BindsNames(node.Condition);
			var skipBlock = endsEarly && elseBlock is null ? CreateBlock("skip") : null;
			
			LowerBranch(node.Condition, thenBlock, elseBlock ?? skipBlock ?? mergeBlock);
			if (endsEarly)
			{
				_activeScopes.Pop();
				EndScope(thenBlock, scope);
				if (elseBlock is not null)
					EndScope(elseBlock, scope);
				
				if (skipBlock is not null)
					ExitScope(skipBlock, scope, mergeBlock);
			}
			
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
			if (!endsEarly)
				EndCurrentScope();
		}
		
		private static bool BindsNames(IResolvedExpressionNode condition) => condition switch
		{
			ResolvedIsExpressionNode node => node.Pattern.AllBindings.Any(),
			ResolvedBinaryOpExpressionNode { Operation: NativeImpl { Op: TokenType.OpAmpersandAmpersand } } node =>
				BindsNames(node.Left) || BindsNames(node.Right),
			_ => false
		};
		
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
	}
}