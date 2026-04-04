using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes.Declarations;

namespace Cella.Core.Binding;

public sealed class SignatureCollector : IDeclarationNodeVisitor
{
	private readonly string? _entryPointName;
	private readonly SymbolTable _symbolTable;
	private readonly ImmutableArray<AssemblySymbol> _dependencies;
	private readonly SignatureTable.Builder _builder = new();
	private readonly Stack<ResolutionContext> _resolutionContexts = [];
	private ResolutionContext CurrentResolutionContext => _resolutionContexts.Peek();
	private readonly List<FunctionInfo> _entryPoints = [];
	
	public SignatureCollector(string? entryPointName, SymbolTable symbolTable, IEnumerable<AssemblySymbol> dependencies)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
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
			Imports = imports
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
		
		// TODO Parameters (resolve types and define in scope^)
		
		// TODO Optional return type -- NativeSymbols.Void
		if (resolutionContext.Resolve(node.ReturnType.GetText()) is not TypeSymbol returnType)
			returnType = new InvalidType();
		
		var signature = new FunctionSignature([], returnType);
		
		// TODO Disable mangling if indicated
		FunctionInfo info;
		if (_entryPointName is not null && function.Name == _entryPointName && IsEntryPoint(signature))
		{
			info = new FunctionInfo(null, function, signature, scope);
			_entryPoints.Add(info);
		}
		else
		{
			var mangledName = Mangling.Mangle(function, signature, resolutionContext.GetQualifiers());
			info = new FunctionInfo(mangledName, function, signature, scope);
		}
		
		_builder.Functions[function] = info;
	}
	
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
				if (symbols.TryGetValue(i.Token.GetText(), out var symbol))
					yield return symbol;
				
				break;
			}
			
			case ListImport i:
			{
				foreach (var token in i.Tokens)
				{
					if (symbols.TryGetValue(token.GetText(), out var symbol))
						yield return symbol;
				}
				
				break;
			}
			
			case FullImport:
			{
				foreach (var symbol in symbols.Values)
					yield return symbol;
				
				break;
			}
		}
	}
}