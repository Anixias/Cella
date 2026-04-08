using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding;

public sealed class SignatureCollector : IDeclarationNodeVisitor
{
	private readonly string? _entryPointName;
	private readonly SymbolTable _symbolTable;
	private readonly TypePool _typePool;
	private readonly ImmutableArray<AssemblySymbol> _dependencies;
	private readonly SignatureTable.Builder _builder = new();
	private readonly Stack<ResolutionContext> _resolutionContexts = [];
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private readonly List<FunctionInfo> _entryPoints = [];
	
	public SignatureCollector(string? entryPointName, SymbolTable symbolTable, TypePool typePool,
		IEnumerable<AssemblySymbol> dependencies)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
		_typePool = typePool;
		_dependencies = dependencies.ToImmutableArray();
	}
	
	// TODO Diagnostics: if (_entryPoints.Count > 1)
	public AssemblySymbol FinishAssembly(string name) =>
		new(name, _symbolTable, _builder.Build(), _entryPoints.FirstOrDefault());
	
	public void Collect(IDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IDeclarationNode node) => ((IDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = CollectImports(node);
		_builder.ImportEnvironments[file] = imports;
		
		var resolutionContext = new ResolutionContext
		{
			File = file,
			Imports = imports,
			TypePool = _typePool
		};
		
		_resolutionContexts.Push(resolutionContext);
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		_resolutionContexts.Pop();
	}
	
	public void Visit(FunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var resolutionContext = CurrentResolutionContext;
		
		var scope = new Scope();
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = resolutionContext.ResolveType(param.Type);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = resolutionContext.ResolveType(returnTypeSyntax);
		
		var signature = new FunctionSignature(paramTypes, returnType);
		
		// TODO Disable mangling if indicated
		FunctionInfo info;
		if (_entryPointName is not null && function.Name == _entryPointName && IsEntryPoint(signature))
		{
			info = new FunctionInfo(null, function, signature, scope, null);
			_entryPoints.Add(info);
		}
		else
		{
			var mangledName = Mangling.Mangle(function, signature, resolutionContext.GetQualifiers());
			info = new FunctionInfo(mangledName, function, signature, scope, null);
		}
		
		_builder.Functions[function] = info;
	}
	
	public void Visit(ExternalFunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var resolutionContext = CurrentResolutionContext;
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = resolutionContext.ResolveType(param.Type);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = resolutionContext.ResolveType(returnTypeSyntax);
		
		var syntax = (ExternalFunctionNode)function.Syntax;
		var signature = new FunctionSignature(paramTypes, returnType);
		_builder.Functions[function] = new(null, function, signature, null, syntax.Origin);
	}
	
	public void Visit(ParameterNode node) => throw new InvalidOperationException();
	
	private static bool IsEntryPoint(FunctionSignature signature)
	{
		// To be an entry point, it must return void or i32 and have either no parameters or take an array of strings
		// TODO Not complete; also, allow async returns?
		
		// TODO Do we allow other integer return types?
		var returnType = signature.ReturnType;
		if (returnType != NativeSymbols.Void && returnType != NativeSymbols.Int32)
			return false;
		
		// TODO Allow array of strings as parameter
		var paramTypes = signature.ParameterTypes;
		if (paramTypes.Length > 0)
			return false;
		
		return true;
	}
	
	private ImportEnvironment CollectImports(FileNode node)
	{
		var imports = new List<Symbol>();
		foreach (var importExpression in node.Imports)
		{
			imports.AddRange(CollectImportsFromSymbolTable(_symbolTable, true, importExpression));
			
			foreach (var assembly in _dependencies)
				imports.AddRange(CollectImportsFromSymbolTable(assembly.SymbolTable, false, importExpression));
		}
		
		return new(imports);
	}
	
	private IEnumerable<Symbol> CollectImportsFromSymbolTable(SymbolTable symbolTable, bool isLocal,
		ImportExpression importExpression)
	{
		// TODO Accessibility/visibility modifiers (if isLocal, we can see internal+)
		if (!symbolTable.ModuleSymbols.TryGetValue(importExpression.ModuleName, out var module))
			yield break;
		
		if (!symbolTable.SymbolsByModule.TryGetValue(module, out var symbols))
			yield break;
		
		switch (importExpression.Import)
		{
			case TokenImport i:
			{
				if (symbols.TryGetValue(i.Token.Text, out var symbol) && CanImport(symbol))
					yield return symbol;
				
				break;
			}
			
			case ListImport i:
			{
				foreach (var token in i.Tokens)
				{
					if (symbols.TryGetValue(token.Text, out var symbol) && CanImport(symbol))
						yield return symbol;
				}
				
				break;
			}
			
			case FullImport:
			{
				foreach (var symbol in symbols.Values)
					if (CanImport(symbol))
						yield return symbol;
				
				break;
			}
		}
		
		yield break;
		
		bool CanImport(Symbol symbol) => symbol is IExportable exportable && 
			(exportable.Visibility == Visibility.Public || isLocal);
	}
}