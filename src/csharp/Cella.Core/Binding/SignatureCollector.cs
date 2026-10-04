using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public interface IConstantResolver
{
	IResolvedExpressionNode ResolveInitializer(IExpressionNode initializer, TypeSymbol type, ResolutionContext context);
	Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context);
}

public sealed class SignatureCollector
{
	private readonly record struct Declaration(IDeclarationNode Node, ResolutionContext Context);
	
	private readonly string? _entryPointName;
	private readonly SymbolTable _symbolTable;
	private readonly TypePool _typePool;
	private readonly ImmutableArray<AssemblySymbol> _dependencies;
	private readonly SignatureTable _dependencyTable;
	private readonly SignatureTable.Builder _builder = new();
	private readonly Dictionary<Symbol, Declaration> _declarations = [];
	private readonly Dictionary<GlobalSymbol, TypeSymbol> _globalTypes = [];
	private readonly HashSet<TypeSymbol> _completedTypes = [];
	private readonly List<(Symbol Symbol, bool IsValue)> _inProgress = [];
	private readonly HashSet<Symbol> _cyclic = [];
	private readonly List<FunctionInfo> _entryPoints = [];
	private IConstantResolver? constants;
	
	public DiagnosticList Diagnostics { get; } = new();
	public ConstantEvaluator Evaluator { get; }
	
	public SignatureCollector(string? entryPointName, SymbolTable symbolTable, TypePool typePool,
		IEnumerable<AssemblySymbol> dependencies, uint pointerBitSize)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
		_typePool = typePool;
		_dependencies = dependencies.ToImmutableArray();
		_dependencyTable = SignatureTable.Combine(_dependencies.Select(static a => a.SignatureTable));
		Evaluator = new ConstantEvaluator(typePool, pointerBitSize, GetGlobalValue);
	}
	
	public void Collect(IReadOnlyCollection<FileNode> files, IConstantResolver constantResolver)
	{
		constants = constantResolver;
		_typePool.TypeCompleter = Complete;
		
		foreach (var file in files)
			Register(file);
		
		foreach (var file in files)
			foreach (var declaration in file.Declarations)
				Complete(_symbolTable.DeclarationSymbols[declaration]);
		
		_typePool.TypeCompleter = null;
	}
	
	public AssemblySymbol FinishAssembly(string name)
	{
		ReportDuplicateDeclarations();
		ReportLayoutCycles();
		ReportEntryPoint();
		
		FunctionInfo? entryPoint = _entryPoints.Count == 1 ? _entryPoints[0] : null;
		return new(name, _symbolTable, _builder.Build(), entryPoint);
	}
	
	public bool IsLocal(Symbol symbol) => _declarations.ContainsKey(symbol);
	
	public ImportEnvironment GetImports(FileSymbol file) => _builder.ImportEnvironments[file];
	
	public FunctionInfo GetFunctionInfo(FunctionSymbol function)
	{
		if (_builder.Functions.TryGetValue(function, out var info))
			return info;
		
		if (!_declarations.TryGetValue(function, out var declaration))
			return _dependencyTable.Functions[function];
		
		if (declaration.Node is ConstructorNode)
		{
			CompleteRecord((RecordSymbol)declaration.Context.ContainingType!);
			return _builder.Functions.TryGetValue(function, out info) ? info : CreateInvalidInfo(function, declaration);
		}
		
		if (!Enter(function, false))
			return CreateInvalidInfo(function, declaration);
		
		info = declaration.Node switch
		{
			FunctionNode node => CollectFunction(function, node, declaration.Context),
			ExternalFunctionNode node => CollectExternalFunction(function, node, declaration.Context),
			_ => throw new InvalidOperationException()
		};
		
		_builder.Functions[function] = info;
		Exit();
		return info;
	}
	
	public TypeSymbol GetVariableType(VariableSymbol variable)
	{
		if (variable is GlobalSymbol global)
			return GetGlobalType(global);
		
		return _builder.VariableTypes.TryGetValue(variable, out var type)
			? type
			: _dependencyTable.VariableTypes[variable];
	}
	
	public TypeSymbol GetGlobalType(GlobalSymbol global)
	{
		if (_globalTypes.TryGetValue(global, out var type))
			return type;
		
		if (!_declarations.TryGetValue(global, out var declaration))
			return _dependencyTable.Globals[global].Type;
		
		if (!Enter(global, false))
			return NativeSymbols.Invalid;
		
		var node = (GlobalNode)declaration.Node;
		type = declaration.Context.ResolveType(node.Type);
		if (node.IsMutable && !IsScalar(type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Type.SourceLocation,
				"A module 'var' must be an integer, a float, 'bool' or 'char'"));
		
		_globalTypes[global] = type;
		Exit();
		return type;
	}
	
	public GlobalInfo? GetGlobalInfo(GlobalSymbol global)
	{
		if (_builder.Globals.TryGetValue(global, out var info))
			return info;
		
		if (!_declarations.TryGetValue(global, out var declaration))
			return _dependencyTable.Globals[global];
		
		var type = GetGlobalType(global);
		if (!Enter(global, true))
			return null;
		
		var node = (GlobalNode)declaration.Node;
		var context = declaration.Context;
		var initializer = constants!.ResolveInitializer(node.Initializer, type, context);
		var value = type is InvalidType ? InvalidConstant.Instance : Evaluator.Evaluate(initializer);
		var mangledName = Mangling.Mangle(global, context.GetQualifiers());
		
		info = new GlobalInfo(mangledName, global, type, initializer, value, context.File);
		_builder.Globals[global] = info;
		Exit();
		return info;
	}
	
	private Constant GetGlobalValue(GlobalSymbol global) =>
		GetGlobalInfo(global) is { Value: { } value } ? value : InvalidConstant.Instance;
	
	private static bool IsScalar(TypeSymbol type) =>
		type is IntegerType or FloatType or PrimitiveType { Kind: PrimitiveTypeKind.Bool } or InvalidType;
	
	private void Register(FileNode node)
	{
		var file = (FileSymbol)_symbolTable.DeclarationSymbols[node];
		var imports = CollectImports(node);
		_builder.ImportEnvironments[file] = imports;
		
		var context = new ResolutionContext
		{
			File = file,
			Imports = imports,
			TypePool = _typePool,
			Diagnostics = Diagnostics,
			EvaluateConstant = EvaluateConstant
		};
		
		foreach (var declaration in node.Declarations)
		{
			var symbol = _symbolTable.DeclarationSymbols[declaration];
			_declarations[symbol] = new(declaration, context);
			if (symbol is EnumSymbol { HasPayload: false } enumType)
				_typePool.RegisterEnumConversions(enumType);
			
			if (declaration is not RecordNode record)
				continue;
			
			var memberContext = context with { ContainingType = (RecordSymbol)symbol };
			foreach (var member in record.Members)
				_declarations[_symbolTable.DeclarationSymbols[member]] = new(member, memberContext);
		}
	}
	
	private Constant? EvaluateConstant(IExpressionNode expression, ResolutionContext context) =>
		constants!.EvaluateConstant(expression, context);
	
	private void Complete(Symbol symbol)
	{
		switch (symbol)
		{
			case FunctionSymbol function:
				GetFunctionInfo(function);
				break;
			
			case RecordSymbol record:
				CompleteRecord(record);
				break;
			
			case EnumSymbol enumType:
				CompleteEnum(enumType);
				break;
			
			case GlobalSymbol global:
				GetGlobalInfo(global);
				break;
		}
	}
	
	private void CompleteRecord(RecordSymbol record)
	{
		if (_completedTypes.Contains(record) || !_declarations.TryGetValue(record, out var declaration) ||
		    !Enter(record, false))
			return;
		
		var node = (RecordNode)declaration.Node;
		var fieldNames = node.Members.OfType<FieldNode>().Select(static f => f.Identifier);
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(fieldNames,
			name => $"Field '{name}' is declared more than once in '{record.Name}'"));
		
		foreach (var member in node.Members)
		{
			var symbol = _symbolTable.DeclarationSymbols[member];
			var context = _declarations[symbol].Context;
			
			switch (member)
			{
				case FieldNode field:
					_typePool.RegisterMember(record, (FieldSymbol)symbol, context.ResolveType(field.Type));
					break;
				
				case ConstructorNode constructor:
					CollectConstructor((FunctionSymbol)symbol, constructor, context);
					break;
				
				default:
					Complete(symbol);
					break;
			}
		}
		
		_typePool.RegisterRecord(record);
		_completedTypes.Add(record);
		Exit();
	}
	
	private void CompleteEnum(EnumSymbol enumType)
	{
		if (_completedTypes.Contains(enumType) || !_declarations.TryGetValue(enumType, out var declaration) ||
		    !Enter(enumType, false))
			return;
		
		var node = enumType.Node;
		var context = declaration.Context;
		if (node.Cases.IsEmpty)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs at least one case"));
		
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(node.Cases.Select(static c => c.Identifier),
			name => $"Case '{name}' is declared more than once in '{enumType.Name}'"));
		
		var payloads = new Dictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>>();
		foreach (var enumCase in enumType.Cases)
		{
			Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(
				enumCase.Node.Payload.Select(static f => f.Identifier),
				name => $"Payload '{name}' is declared more than once in '{enumCase.Name}'"));
			
			ImmutableArray<TypeSymbol> types = [..enumCase.Fields.Select(f => context.ResolveType(f.Node!.Type))];
			for (var i = 0; i < types.Length; i++)
				_typePool.RegisterPayloadField(enumCase.Fields[i], types[i]);
			
			payloads[enumCase] = types;
		}
		
		var values = CollectCaseValues(enumType, context);
		var tagType = GetTagType(enumType, context, values);
		ReportSharedValues(enumType, values, payloads);
		if (!node.Cases.IsEmpty && !values.Contains(BigInteger.Zero))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs a case with the value 0"));
		
		_typePool.RegisterEnum(enumType, tagType, values);
		_completedTypes.Add(enumType);
		Exit();
	}
	
	private ImmutableArray<BigInteger> CollectCaseValues(EnumSymbol enumType, ResolutionContext context)
	{
		var scope = new Scope();
		var values = ImmutableArray.CreateBuilder<BigInteger>(enumType.Cases.Length);
		var next = BigInteger.Zero;
		foreach (var enumCase in enumType.Cases)
		{
			var value = enumCase.Node.Value is { } expression
				? EvaluateCaseValue(enumCase, expression, context with { LocalScope = scope }) ?? next
				: next;
			
			values.Add(value);
			scope.Define(new LocalVariableSymbol(enumCase.Node.Identifier, NativeSymbols.Int128, false)
			{
				ConstantValue = new IntegerConstant(NativeSymbols.Int128, value)
			});
			
			next = value + 1;
		}
		
		return values.MoveToImmutable();
	}
	
	private BigInteger? EvaluateCaseValue(EnumCaseSymbol enumCase, IExpressionNode expression,
		ResolutionContext context)
	{
		var value = constants!.ResolveInitializer(expression, NativeSymbols.Int128, context);
		switch (Evaluator.Evaluate(value))
		{
			case IntegerConstant constant:
				return constant.Value;
			
			case null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, expression.SourceLocation,
					$"The value of '{enumCase.Name}' must be a constant"));
				
				return null;
			
			default:
				return null;
		}
	}
	
	private IntegerType GetTagType(EnumSymbol enumType, ResolutionContext context, IReadOnlyList<BigInteger> values)
	{
		if (enumType.Node.TagType is not { } typeNode)
			return SmallestTagType(values);
		
		var type = context.ResolveType(typeNode);
		if (type is not IntegerType tagType || !NativeSymbols.PureIntegerTypes.Contains(tagType))
		{
			if (type is not InvalidType)
				Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation,
					$"'{type.Name}' isn't an integer type"));
			
			return SmallestTagType(values);
		}
		
		for (var i = 0; i < values.Count; i++)
		{
			if (Evaluator.Fits(values[i], tagType))
				continue;
			
			var enumCase = enumType.Cases[i];
			var location = enumCase.Node.Value?.SourceLocation ?? enumCase.Node.Identifier.SourceLocation;
			Diagnostics.Add(new(DiagnosticSeverity.Error, location,
				$"'{enumCase.Name}' is {values[i]}, which doesn't fit in '{tagType.Name}'"));
		}
		
		return tagType;
	}
	
	private IntegerType SmallestTagType(IReadOnlyList<BigInteger> values)
	{
		IntegerType[] candidates = values.Any(static value => value.Sign < 0)
			? [NativeSymbols.Int8, NativeSymbols.Int16, NativeSymbols.Int32, NativeSymbols.Int64]
			: [NativeSymbols.UInt8, NativeSymbols.UInt16, NativeSymbols.UInt32, NativeSymbols.UInt64];
		
		return candidates.FirstOrDefault(type => values.All(value => Evaluator.Fits(value, type)))
		       ?? NativeSymbols.Int128;
	}
	
	private void ReportSharedValues(EnumSymbol enumType, IReadOnlyList<BigInteger> values,
		IReadOnlyDictionary<EnumCaseSymbol, ImmutableArray<TypeSymbol>> payloads)
	{
		var groups = enumType.Cases
			.Select((enumCase, i) => (Case: enumCase, Value: values[i]))
			.GroupBy(static c => c.Value);
		
		foreach (var group in groups)
		{
			var shape = payloads[group.First().Case];
			if (group.All(c => payloads[c.Case].SequenceEqual(shape)))
				continue;
			
			var names = DiagnosticReporter.JoinNames([..group.Select(static c => c.Case.Name)]);
			foreach (var (enumCase, value) in group)
				Diagnostics.Add(new(DiagnosticSeverity.Error, enumCase.Node.Identifier.SourceLocation,
					$"{names} share the value {value} but have different payloads"));
		}
	}
	
	private void CollectConstructor(FunctionSymbol function, ConstructorNode node, ResolutionContext context)
	{
		var containingType = context.ContainingType!;
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
			var paramType = context.ResolveType(param.Type);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		var signature = new FunctionSignature(paramTypes, NativeSymbols.Void);
		var mangledName = Mangling.Mangle(function, signature, context.GetQualifiers());
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		
		_builder.Functions[function] = info;
		_typePool.AddConstructor(containingType, info);
	}
	
	private FunctionInfo CollectFunction(FunctionSymbol function, FunctionNode node, ResolutionContext context)
	{
		ReportDuplicateParameters(node.Parameters);
		
		var scope = new Scope();
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = context.ResolveType(param.Type);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = context.ResolveType(returnTypeSyntax);
		
		var signature = new FunctionSignature(paramTypes, returnType);
		
		// TODO Disable mangling if indicated
		var mangledName = Mangling.Mangle(function, signature, context.GetQualifiers());
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		
		if (_entryPointName is not null && function.Name == _entryPointName && IsEntryPoint(signature))
			_entryPoints.Add(info);
		
		return info;
	}
	
	private FunctionInfo CollectExternalFunction(FunctionSymbol function, ExternalFunctionNode node,
		ResolutionContext context)
	{
		ReportDuplicateParameters(node.Parameters);
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = context.ResolveType(param.Type);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = context.ResolveType(returnTypeSyntax);
		
		var signature = new FunctionSignature(paramTypes, returnType, node.IsVariadic);
		return new(null, function, signature, null, node.Origin, context.File);
	}
	
	private static FunctionInfo CreateInvalidInfo(FunctionSymbol function, Declaration declaration)
	{
		var parameterTypes = function.Parameters.Select(static _ => (TypeSymbol)NativeSymbols.Invalid);
		var signature = new FunctionSignature(parameterTypes, NativeSymbols.Invalid);
		return new(null, function, signature, new Scope(), null, declaration.Context.File);
	}
	
	private bool Enter(Symbol symbol, bool isValue)
	{
		var index = _inProgress.IndexOf((symbol, isValue));
		if (index < 0)
		{
			_inProgress.Add((symbol, isValue));
			return true;
		}
		
		foreach (var (member, _) in _inProgress.Skip(index))
			if (_cyclic.Add(member))
				ReportCycle(member);
		
		return false;
	}
	
	private void Exit() => _inProgress.RemoveAt(_inProgress.Count - 1);
	
	private void ReportCycle(Symbol symbol)
	{
		var (identifier, message) = symbol switch
		{
			GlobalSymbol global => (global.Syntax.Identifier, $"'{global.Name}' depends on its own value"),
			RecordSymbol record => (record.Node.Identifier, $"'{record.Name}' depends on its own layout"),
			EnumSymbol enumType => (enumType.Node.Identifier, $"'{enumType.Name}' depends on its own layout"),
			FunctionSymbol function => (GetIdentifier(function.Syntax),
				$"'{function.Name}' depends on its own signature"),
			_ => throw new InvalidOperationException()
		};
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, identifier.SourceLocation, message));
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
		EnumNode node => node.Identifier,
		GlobalNode node => node.Identifier,
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
	
	private void ReportLayoutCycles()
	{
		foreach (var type in _symbolTable.DeclarationSymbols.Values.OfType<TypeSymbol>())
		{
			foreach (var (field, fieldType) in GetStoredFields(type))
			{
				if (!ContainsType(fieldType, type))
					continue;
				
				var kind = type is EnumSymbol ? "Payload" : "Field";
				Diagnostics.Add(new(DiagnosticSeverity.Error, field.Node!.Type.SourceLocation,
					$"{kind} '{field.Name}' of type '{fieldType.Name}' causes a cycle in the memory layout"));
			}
		}
	}
	
	private bool ContainsType(TypeSymbol type, TypeSymbol target)
	{
		var visited = new HashSet<TypeSymbol>();
		var pending = new Stack<TypeSymbol>([type]);
		
		while (pending.TryPop(out var current))
		{
			if (GetStoredType(current) is not { } stored || !visited.Add(stored))
				continue;
			
			if (stored == target)
				return true;
			
			foreach (var (_, fieldType) in GetStoredFields(stored))
				pending.Push(fieldType);
		}
		
		return false;
	}
	
	private IEnumerable<(FieldSymbol Field, TypeSymbol Type)> GetStoredFields(TypeSymbol type)
	{
		IEnumerable<FieldSymbol> fields = type switch
		{
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>(),
			EnumSymbol enumType => enumType.Cases.SelectMany(static c => c.Fields),
			_ => []
		};
		
		foreach (var field in fields)
			if (_typePool.TryGetTypeOfMember(field, out var fieldType))
				yield return (field, fieldType);
	}
	
	private static TypeSymbol? GetStoredType(TypeSymbol type) => type switch
	{
		RecordSymbol or EnumSymbol => type,
		ArrayType array => GetStoredType(array.ElementType),
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