using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding;

public sealed class SymbolCollector : IDeclarationNodeVisitor<Symbol>
{
	private readonly SymbolTable.Builder _builder = new();
	private readonly List<Symbol> _symbolsInFile = [];
	
	public SymbolTable Build() => _builder.Build();
	
	public void Collect(IDeclarationNode root) => VisitNode(root);
	
	private Symbol VisitNode(IDeclarationNode node) => ((IDeclarationNodeVisitor<Symbol>)this).Visit(node);
	
	public Symbol Visit(FileNode node)
	{
		var moduleName = node.ModuleName;
		
		if (_builder.ModuleSymbols.GetValueOrDefault(moduleName) is not { } module)
		{
			module = new(moduleName, node.ModuleName.SourceLocation);
			_builder.ModuleSymbols[moduleName] = module;
		}
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		var symbol = new FileSymbol(node, module, _symbolsInFile);
		_builder.DeclarationSymbols[node] = symbol;
		_builder.SymbolsByModule.GetOrAdd(module).UnionWith(_symbolsInFile);
		_symbolsInFile.Clear();
		return symbol;
	}
	
	public Symbol Visit(FunctionNode node)
	{
		// TODO Disallow multiple parameters with the same name
		var parameters = new List<ParameterSymbol>(node.Parameters.Length);
		foreach (var param in node.Parameters)
		{
			var paramSymbol = new ParameterSymbol(param.Identifier);
			parameters.Add(paramSymbol);
			_builder.DeclarationSymbols[param] = paramSymbol;
		}
		
		// TODO Containing function
		var name = node.Identifier.Text;
		var function = new FunctionSymbol(name, node, null, parameters);
		_builder.DeclarationSymbols[node] = function;
		_symbolsInFile.Add(function);
		return function;
	}
	
	public Symbol Visit(ExternalFunctionNode node)
	{
		// TODO Disallow multiple parameters with the same name
		var parameters = new List<ParameterSymbol>(node.Parameters.Length);
		foreach (var param in node.Parameters)
		{
			var paramSymbol = new ParameterSymbol(param.Identifier);
			parameters.Add(paramSymbol);
			_builder.DeclarationSymbols[param] = paramSymbol;
		}
		
		var name = node.Identifier.Text;
		var function = new FunctionSymbol(name, node, null, parameters);
		_builder.DeclarationSymbols[node] = function;
		_symbolsInFile.Add(function);
		return function;
	}
	
	public Symbol Visit(ParameterNode node) => throw new InvalidOperationException();
	
	public Symbol Visit(RecordNode node)
	{
		// TODO Type parameters
		var members = new List<MemberSymbol>(node.Members.Length);
		var types = new List<TypeSymbol>(node.Members.Length);
		
		foreach (var member in node.Members)
		{
			var m = VisitNode(member);
			switch (m)
			{
				case FieldSymbol s:
					members.Add(s);
					break;
				
				case FunctionSymbol s:
					members.Add(new MethodSymbol(s, SelfReferenceKind.None)); // TODO Self reference
					break;
				
				case TypeSymbol s:
					types.Add(s);
					break;
				
				default:
					// TODO Diagnostics?
					break;
			}
		}
		
		var name = node.Identifier.Text;
		var record = new RecordSymbol(name, node, members, types);
		_builder.DeclarationSymbols[node] = record;
		_symbolsInFile.Add(record);
		return record;
	}
	
	public Symbol Visit(FieldNode node)
	{
		var name = node.Identifier.Text;
		var field = new FieldSymbol(name, node, true); // TODO Immutable fields
		_builder.DeclarationSymbols[node] = field;
		_symbolsInFile.Add(field);
		return field;
	}
}