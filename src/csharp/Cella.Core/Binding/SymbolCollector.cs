using System.Collections.Immutable;
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
	private readonly Stack<PropertyNode> _properties = [];
	private readonly Stack<ImmutableArray<TypeParameterSymbol>> _typeParameters = [];
	
	private ImmutableArray<TypeParameterSymbol> CurrentTypeParameters =>
		_typeParameters.TryPeek(out var parameters) ? parameters : [];
	
	public SymbolTable Build() => _builder.Build();
	
	private static ImmutableArray<TypeParameterSymbol> CreateTypeParameters(
		ImmutableArray<TypeParameterNode> parameters) => parameters
		.Select(static parameter => new TypeParameterSymbol(parameter.Identifier, parameter.Constraint is not null))
		.ToImmutableArray();
	
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
			FunctionKind.Constructor)
		{
			TypeParameters = CurrentTypeParameters
		};
		
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
		
		var function = new FunctionSymbol(name, node, GetMemberVisibility(null), null, [self], FunctionKind.Destructor)
		{
			TypeParameters = CurrentTypeParameters
		};
		
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
		var isAccessor = _properties.TryPeek(out var property);
		var name = isAccessor ? $"{typeName}.{property!.Identifier.Text}.{node.Identifier.Text}"
			: isMember ? $"{typeName}.{node.Identifier.Text}"
			: node.Identifier.Text;
		
		var kind = node.Receiver is null ? FunctionKind.Free : FunctionKind.Method;
		var visibility = isMember
			? GetMemberVisibility(node.Visibility ?? property?.Visibility)
			: Visibility.FromKeyword(node.Visibility);
		
		var typeParameters = CreateTypeParameters(node.TypeParameters);
		var function = new FunctionSymbol(name, node, visibility, null, parameters, kind)
		{
			TypeParameters = CurrentTypeParameters.AddRange(typeParameters),
			DeclaredTypeParameters = typeParameters
		};
		
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
	
	public Symbol Visit(PropertyNode node)
	{
		_properties.Push(node);
		var accessors = node.Accessors.Select(accessor => (FunctionSymbol)VisitNode(accessor)).ToList();
		_properties.Pop();
		
		var property = new PropertySymbol(node.Identifier.Text)
		{
			Node = node,
			Getter = FindAccessor(accessors, TokenType.KeywordGet),
			Setter = FindAccessor(accessors, TokenType.KeywordSet),
			Visibility = GetMemberVisibility(node.Visibility)
		};
		
		foreach (var accessor in accessors)
			accessor.Property = property;
		
		_builder.DeclarationSymbols[node] = property;
		return property;
	}
	
	private static FunctionAccessor? FindAccessor(IEnumerable<FunctionSymbol> accessors, TokenType keyword) =>
		accessors.FirstOrDefault(accessor => ((FunctionNode)accessor.Syntax).Identifier.Type == keyword) is { } function
			? new(function)
			: null;
	
	private Visibility GetMemberVisibility(Token? keyword) => keyword is null && _typeVisibilities.TryPeek(out var type)
		? type switch
		{
			Visibility.Public => Visibility.Public,
			Visibility.Project => Visibility.Project,
			_ => Visibility.Module
		}
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
		var typeParameters = CreateTypeParameters(node.TypeParameters);
		_typeParameters.Push(typeParameters);
		var members = new List<MemberSymbol>(node.Members.Length);
		var statics = new List<GlobalSymbol>();
		var properties = new List<PropertySymbol>();
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
				
				case GlobalSymbol s:
					statics.Add(s);
					break;
				
				case PropertySymbol s:
					properties.Add(s);
					break;
				
				default:
					// TODO Diagnostics?
					break;
			}
		}
		
		var name = node.Identifier.Text;
		var record = new RecordSymbol(name, node, members, types, typeParameters)
		{
			StaticFields = [..statics],
			Properties = [..properties]
		};
		
		foreach (var field in statics)
			field.ContainingType = record;
		
		foreach (var property in properties)
			property.ContainingType = record;
		
		_builder.DeclarationSymbols[node] = record;
		_symbolsInFile.Add(record);
		_typeStack.Pop();
		_typeVisibilities.Pop();
		_typeParameters.Pop();
		
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
		var typeParameters = CreateTypeParameters(node.TypeParameters);
		_typeParameters.Push(typeParameters);
		var functions = new List<MethodSymbol>();
		var statics = new List<GlobalSymbol>();
		var properties = new List<PropertySymbol>();
		foreach (var member in node.Members)
		{
			switch (VisitNode(member))
			{
				case FunctionSymbol function:
					functions.Add(new MethodSymbol(GetMemberName(member), function));
					break;
				
				case GlobalSymbol global:
					statics.Add(global);
					break;
				
				case PropertySymbol property:
					properties.Add(property);
					break;
			}
		}
		
		_typeStack.Pop();
		_typeVisibilities.Pop();
		_typeParameters.Pop();
		var symbol = new EnumSymbol(node, cases, functions, typeParameters)
		{
			StaticFields = [..statics],
			Properties = [..properties]
		};
		
		foreach (var field in statics)
			field.ContainingType = symbol;
		
		foreach (var property in properties)
			property.ContainingType = symbol;
		
		_builder.DeclarationSymbols[node] = symbol;
		_symbolsInFile.Add(symbol);
		return symbol;
	}
	
	public Symbol Visit(GlobalNode node)
	{
		var isMember = _typeStack.Count > 0;
		var visibility = isMember ? GetMemberVisibility(node.Visibility) : Visibility.FromKeyword(node.Visibility);
		var global = new GlobalSymbol(node, visibility);
		_builder.DeclarationSymbols[node] = global;
		if (!isMember)
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