using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class SymbolCollector : IDeclarationNodeVisitor<Symbol>
{
	private readonly SymbolTable.Builder _builder = new();
	private readonly List<Symbol> _symbolsInFile = [];
	private readonly Stack<string> _typeStack = [];
	private readonly Stack<Visibility> _typeVisibilities = [];
	
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
		module.Files.Add(symbol);
		_builder.DeclarationSymbols[node] = symbol;
		_builder.SymbolsByModule.GetOrAdd(module).UnionWith(_symbolsInFile);
		_symbolsInFile.Clear();
		return symbol;
	}
	
	public Symbol Visit(ConstructorNode node)
	{
		var parameters = new List<ParameterSymbol>(node.Parameters.Length + 1)
		{
			new("self", node.SourceLocation) { Mode = ParameterMode.Mut }
		};
		
		foreach (var param in node.Parameters)
		{
			var paramSymbol = new ParameterSymbol(param.Identifier) { Mode = GetMode(param.Mode) };
			parameters.Add(paramSymbol);
			_builder.DeclarationSymbols[param] = paramSymbol;
		}
		
		if (_typeStack.TryPeek(out var name))
			name += ".new";
		else
			name = ".new";
		
		// Do not add to file symbols
		var function = new FunctionSymbol(name, node, GetMemberVisibility(node.Visibility), null, parameters,
			FunctionKind.Constructor);
		
		_builder.DeclarationSymbols[node] = function;
		return function;
	}
	
	public Symbol Visit(DestructorNode node)
	{
		var self = new ParameterSymbol("self", node.SourceLocation) { Mode = ParameterMode.Mut };
		
		if (_typeStack.TryPeek(out var name))
			name += ".drop";
		else
			name = ".drop";
		
		var function = new FunctionSymbol(name, node, GetMemberVisibility(null), null, [self], FunctionKind.Destructor);
		_builder.DeclarationSymbols[node] = function;
		return function;
	}
	
	public Symbol Visit(FunctionNode node)
	{
		var parameters = new List<ParameterSymbol>(node.Parameters.Length + 1);
		if (node.Receiver is { } receiver)
			parameters.Add(new(receiver.Self) { Mode = GetMode(receiver.Mode) });
		
		foreach (var param in node.Parameters)
		{
			var paramSymbol = new ParameterSymbol(param.Identifier) { Mode = GetMode(param.Mode) };
			parameters.Add(paramSymbol);
			_builder.DeclarationSymbols[param] = paramSymbol;
		}
		
		// TODO Containing function
		var isMember = _typeStack.TryPeek(out var typeName);
		var name = isMember ? $"{typeName}.{node.Identifier.Text}" : node.Identifier.Text;
		var kind = node.Receiver is null ? FunctionKind.Free : FunctionKind.Method;
		var visibility = isMember ? GetMemberVisibility(node.Visibility) : Visibility.FromKeyword(node.Visibility);
		var function = new FunctionSymbol(name, node, visibility, null, parameters, kind);
		_builder.DeclarationSymbols[node] = function;
		if (!isMember)
			_symbolsInFile.Add(function);
		
		return function;
	}
	
	public Symbol Visit(ExternalFunctionNode node)
	{
		var parameters = new List<ParameterSymbol>(node.Parameters.Length);
		foreach (var param in node.Parameters)
		{
			var paramSymbol = new ParameterSymbol(param.Identifier) { Mode = GetMode(param.Mode) };
			parameters.Add(paramSymbol);
			_builder.DeclarationSymbols[param] = paramSymbol;
		}
		
		var name = node.Identifier.Text;
		var function = new FunctionSymbol(name, node, Visibility.FromKeyword(node.Visibility), null, parameters,
			FunctionKind.External);
		
		_builder.DeclarationSymbols[node] = function;
		_symbolsInFile.Add(function);
		return function;
	}
	
	public Symbol Visit(ParameterNode node) => throw new InvalidOperationException();
	
	private Visibility GetMemberVisibility(Token? keyword) =>
		keyword is null && _typeVisibilities.TryPeek(out var visibility) && visibility == Visibility.Public
			? Visibility.Public
			: Visibility.FromKeyword(keyword);
	
	public static ParameterMode GetMode(Token? keyword) => keyword?.Type switch
	{
		TokenType.KeywordMut => ParameterMode.Mut,
		TokenType.KeywordOwn => ParameterMode.Own,
		_ => ParameterMode.ReadOnly
	};
	
	public Symbol Visit(RecordNode node)
	{
		_typeStack.Push(node.Identifier.Text);
		_typeVisibilities.Push(Visibility.FromKeyword(node.Visibility));
		
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
					members.Add(new MethodSymbol(GetMemberName(member), s));
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
		_typeStack.Pop();
		_typeVisibilities.Pop();
		
		return record;
	}
	
	private static string GetMemberName(IDeclarationNode member) => member switch
	{
		FunctionNode function => function.Identifier.Text,
		ConstructorNode constructor => constructor.Keyword.Text,
		DestructorNode destructor => destructor.Keyword.Text,
		_ => throw new InvalidOperationException()
	};
	
	public Symbol Visit(EnumNode node)
	{
		var cases = node.Cases.Select((e, i) =>
			new EnumCaseSymbol(e, i, e.Payload.Select(f => (FieldSymbol)VisitNode(f))));
		
		_typeStack.Push(node.Identifier.Text);
		_typeVisibilities.Push(Visibility.FromKeyword(node.Visibility));
		var functions = node.Functions
			.Select(function => new MethodSymbol(function.Identifier.Text, (FunctionSymbol)VisitNode(function)))
			.ToList();
		
		_typeStack.Pop();
		_typeVisibilities.Pop();
		var symbol = new EnumSymbol(node, cases, functions);
		_builder.DeclarationSymbols[node] = symbol;
		_symbolsInFile.Add(symbol);
		return symbol;
	}
	
	public Symbol Visit(GlobalNode node)
	{
		var global = new GlobalSymbol(node);
		_builder.DeclarationSymbols[node] = global;
		_symbolsInFile.Add(global);
		return global;
	}
	
	public Symbol Visit(FieldNode node)
	{
		var name = node.Identifier.Text;
		var field = new FieldSymbol(name, node, true); // TODO Immutable fields
		_builder.DeclarationSymbols[node] = field;
		return field;
	}
}