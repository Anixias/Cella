using Cella.Core.Binding.Nodes.Declarations;
using Cella.Core.Binding.Nodes.Expressions;
using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Symbols;

namespace Cella.Core.Lowering;

public sealed class Lowerer : IResolvedDeclarationNodeVisitor
{
	public IReadOnlyCollection<LoweredModule> Modules => _modules.Values;
	
	private readonly Dictionary<ModuleSymbol, LoweredModule> _modules = [];
	private readonly Dictionary<FunctionSymbol, LoweredFunction> _functions = [];
	private readonly Stack<LoweredModule> _moduleStack = [];
	private LoweredModule CurrentModule => _moduleStack.Peek();
	
	public void Lower(IResolvedDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IResolvedDeclarationNode node) => ((IResolvedDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(ResolvedFileNode node)
	{
		if (!_modules.TryGetValue(node.ModuleSymbol, out var loweredModule))
		{
			loweredModule = new(node.ModuleSymbol);
			_modules[node.ModuleSymbol] = loweredModule;
		}
		
		_moduleStack.Push(loweredModule);
		
		foreach (var declaration in node.Declarations)
			VisitNode(declaration);
		
		_moduleStack.Pop();
	}
	
	// TODO Probably want to mangle names here
	public void Visit(ResolvedFunctionNode node)
	{
		var function = FunctionLowerer.Lower(node);
		_functions[node.FunctionSymbol] = function;
		CurrentModule.Functions.Add(function);
	}
	
	private sealed class FunctionLowerer : IResolvedStatementNodeVisitor, IResolvedExpressionNodeVisitor<Value?>
	{
		private readonly LoweredFunction _function;
		private BasicBlock currentBlock;
		
		private BasicBlock CreateBlock(string hint)
		{
			var block = new BasicBlock($"{hint}_{_function.Blocks.Count}");
			_function.Blocks.Add(block);
			return block;
		}
		
		private FunctionLowerer(LoweredFunction function)
		{
			_function = function;
			currentBlock = CreateBlock("entry");
		}
		
		public static LoweredFunction Lower(ResolvedFunctionNode node)
		{
			var function = new LoweredFunction(node.FunctionSymbol);
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
			
			// Collapse empty blocks
			for (var i = function.Blocks.Count - 1; i >= 0; i--)
			{
				var block = function.Blocks[i];
				
				// If the block has no instructions and an undefined terminator, it wasn't actually used
				if (block.Instructions.Count == 0 && block.Terminator == UndefinedTerminator.Instance)
					function.Blocks.RemoveAt(i);
			}
			
			// TODO Warn about unreachable code & remove
			
			return function;
		}
		
		private void VisitNode(IResolvedStatementNode node) => ((IResolvedStatementNodeVisitor)this).Visit(node);
		
		private Value? VisitNode(IResolvedExpressionNode node) =>
			((IResolvedExpressionNodeVisitor<Value?>)this).Visit(node);
		
		public void Visit(ResolvedBlockStatementNode node)
		{
			foreach (var statement in node.Statements)
				VisitNode(statement);
		}
		
		public void Visit(ResolvedReturnStatementNode node)
		{
			var value = node.Expression is null ? null : VisitNode(node.Expression);
			currentBlock.Terminator = ReturnTerminator.FromValue(value);
			
			// We don't want to add further instructions to this terminated block, so create a dummy block
			currentBlock = CreateBlock("unreachable");
		}
		
		public Value Visit(ResolvedLiteralExpressionNode node) => new ConstantValue(node.Type, node.Value);
	}
}

