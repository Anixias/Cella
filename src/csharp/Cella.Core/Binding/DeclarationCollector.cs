using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes.Declarations;

namespace Cella.Core.Binding;

public sealed class DeclarationCollector(CollectorContext context) : IDeclarationNodeVisitor
{
	private readonly Stack<Scope> _scopes = [];
	private Scope CurrentScope => _scopes.Peek();
	
	private Scope GetScope(IDeclarationNode node) => context.DeclarationScopes[node];
	
	private Scope OpenNodeScope(IDeclarationNode node)
	{
		var scope = GetScope(node);
		_scopes.Push(scope);
		return scope;
	}
	
	private Scope CloseScope() => _scopes.Pop();
	
	private Scope OpenScope()
	{
		var scope = CurrentScope.CreateChild();
		_scopes.Push(scope);
		return scope;
	}
	
	public void Collect(IDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IDeclarationNode node) => ((IDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(FileNode node)
	{
		OpenNodeScope(node);
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		CloseScope();
	}
	
	public void Visit(FunctionNode node)
	{
		context.DeclarationScopes[node] = OpenScope();
		
		//foreach (var param in node.Parameters)
		
		CloseScope();
		
		var parentScope = CurrentScope;
		
		// TEMP ReturnType won't be a simple Token later, cannot naively resolve it
		var name = node.Identifier.GetText();
		var returnType = parentScope.Resolve(node.ReturnType.GetText()) as TypeSymbol;
		var function = new FunctionSymbol(name, [], returnType, node.SourceLocation);
		parentScope.Define(function);
		
		context.DeclarationSymbols[node] = function;
	}
}