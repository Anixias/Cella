using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;
using IDeclarationNodeVisitor = Cella.Core.Syntax.Nodes.Declarations.IDeclarationNodeVisitor;

namespace Cella.Core.Binding;

// TODO This currently collects all declarations in depth-first, but it needs to be breadth-first
public sealed class Collector : IDeclarationNodeVisitor
{
	public Scope GlobalScope { get; } = new();
	public IReadOnlyDictionary<IDeclarationNode, Scope> DeclarationScopes => _declarationScopes;
	public IReadOnlyDictionary<IDeclarationNode, Symbol> DeclarationSymbols => _declarationSymbols;
	
	private readonly Dictionary<IDeclarationNode, Scope> _declarationScopes = [];
	private readonly Dictionary<IDeclarationNode, Symbol> _declarationSymbols = [];
	private readonly Stack<Scope> _scopes = [];
	private Scope CurrentScope => _scopes.Peek();
	
	public Collector()
	{
		// Push global scope
		var globalScope = GlobalScope;
		_scopes.Push(globalScope);
		
		// Create native types
		CreateNativeTypes(globalScope);
	}
	
	private static void CreateNativeTypes(Scope scope)
	{
		// TEMP
		scope.Define(NativeSymbols.Int32);
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
		var module = new ModuleSymbol(node.ModuleIdentifier.GetText(), node.ModuleIdentifier.SourceLocation);
		CurrentScope.Define(module);
		
		_declarationScopes[node] = OpenScope();
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		CloseScope();
		
		_declarationSymbols[node] = module;
	}
	
	public void Visit(FunctionNode node)
	{
		_declarationScopes[node] = OpenScope();
		
		//foreach (var param in node.Parameters)
		
		CloseScope();
		
		var parentScope = CurrentScope;
		
		// TEMP ReturnType won't be a simple Token later, cannot naively resolve it
		var returnType = parentScope.Resolve(node.ReturnType.GetText()) as TypeSymbol;
		var function = new FunctionSymbol(node.Identifier.GetText(), [], returnType, node.SourceLocation);
		parentScope.Define(function);
		
		_declarationSymbols[node] = function;
	}
}