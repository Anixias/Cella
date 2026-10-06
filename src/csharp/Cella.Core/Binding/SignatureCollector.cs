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
	private readonly ExtSignatureTypes _extSignatureTypes;
	private IConstantResolver? constants;
	
	public DiagnosticList Diagnostics { get; } = new();
	public ConstantEvaluator Evaluator { get; }
	public ModuleIndex Modules { get; }
	
	public SignatureCollector(string? entryPointName, SymbolTable symbolTable, TypePool typePool,
		IEnumerable<AssemblySymbol> dependencies, uint pointerBitSize)
	{
		_entryPointName = entryPointName;
		_symbolTable = symbolTable;
		_typePool = typePool;
		_extSignatureTypes = new(typePool);
		_dependencies = dependencies.ToImmutableArray();
		_dependencyTable = SignatureTable.Combine(_dependencies.Select(static a => a.SignatureTable));
		Evaluator = new ConstantEvaluator(typePool, pointerBitSize, GetGlobalValue);
		Modules = new ModuleIndex(symbolTable, _dependencies.Select(static a => a.SymbolTable));
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
		ReportModuleConflicts();
		ReportLayoutCycles();
		ReportEntryPoint();
		ReportPlainEnumsInExtSignatures();
		ReportDestructorsInExtSignatures();
		
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
		
		if (declaration.Node is ConstructorNode or DestructorNode)
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
		ReportHiddenType(node.Type, type, global.Visibility, global.Name);
		if (node.IsMutable && !IsScalar(type))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Type.SourceLocation,
				"Module variables must be numbers, 'bool' or 'char'"));
		
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
		var mangledName = context.Mangle(global);
		
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
		var context = new ResolutionContext
		{
			File = file,
			Modules = Modules,
			TypePool = _typePool,
			Diagnostics = Diagnostics,
			ExtSignatureTypes = _extSignatureTypes,
			EvaluateConstant = EvaluateConstant
		};
		
		var imports = CollectImports(node, context);
		_builder.ImportEnvironments[file] = imports;
		context = context with { Imports = imports };
		
		foreach (var declaration in node.Declarations)
		{
			var symbol = _symbolTable.DeclarationSymbols[declaration];
			_declarations[symbol] = new(declaration, context);
			if (symbol is EnumSymbol { HasPayload: false } enumType)
				_typePool.RegisterEnumConversions(enumType);
			
			IEnumerable<IDeclarationNode> members = declaration switch
			{
				RecordNode record => record.Members,
				EnumNode enumNode => enumNode.Functions,
				_ => []
			};
			
			var memberContext = context with { ContainingType = symbol as TypeSymbol };
			foreach (var member in members)
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
		var destructors = node.Members.OfType<DestructorNode>().Select(static d => d.Keyword);
		Diagnostics.AddRange(DiagnosticReporter.ReportDuplicates(destructors,
			name => $"'{name}' is declared more than once in '{record.Name}'"));
		
		foreach (var member in node.Members)
		{
			var symbol = _symbolTable.DeclarationSymbols[member];
			var context = _declarations[symbol].Context;
			
			switch (member)
			{
				case FieldNode field:
					var fieldSymbol = (FieldSymbol)symbol;
					var fieldType = context.ResolveType(field.Type);
					_typePool.RegisterMember(record, fieldSymbol, fieldType);
					ReportHiddenType(field.Type, fieldType, GetEffectiveVisibility(fieldSymbol.Visibility, record),
						field.Identifier.Text);
					
					if (!node.IsRef && _typePool.HoldsBorrows(fieldType))
						Diagnostics.Add(ReportStoredBorrow(field.Type, node.Identifier, node.Modifiers, "records",
							"rec"));
					
					break;
				
				case ConstructorNode constructor:
					CollectConstructor((FunctionSymbol)symbol, constructor, context);
					break;
				
				case DestructorNode:
					CollectDestructor((FunctionSymbol)symbol, context);
					break;
				
				default:
					Complete(symbol);
					break;
			}
		}
		
		var functions = node.Members.OfType<FunctionNode>().ToLookup(static f => f.Identifier.Type == TokenType.OpStar);
		ReportMemberConflicts(record, [
			..node.Members.OfType<FieldNode>().Select(static f => (f.Identifier, (FunctionNode?)null, true)),
			..functions[false].Select(static f => (f.Identifier, (FunctionNode?)f, false))
		]);
		
		ReportDereferences(record, [..functions[true]]);
		_typePool.RegisterRecord(record);
		_completedTypes.Add(record);
		Exit();
	}
	
	private void ReportDereferences(RecordSymbol record, List<FunctionNode> dereferences)
	{
		var valid = new List<(FunctionNode Node, FunctionSignature Signature)>();
		foreach (var dereference in dereferences)
		{
			var signature = _builder.Functions[(FunctionSymbol)_symbolTable.DeclarationSymbols[dereference]].Signature;
			if (signature.ReturnType is InvalidType)
				continue;
			
			if (FindDereferenceError(dereference, signature) is var (location, message))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, message));
			else
				valid.Add((dereference, signature));
		}
		
		foreach (var sameMode in valid.GroupBy(static d => d.Signature.GetMode(0)).Where(static g => g.Count() > 1))
		{
			var message = $"'*' with '{DescribeReceiver(sameMode.Key)}' is declared more than once in '{record.Name}'";
			Diagnostics.AddRange(sameMode.Select(d =>
				new Diagnostic(DiagnosticSeverity.Error, d.Node.Identifier.SourceLocation, message)));
		}
		
		if (valid.Select(static d => ((BorrowType)d.Signature.ReturnType).Target).Distinct().Count() > 1)
			Diagnostics.AddRange(valid.Select(static d => new Diagnostic(DiagnosticSeverity.Error,
				d.Node.ReturnType!.SourceLocation, "Cannot declare '*' operators with different target types")));
	}
	
	private static (SourceLocation Location, string Message)? FindDereferenceError(FunctionNode node,
		FunctionSignature signature) => node switch
	{
		{ Receiver: null } => (node.Identifier.SourceLocation, "Cannot declare '*' operators without 'self'"),
		{ Receiver: { Mode: { Type: TokenType.KeywordOwn } } receiver } =>
			(receiver.SourceLocation, "Cannot take 'own self' in '*' operators"),
		{ Parameters: [var parameter, ..] } => (parameter.SourceLocation, "Cannot take parameters in '*' operators"),
		_ when signature.ReturnType is BorrowType { IsMutable: var isMutable } &&
		       isMutable == (signature.GetMode(0) == ParameterMode.Mut) => null,
		_ => (node.ReturnType?.SourceLocation ?? node.Identifier.SourceLocation,
			$"Cannot return '{signature.ReturnType.Name}' from '*' with '{DescribeReceiver(signature.GetMode(0))}'")
	};
	
	private static string DescribeReceiver(ParameterMode mode) => mode == ParameterMode.Mut ? "mut self" : "self";
	
	private void ReportMemberConflicts(TypeSymbol type,
		IEnumerable<(Token Name, FunctionNode? Function, bool IsField)> members)
	{
		var sameNames = members
			.GroupBy(static member => member.Name.Text)
			.Where(static sameName => sameName.Count() > 1);
		
		foreach (var sameName in sameNames)
		{
			if (sameName.All(static member => member.Function is not null))
			{
				var functions = sameName.Select(static member => member.Function!).ToList();
				Diagnostics.AddRange(functions
					.Select(function => FindConflict(function, functions))
					.OfType<Diagnostic>());
				
				continue;
			}
			
			if (sameName.All(static member => member is { Function: null, IsField: false }))
				continue;
			
			var message = sameName.All(static member => member.IsField)
				? $"Field '{sameName.Key}' is declared more than once in '{type.Name}'"
				: $"'{sameName.Key}' is declared more than once in '{type.Name}'";
			
			Diagnostics.AddRange(sameName.Select(member =>
				new Diagnostic(DiagnosticSeverity.Error, member.Name.SourceLocation, message)));
		}
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
			{
				_typePool.RegisterPayloadField(enumCase.Fields[i], types[i]);
				ReportHiddenType(enumCase.Fields[i].Node!.Type, types[i], enumType.Visibility, enumType.Name);
				if (!node.IsRef && _typePool.HoldsBorrows(types[i]))
					Diagnostics.Add(ReportStoredBorrow(enumCase.Fields[i].Node!.Type, node.Identifier, node.Modifiers,
						"enums", "enum"));
			}
			
			payloads[enumCase] = types;
		}
		
		var values = CollectCaseValues(enumType, context);
		var tagType = GetTagType(enumType, context, values);
		ReportSharedValues(enumType, values, payloads);
		if (!enumType.IsExternal && !node.Cases.IsEmpty && !values.Contains(BigInteger.Zero))
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.Identifier.SourceLocation,
				$"'{enumType.Name}' needs a case with the value 0"));
		
		_typePool.RegisterEnum(enumType, tagType, values);
		_completedTypes.Add(enumType);
		Exit();
		
		foreach (var function in node.Functions)
			Complete(_symbolTable.DeclarationSymbols[function]);
		
		ReportMemberConflicts(enumType, [
			..node.Cases.Select(static c => (c.Identifier, (FunctionNode?)null, false)),
			..node.Functions.Select(static f => (f.Identifier, (FunctionNode?)f, false))
		]);
	}
	
	private void ReportHiddenType(ITypeNode node, TypeSymbol type, Visibility visibility, string name)
	{
		if (FindHiddenType(type, visibility) is { } hidden)
			Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
				$"'{hidden.Name}' is less visible than '{name}'"));
	}
	
	private static TypeSymbol? FindHiddenType(TypeSymbol type, Visibility visibility) => type switch
	{
		PointerType pointer => FindHiddenType(pointer.BaseType, visibility),
		BorrowType borrow => FindHiddenType(borrow.Target, visibility),
		ArrayType array => FindHiddenType(array.ElementType, visibility),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(part => FindHiddenType(part, visibility))
			.FirstOrDefault(static hidden => hidden is not null),
		IExportable exportable when exportable.Visibility < visibility => type,
		_ => null
	};
	
	private static Visibility GetEffectiveVisibility(Visibility member, TypeSymbol owner)
	{
		var ownerVisibility = ModuleIndex.GetVisibility(owner);
		return member < Visibility.Project && member < ownerVisibility ? member : ownerVisibility;
	}
	
	private static Visibility GetEffectiveVisibility(FunctionSymbol function, ResolutionContext context) =>
		context.ContainingType is { } owner ? GetEffectiveVisibility(function.Visibility, owner) : function.Visibility;
	
	private static Diagnostic ReportStoredBorrow(ITypeNode type, Token name, IEnumerable<Token> modifiers,
		string kinds, string keyword)
	{
		var prefix = string.Concat(modifiers.Select(static modifier => $"{modifier.Text} "));
		return new(DiagnosticSeverity.Error, type.SourceLocation, $"Cannot store borrows in plain {kinds}")
		{
			Hints = [$"Did you mean '{name.Text}: {prefix}ref {keyword}'?"]
		};
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
		var declared = enumType.Node.TagType is { } typeNode ? ResolveTagType(typeNode, context) : null;
		var tagType = declared ?? (enumType.IsExternal ? NativeSymbols.Int32 : null);
		if (tagType is null)
			return SmallestTagType(values);
		
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
	
	private IntegerType? ResolveTagType(ITypeNode typeNode, ResolutionContext context)
	{
		var type = context.ResolveType(typeNode);
		if (type is IntegerType tagType && NativeSymbols.PureIntegerTypes.Contains(tagType))
			return tagType;
		
		if (type is not InvalidType)
			Diagnostics.Add(new(DiagnosticSeverity.Error, typeNode.SourceLocation,
				$"'{type.Name}' isn't an integer type"));
		
		return null;
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
		var visibility = GetEffectiveVisibility(function, context);
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, false);
		
		var scope = new Scope();
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length + 1);
		
		var selfSymbol = function.Parameters[0];
		var selfType = _typePool.GetPointerType(containingType);
		
		paramTypes.Add(selfType);
		_builder.VariableTypes[selfSymbol] = selfType;
		scope.Define(selfSymbol);
		
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + 1]; // + 1 due to implicit self parameter
			var paramType = _typePool.GetPassedType(context.ResolveType(param.Type), paramSymbol.Mode);
			ReportHiddenType(param.Type, paramType, visibility, node.Keyword.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		var signature = new FunctionSignature(paramTypes, NativeSymbols.Void, false, GetModes(function));
		var mangledName = context.Mangle(function, signature);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		
		_builder.Functions[function] = info;
		_typePool.AddConstructor(containingType, info);
	}
	
	private void CollectDestructor(FunctionSymbol function, ResolutionContext context)
	{
		var self = function.Parameters[0];
		var selfType = _typePool.GetPointerType(context.ContainingType!);
		_builder.VariableTypes[self] = selfType;
		
		var scope = new Scope();
		scope.Define(self);
		
		var signature = new FunctionSignature([selfType], NativeSymbols.Void, false, GetModes(function));
		var mangledName = context.Mangle(function, signature);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		_builder.Functions[function] = info;
		_typePool.SetDestructor(context.ContainingType!, info);
	}
	
	private FunctionInfo CollectFunction(FunctionSymbol function, FunctionNode node, ResolutionContext context)
	{
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, node.IsExternal);
		
		var scope = new Scope();
		
		var paramTypes = new List<TypeSymbol>(function.Parameters.Length);
		if (node.Receiver is { } receiver)
		{
			var self = function.Parameters[0];
			var selfType = context.ContainingType is { } owner
				? _typePool.GetPassedType(owner, self.Mode)
				: NativeSymbols.Invalid;
			
			if (context.ContainingType is null)
				Diagnostics.Add(new(DiagnosticSeverity.Error, receiver.SourceLocation,
					"Cannot take 'self' outside types"));
			
			paramTypes.Add(selfType);
			_builder.VariableTypes[self] = selfType;
			scope.Define(self);
		}
		
		var visibility = GetEffectiveVisibility(function, context);
		var offset = paramTypes.Count;
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i + offset];
			var paramType = _typePool.GetPassedType(context.ResolveType(param.Type), paramSymbol.Mode);
			ReportHiddenType(param.Type, paramType, visibility, node.Identifier.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
			scope.Define(paramSymbol);
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = context.ResolveType(returnTypeSyntax);
		
		if (node.ReturnType is { } returnTypeNode)
			ReportHiddenType(returnTypeNode, returnType, visibility, node.Identifier.Text);
		
		var signature = new FunctionSignature(paramTypes, returnType, false, GetModes(function));
		
		// TODO Disable mangling if indicated
		var mangledName = context.Mangle(function, signature);
		var info = new FunctionInfo(mangledName, function, signature, scope, null, context.File);
		
		if (_entryPointName is not null && function.Name == _entryPointName && IsEntryPoint(signature))
			_entryPoints.Add(info);
		
		return info;
	}
	
	private FunctionInfo CollectExternalFunction(FunctionSymbol function, ExternalFunctionNode node,
		ResolutionContext context)
	{
		ReportDuplicateParameters(node.Parameters);
		ReportBorrowParameters(node.Parameters, true);
		
		var paramTypes = new List<TypeSymbol>(node.Parameters.Length);
		for (var i = 0; i < node.Parameters.Length; i++)
		{
			var param = node.Parameters[i];
			var paramSymbol = function.Parameters[i];
			var paramType = _typePool.GetPassedType(context.ResolveType(param.Type), paramSymbol.Mode);
			ReportHiddenType(param.Type, paramType, function.Visibility, node.Identifier.Text);
			
			paramTypes.Add(paramType);
			_builder.VariableTypes[paramSymbol] = paramType;
		}
		
		TypeSymbol returnType;
		if (node.ReturnType is not { } returnTypeSyntax)
			returnType = NativeSymbols.Void;
		else
			returnType = context.ResolveType(returnTypeSyntax);
		
		if (node.ReturnType is { } returnTypeNode)
			ReportHiddenType(returnTypeNode, returnType, function.Visibility, node.Identifier.Text);
		
		var signature = new FunctionSignature(paramTypes, returnType, node.IsVariadic, GetModes(function));
		return new(null, function, signature, null, node.Origin, context.File);
	}
	
	private static FunctionInfo CreateInvalidInfo(FunctionSymbol function, Declaration declaration)
	{
		var parameterTypes = function.Parameters.Select(static _ => (TypeSymbol)NativeSymbols.Invalid);
		var signature = new FunctionSignature(parameterTypes, NativeSymbols.Invalid, false, GetModes(function));
		return new(null, function, signature, new Scope(), null, declaration.Context.File);
	}
	
	private static IEnumerable<ParameterMode> GetModes(FunctionSymbol function) =>
		function.Parameters.Select(static p => p.Mode);
	
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
	
	private void ReportBorrowParameters(IEnumerable<ParameterNode> parameters, bool isExternal)
	{
		foreach (var parameter in parameters)
		{
			if (parameter.Type is BorrowTypeNode borrow)
				Diagnostics.Add(DiagnosticReporter.ReportBorrowParameter(borrow, parameter.Identifier.Text,
					parameter.Mode is not null, isExternal));
		}
	}
	
	private void ReportDuplicateDeclarations()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var declarationsByName = module.Files
				.SelectMany(static file => file.Syntax.Declarations.Select(declaration => (file, declaration)))
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d.declaration))
				.Select(d => (d.file, d.declaration, symbol: _symbolTable.DeclarationSymbols[d.declaration]))
				.ToLookup(static d => d.symbol.Name);
			
			foreach (var sameName in declarationsByName)
			{
				foreach (var (file, declaration, symbol) in sameName)
				{
					var visibleTogether = sameName
						.Where(other => AreVisibleTogether(symbol, file, other.symbol, other.file))
						.Select(static other => other.declaration);
					
					if (FindConflict(declaration, visibleTogether) is { } diagnostic)
						Diagnostics.Add(diagnostic);
				}
			}
		}
	}
	
	private static bool AreVisibleTogether(Symbol first, FileSymbol firstFile, Symbol second, FileSymbol secondFile) =>
		firstFile.Module == secondFile.Module && (firstFile == secondFile ||
		                                          ModuleIndex.GetVisibility(first) != Visibility.Private ||
		                                          ModuleIndex.GetVisibility(second) != Visibility.Private);
	
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
			return new(DiagnosticSeverity.Error, location, "'ext' functions cannot be overloaded");
		
		var signature = _builder.Functions[function].Signature;
		var sameParameters = others
			.Select(other => _builder.Functions[(FunctionSymbol)other].Signature)
			.Where(other => HasSameParameters(other, signature))
			.ToList();
		
		if (sameParameters.Count == 0)
			return null;
		
		var sameModes = sameParameters
			.Where(other => other.ParameterModes.SequenceEqual(signature.ParameterModes))
			.ToList();
		
		var sameReturn = sameParameters.Any(other => other.ReturnType == signature.ReturnType);
		var message = (sameModes.Count > 0, sameReturn) switch
		{
			(true, _) when sameModes.Any(other => other.ReturnType == signature.ReturnType) =>
				$"'{symbol.Name}' is declared more than once with the same signature",
			(true, _) => $"'{symbol.Name}' overloads cannot differ only in return type",
			(false, true) => $"'{symbol.Name}' overloads cannot differ only in parameter modes",
			(false, false) => $"'{symbol.Name}' overloads cannot differ only in parameter modes and return type"
		};
		
		return new(DiagnosticSeverity.Error, location, message);
	}
	
	private void ReportModuleConflicts()
	{
		foreach (var module in _symbolTable.ModuleSymbols.Values)
		{
			var path = Modules.Find(module.Name)!;
			var declarations = module.Files
				.SelectMany(static f => f.Syntax.Declarations)
				.Where(d => _symbolTable.DeclarationSymbols.ContainsKey(d));
			
			foreach (var declaration in declarations)
			{
				var name = _symbolTable.DeclarationSymbols[declaration].Name;
				if (path.Children.ContainsKey(name))
					Diagnostics.Add(ReportModuleConflict(GetIdentifier(declaration).SourceLocation, path, name));
			}
			
			if (path.Parent is not { } parent || !parent.Members.ContainsKey(path.Name))
				continue;
			
			foreach (var file in module.Files)
				Diagnostics.Add(ReportModuleConflict(file.Syntax.ModuleName.SourceLocation, parent, path.Name));
		}
	}
	
	private static Diagnostic ReportModuleConflict(SourceLocation location, ModulePathSymbol parent, string name) =>
		new(DiagnosticSeverity.Error, location,
			$"'{parent.Path}.{name}' is both a module and a member of '{parent.Path}'");
	
	private static bool HasSameParameters(FunctionSignature first, FunctionSignature second) =>
		first.IsVariadic == second.IsVariadic && first.ParameterTypes.Length == second.ParameterTypes.Length &&
		Enumerable.Range(0, first.ParameterTypes.Length)
			.All(i => first.GetDeclaredType(i) == second.GetDeclaredType(i));
	
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
			var reportedAsDuplicates = _entryPoints.All(entryPoint => _entryPoints.Any(other => other != entryPoint &&
				AreVisibleTogether(entryPoint.Symbol, entryPoint.File, other.Symbol, other.File)));
			
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
	
	private void ReportPlainEnumsInExtSignatures()
	{
		foreach (var (type, node) in GetExtSignatureTypes())
			ReportPlainEnum(type, node);
	}
	
	private void ReportDestructorsInExtSignatures()
	{
		foreach (var (type, node) in GetExtSignatureTypes())
			_extSignatureTypes.Add(type, node);
		
		_extSignatureTypes.ReportDestructors(Diagnostics);
	}
	
	private IEnumerable<(TypeSymbol Type, ITypeNode Node)> GetExtSignatureTypes()
	{
		foreach (var (function, info) in _builder.Functions)
		{
			if (!function.IsExternal)
				continue;
			
			var (parameters, returnType) = function.Syntax switch
			{
				FunctionNode node => (node.Parameters, node.ReturnType),
				ExternalFunctionNode node => (node.Parameters, node.ReturnType),
				_ => ([], null)
			};
			
			for (var i = 0; i < parameters.Length; i++)
				yield return (info.Signature.GetDeclaredType(i), parameters[i].Type);
			
			if (returnType is not null)
				yield return (info.Signature.ReturnType, returnType);
		}
	}
	
	private void ReportPlainEnum(TypeSymbol type, ITypeNode node)
	{
		if (!HasPlainEnum(type))
			return;
		
		var (located, location) = ExtSignatureTypes.Locate(type, node, HasPlainEnum);
		Diagnostics.Add(ExtSignatureTypes.Report(location, "Cannot use plain enums in 'ext' signatures", located,
			FindPlainEnum(located, [])!));
	}
	
	private bool HasPlainEnum(TypeSymbol type) => FindPlainEnum(type, []) is not null;
	
	private EnumSymbol? FindPlainEnum(TypeSymbol type, HashSet<TypeSymbol> visited)
	{
		if (type is EnumSymbol { IsExternal: false } plainEnum)
			return plainEnum;
		
		if (!visited.Add(type))
			return null;
		
		IEnumerable<TypeSymbol> parts = type switch
		{
			EnumSymbol enumType => enumType.Cases.SelectMany(c => _typePool.GetPayloadTypes(enumType, c)),
			RecordSymbol record => _typePool.GetMembers(record).OfType<FieldSymbol>().Select(_typePool.GetTypeOfMember),
			ArrayType array => [array.ElementType],
			FunctionType { IsExternal: true } function =>
				[..function.ParameterTypes.Select((_, i) => function.GetDeclaredType(i)), function.ReturnType],
			_ => []
		};
		
		return parts.Select(part => FindPlainEnum(part, visited)).FirstOrDefault(static found => found is not null);
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
	
	private ImportEnvironment CollectImports(FileNode node, ResolutionContext context)
	{
		var imports = new List<Symbol>();
		foreach (var importExpression in node.Imports)
			imports.AddRange(CollectImport(importExpression, context));
		
		return new(imports);
	}
	
	private List<Symbol> CollectImport(ImportExpression import, ResolutionContext context)
	{
		var path = import.ModuleName.Parts;
		var symbol = context.ResolveMembers(Modules.Root, path, 0);
		if (symbol is ModulePathSymbol module)
			return CollectImport(module, import.Import, context);
		
		if (symbol is not null)
			Diagnostics.Add(ResolutionContext.ReportNotModule(symbol, path));
		
		return [];
	}
	
	private List<Symbol> CollectImport(ModulePathSymbol module, IImport import, ResolutionContext context) =>
		import switch
		{
			FullImport => [..module.Members.Values.SelectMany(static members => members).Where(context.IsVisible)],
			TokenImport i => ImportMember(module, i.Token, context),
			ListImport i => [..i.Tokens.SelectMany(token => ImportMember(module, token, context))],
			_ => []
		};
	
	private List<Symbol> ImportMember(ModulePathSymbol module, Token name, ResolutionContext context)
	{
		if (module.Members.TryGetValue(name.Text, out var members) &&
		    members.Where(context.IsVisible).ToList() is { Count: > 0 } visible)
			return visible;
		
		if (module.Children.TryGetValue(name.Text, out var child))
			return [child];
		
		Diagnostics.Add(context.ReportUndefinedMember(name.SourceLocation, module, name.Text));
		return [];
	}
}