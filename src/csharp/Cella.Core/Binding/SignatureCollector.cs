using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

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
	
	public DiagnosticList Diagnostics { get; } = new();
	
	public SignatureCollector(string? entryPointName, SymbolTable symbolTable, TypePool typePool,
		IEnumerable<AssemblySymbol> dependencies)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
		_typePool = typePool;
		_dependencies = dependencies.ToImmutableArray();
	}
	
	public AssemblySymbol FinishAssembly(string name)
	{
		ReportDuplicateDeclarations();
		ReportRecordCycles();
		ReportEntryPoint();
		
		FunctionInfo? entryPoint = _entryPoints.Count == 1 ? _entryPoints[0] : null;
		return new(name, _symbolTable, _builder.Build(), entryPoint);
	}
	
	public void Collect(IDeclarationNode root) => VisitNode(root);
	
	private void VisitNode(IDeclarationNode node) => ((IDeclarationNodeVisitor)this).Visit(node);
	
	public void Visit(FieldNode node)
	{
		var field = (FieldSymbol)_symbolTable.DeclarationSymbols[node];
		
		var resolutionContext = CurrentResolutionContext;
		var fieldType = resolutionContext.ResolveType(node.Type);
		_typePool.RegisterMember(resolutionContext.ContainingType!, field, fieldType);
	}
	
	public void Visit(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = CollectImports(node);
		_builder.ImportEnvironments[file] = imports;
		
		var resolutionContext = new ResolutionContext
		{
			File = file,
			Imports = imports,
			TypePool = _typePool,
			Diagnostics = Diagnostics
		};
		
		_resolutionContexts.Push(resolutionContext);
		
		foreach (var child in node.Declarations)
			VisitNode(child);
		
		_resolutionContexts.Pop();
	}
	
	public void Visit(ConstructorNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var resolutionContext = CurrentResolutionContext;
		var containingType = resolutionContext.ContainingType!;
		ReportDuplicateParameters(node.Parameters);
		
		var scope = new Scope();
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length + 1);
		
		var selfSymbol = function.Parameters[0];
		var selfType = _typePool.GetPointerType(containingType, PointerKind.Mutable);
		
		paramTypes.Add(selfType);
		_builder.VariableTypes[selfSymbol] = selfType;
		scope.Define(selfSymbol);
		
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + 1]; // + 1 due to implicit self parameter
			var paramType = resolutionContext.ResolveType(param.Type);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		var signature = new FunctionSignature(paramTypes, NativeSymbols.Void);
		var mangledName = Mangling.Mangle(function, signature, resolutionContext.GetQualifiers());
		var info = new FunctionInfo(mangledName, function, signature, scope, null, resolutionContext.File);
		
		_builder.Functions[function] = info;
		_typePool.AddConstructor(containingType, info);
	}
	
	public void Visit(FunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var resolutionContext = CurrentResolutionContext;
		ReportDuplicateParameters(node.Parameters);
		
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
		var mangledName = Mangling.Mangle(function, signature, resolutionContext.GetQualifiers());
		var info = new FunctionInfo(mangledName, function, signature, scope, null, resolutionContext.File);
		
		if (_entryPointName is not null && function.Name == _entryPointName && IsEntryPoint(signature))
			_entryPoints.Add(info);
		
		_builder.Functions[function] = info;
	}
	
	public void Visit(ExternalFunctionNode node)
	{
		var function = (FunctionSymbol)_symbolTable.DeclarationSymbols[node];
		var resolutionContext = CurrentResolutionContext;
		ReportDuplicateParameters(node.Parameters);
		
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
		var signature = new FunctionSignature(paramTypes, returnType, syntax.IsVariadic);
		_builder.Functions[function] = new(null, function, signature, null, syntax.Origin, resolutionContext.File);
	}
	
	public void Visit(ParameterNode node) => throw new InvalidOperationException();
	
	public void Visit(RecordNode node)
	{
		var record = (RecordSymbol)_symbolTable.DeclarationSymbols[node];
		var fieldNames = node.Members.OfType<FieldNode>().Select(static f => f.Identifier);
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(fieldNames,
			name => $"Field '{name}' is declared more than once in '{record.Name}'"));
		
		var resolutionContext = CurrentResolutionContext with
		{
			ContainingType = record
		};
		
		_resolutionContexts.Push(resolutionContext);
		
		foreach (var member in node.Members)
			VisitNode(member);
		
		_resolutionContexts.Pop();
		_typePool.RegisterRecord(record);
	}
	
	private void ReportDuplicateParameters(IEnumerable<ParameterNode> parameters) =>
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(parameters.Select(static p => p.Identifier),
			static name => $"Parameter '{name}' is declared more than once"));
	
	private void ReportDuplicateDeclarations()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var declarationsByName = module.Files
				.SelectMany(static f => f.Syntax.Declarations)
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d))
				.ToLookup(d => _symbolTable.DeclarationSymbols[d].Name);
			
			foreach (var sameName in declarationsByName)
			{
				foreach (var declaration in sameName)
				{
					if (FindConflict(declaration, sameName) is { } diagnostic)
						Diagnostics.Add(diagnostic);
				}
			}
		}
	}
	
	private Diagnostic? FindConflict(IDeclarationNode declaration, IEnumerable<IDeclarationNode> sameName)
	{
		var symbol = _symbolTable.DeclarationSymbols[declaration];
		var others = sameName
			.Where(other => other != declaration)
			.Select(other => _symbolTable.DeclarationSymbols[other])
			.ToList();
		
		if (others.Count == 0)
			return null;
		
		var location = GetIdentifier(declaration).SourceLocation;
		if (symbol is not FunctionSymbol function || others.Any(static s => s is not FunctionSymbol))
			return new(DiagnosticSeverity.Error, location,
				$"'{symbol.Name}' is declared more than once in this module");
		
		var isExternal = function.Kind == FunctionKind.External;
		if (isExternal && others.Any(static s => s is FunctionSymbol { Kind: FunctionKind.External }))
		{
			return new(DiagnosticSeverity.Error, location,
				$"'{symbol.Name}' is declared more than once as an ext function")
			{
				Hints = ["Each ext function is one C symbol, so it can't be overloaded"]
			};
		}
		
		var signature = _builder.Functions[function].Signature;
		var sameParameters = others
			.Select(other => _builder.Functions[(FunctionSymbol)other].Signature)
			.Where(other => HasSameParameters(other, signature))
			.ToList();
		
		if (sameParameters.Count == 0)
			return null;
		
		var message = sameParameters.Any(other => other.ReturnType == signature.ReturnType)
			? $"'{symbol.Name}' is declared more than once with the same signature"
			: $"'{symbol.Name}' overloads cannot differ only in return type";
		
		return new(DiagnosticSeverity.Error, location, message);
	}
	
	private static bool HasSameParameters(FunctionSignature first, FunctionSignature second) =>
		first.IsVariadic == second.IsVariadic && first.ParameterTypes.SequenceEqual(second.ParameterTypes);
	
	private static Token GetIdentifier(IDeclarationNode declaration) => declaration switch
	{
		FunctionNode node => node.Identifier,
		ExternalFunctionNode node => node.Identifier,
		RecordNode node => node.Identifier,
		_ => throw new InvalidOperationException()
	};
	
	private void ReportEntryPoint()
	{
		if (_entryPointName is null || _entryPoints.Count == 1)
			return;
		
		if (_entryPoints.Count > 1)
		{
			var reportedAsDuplicates = _entryPoints.Select(static e => e.File.Module).Distinct().Count() == 1;
			if (reportedAsDuplicates)
				return;
			
			foreach (var entryPoint in _entryPoints)
				Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(entryPoint.Symbol.Syntax).SourceLocation,
					$"The program has more than one '{_entryPointName}' function"));
			
			return;
		}
		
		var candidates = _builder.Functions.Values
			.Where(f => f.Symbol is { Kind: FunctionKind.Free } && f.Symbol.Name == _entryPointName)
			.ToList();
		
		if (candidates.Count == 0)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, SourceLocation.None,
				$"The program has no '{_entryPointName}' function"));
			
			return;
		}
		
		foreach (var candidate in candidates)
			Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(candidate.Symbol.Syntax).SourceLocation,
				$"'{_entryPointName}' must have no parameters and return 'i32' or nothing"));
	}
	
	private void ReportRecordCycles()
	{
		foreach (var record in _symbolTable.DeclarationSymbols.Values.OfType<RecordSymbol>())
		{
			foreach (var field in _typePool.GetMembers(record).OfType<FieldSymbol>())
			{
				var fieldType = _typePool.GetTypeOfMember(field);
				if (!ContainsRecord(fieldType, record))
					continue;
				
				Diagnostics.Add(new(DiagnosticSeverity.Error, field.Node!.Type.SourceLocation,
					$"Field '{field.Name}' of type '{fieldType.Name}' causes a cycle in the memory layout"));
			}
		}
	}
	
	private bool ContainsRecord(TypeSymbol type, RecordSymbol record)
	{
		var visited = new HashSet<RecordSymbol>();
		var pending = new Stack<TypeSymbol>([type]);
		
		while (pending.TryPop(out var current))
		{
			if (GetContainedRecord(current) is not { } contained || !visited.Add(contained))
				continue;
			
			if (contained == record)
				return true;
			
			foreach (var field in _typePool.GetMembers(contained).OfType<FieldSymbol>())
				pending.Push(_typePool.GetTypeOfMember(field));
		}
		
		return false;
	}
	
	private static RecordSymbol? GetContainedRecord(TypeSymbol type) => type switch
	{
		RecordSymbol record => record,
		ArrayType array => GetContainedRecord(array.ElementType),
		_ => null
	};
	
	private static bool IsEntryPoint(FunctionSignature signature)
	{
		// To be an entry point, it must return void or i32 and have either no parameters or take an array of strings
		// TODO Not complete; also, allow async returns?
		
		// TODO Do we allow other integer return types?
		var returnType = signature.ReturnType;
		if (returnType != NativeSymbols.Void && returnType != NativeSymbols.Int32)
			return false;
		
		// TODO Allow view of strings as parameter
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
				if (symbols.TryGetValue(i.Token.Text, out var symbolSet))
					foreach (var symbol in symbolSet)
						if (CanImport(symbol))
							yield return symbol;
				
				break;
			}
			
			case ListImport i:
			{
				foreach (var token in i.Tokens)
				{
					if (!symbols.TryGetValue(token.Text, out var symbolSet))
						continue;
					
					foreach (var symbol in symbolSet)
						if (CanImport(symbol))
							yield return symbol;
				}
				
				break;
			}
			
			case FullImport:
			{
				foreach (var symbolSet in symbols.Values)
					foreach (var symbol in symbolSet)
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