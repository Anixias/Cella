using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes.Declarations;

namespace Cella.Core.Binding;

public sealed class TypeCollector : IDeclarationNodeVisitor
{
	private readonly CollectorContext _context;
	private readonly Stack<Scope> _scopes = [];
	private Scope CurrentScope => _scopes.Peek();
	
	public TypeCollector(CollectorContext context)
	{
		_context = context;
		
		// Push global scope
		var globalScope = _context.GlobalScope;
		_scopes.Push(globalScope);
		
		// Create native types
		CreateNativeTypes(globalScope);
	}
	
	private static void CreateNativeTypes(Scope scope)
	{
		// TEMP
		scope.Define(NativeSymbols.Int32);
		scope.Define(NativeSymbols.Int64);
		scope.Define(NativeSymbols.Int128);
	}
	
	public void Collect(IDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IDeclarationNode node) => ((IDeclarationNodeVisitor)this).Visit(node);
	
	private Scope OpenScope()
	{
		var scope = CurrentScope.CreateChild();
		_scopes.Push(scope);
		return scope;
	}
	
	private Scope CloseScope() => _scopes.Pop();
	
	public void Visit(FileNode node)
	{
		var moduleName = node.ModuleIdentifier.GetText();
		
		// If module symbol already defined, reuse it
		if (CurrentScope.Resolve(moduleName) is not { } module)
		{
			var m = new ModuleSymbol(moduleName, node.ModuleIdentifier.SourceLocation);
			module = m;
			CurrentScope.Define(module);
			_context.ModuleScopes[m] = OpenScope();
		}
		else
			_scopes.Push(_context.ModuleScopes[(ModuleSymbol)module]);
		
		_context.DeclarationScopes[node] = CurrentScope;
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		CloseScope();
		
		_context.DeclarationSymbols[node] = module;
	}
	
	public void Visit(FunctionNode node)
	{
	}
}