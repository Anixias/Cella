using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes.Declarations;

namespace Cella.Core.Binding;

public sealed class SymbolCollector : IDeclarationNodeVisitor
{
	private readonly SymbolTable.Builder _builder = new();
	private readonly List<Symbol> _symbolsInFile = [];
	
	public SymbolTable Build() => _builder.Build();
	
	public void Collect(IDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IDeclarationNode node) => ((IDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(FileNode node)
	{
		var moduleName = node.ModuleName;
		
		if (_builder.ModuleSymbols.GetValueOrDefault(moduleName) is not { } module)
		{
			module = new(moduleName, node.ModuleName.SourceLocation);
			_builder.ModuleSymbols[moduleName] = module;
		}
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		_builder.DeclarationSymbols[node] = new FileSymbol(node, module, _symbolsInFile);
		_builder.SymbolsByModule.GetOrAdd(module).UnionWith(_symbolsInFile);
		_symbolsInFile.Clear();
	}
	
	public void Visit(FunctionNode node)
	{
		// TODO foreach (var param in node.Parameters)
		// Note: Parameters are created, but not defined until the function's body is created
		
		var name = node.Identifier.GetText();
		var function = new FunctionSymbol(name, node, null, []);
		_builder.DeclarationSymbols[node] = function;
		_symbolsInFile.Add(function);
	}
}