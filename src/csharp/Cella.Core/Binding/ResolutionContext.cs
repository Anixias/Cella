using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public readonly struct ResolutionContext
{
	public FileSymbol File { get; init; }
	public TypeSymbol? ContainingType { get; init; }
	public FunctionInfo? ContainingFunction { get; init; }
	public ImportEnvironment? Imports { get; init; }
	public ModuleIndex? Modules { get; init; }
	public Scope? LocalScope { get; init; }
	public TypePool TypePool { get; init; }
	public DiagnosticList Diagnostics { get; init; }
	public ExtSignatureTypes? ExtSignatureTypes { get; init; }
	public Func<IExpressionNode, ResolutionContext, Constant?>? EvaluateConstant { get; init; }
	public Dictionary<IndexerExpressionNode, TypeSymbol?>? GenericTypes { get; init; }
	public ImmutableArray<TypeParameterSymbol> TypeParameters { get; init; }
	public TraitSymbol? Trait { get; init; }
	public ImplSymbol? ImplBlock { get; init; }
	
	public string Mangle(Symbol symbol) => Mangling.Mangle(symbol, Modules, GetQualifiers());
	
	public string Mangle(Symbol symbol, FunctionSignature signature) =>
		Mangling.Mangle(symbol, signature, Modules, GetQualifiers());
	
	private List<string> GetQualifiers()
	{
		var result = new List<string>();
		
		// TODO Nested functions in functions not supported
		for (var f = ContainingFunction; f is not null; f = f.Value.Symbol.ContainingFunction)
			result.Add(Mangling.Mangle(f.Value.Symbol, f.Value.Signature, Modules));
		
		for (var t = ContainingType; t is not null; t = t.ContainingType)
			result.Add(Mangling.MangleName(t, Modules));
		
		result.Reverse();
		
		var moduleParts = File.Module.ModuleName.Text.Split('.');
		result.InsertRange(0, moduleParts);
		return result;
	}
	
	private static Symbol? ResolveStatic(TypeSymbol type, string name) => ResolveFrom(name,
	[
		..type.GetFunctions(name)
			.Where(static function => !function.HasReceiver)
			.Select(static function => function.Function)
	]);
	
	private static Symbol? ResolveStatic(IEnumerable<MethodSymbol> functions, IEnumerable<PropertySymbol> properties,
		string name) =>
		properties.FirstOrDefault(p => p.Name == name && p.IsStatic) ?? ResolveFrom(name,
		[
			..functions
				.Where(function => function.Name == name && !function.HasReceiver)
				.Select(static function => function.Function)
		]);
	
	private static Symbol? ResolveFrom(string name, IReadOnlyCollection<Symbol> candidates) => candidates switch
	{
		{ Count: > 1 } => new AmbiguousSymbol(name, candidates),
		{ Count: 1 } => candidates.First(),
		_ => null
	};
	
	public Symbol? Resolve(string name)
	{
		if (ResolveNear(name) is { } symbol)
			return symbol;
		
		if (Imports?.Resolve(name) is { Length: > 0 } imports)
			return ResolveFrom(name, imports);
		
		return NativeSymbols.Resolve(name) ?? Modules?.Root.Children.GetValueOrDefault(name);
	}
	
	public Symbol? ResolveNear(string name)
	{
		// TODO None of this will work with function overloads :(
		
		if (LocalScope?.Resolve(name) is { } localSymbol)
			return localSymbol;
		
		if (ContainingFunction?.Symbol.Parameters.FirstOrDefault(p => p.Name == name) is { } param)
			return param;
		
		if (!TypeParameters.IsDefault && TypeParameters.FirstOrDefault(p => p.Name == name) is { } typeParameter)
			return typeParameter;
		
		if (Trait is { } trait && name == trait.Self.Name)
			return trait.Self;
		
		if (ImplBlock?.TypeParameters.FirstOrDefault(p => p.Name == name) is { } blockParameter)
			return blockParameter;
		
		if (ImplBlock is { } impl && ResolveStatic(impl.Functions, impl.Properties, name) is { } blockMember)
			return blockMember;
		
		if (Trait is { } owner && ResolveStatic(owner.Functions, owner.Properties, name) is { } traitMember)
			return traitMember;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
		{
			if (type.Children.TryGetValue(name, out var member))
				return member;
			
			if (type.GetStaticField(name) is { } field)
				return field;
			
			if (type.GetProperty(name) is { IsStatic: true } property)
				return property;
			
			if (ResolveStatic(type, name) is { } function)
				return function;
		}
		
		return ResolveFrom(name, [..GetModuleSymbols(name).Where(IsVisible)]);
	}
	
	private IEnumerable<Symbol> GetModuleSymbols(string name) =>
		File.Module.Files.SelectMany(file => file.Symbols.GetValueOrDefault(name) ?? []);
	
	public bool IsVisible(Symbol symbol) => Modules?.IsVisible(symbol, File) ?? true;
	
	public bool CanAccess(TypeSymbol owner, Visibility visibility) =>
		Modules?.IsAccessible(owner, visibility, File, ContainingType) ?? true;
	
	public Diagnostic? ReportHidden(string name, SourceLocation location)
	{
		foreach (var symbol in GetModuleSymbols(name))
		{
			if (!IsVisible(symbol))
				return DiagnosticReporter.ReportHidden(location, name, ModuleIndex.GetVisibility(symbol), false);
		}
		
		return null;
	}
	
	public Symbol? ResolveMember(ModulePathSymbol module, string name) =>
		module.Members.TryGetValue(name, out var members) &&
		ResolveFrom(name, [..members.Where(IsVisible)]) is { } member
			? member
			: module.Children.GetValueOrDefault(name);
	
	public Diagnostic ReportUndefinedMember(SourceLocation location, ModulePathSymbol module, string name)
	{
		if (module.Members.TryGetValue(name, out var members) && members.Count > 0)
			return DiagnosticReporter.ReportHidden(location, $"{module.Path}.{name}",
				members.Max(ModuleIndex.GetVisibility), false);
		
		var memberNames = new List<string>();
		foreach (var (memberName, symbols) in module.Members)
		{
			if (symbols.Any(IsVisible))
				memberNames.Add(memberName);
		}
		
		return DiagnosticReporter.ReportUndefinedMember(location, module, name, memberNames);
	}
	
	public ModulePathSymbol? ResolveModule(IExpressionNode node) => node switch
	{
		VarExpressionNode v => Resolve(v.Identifier.Text) as ModulePathSymbol,
		AccessExpressionNode a => ResolveModule(a.Target) is { } module
			? ResolveMember(module, a.Member.Text) as ModulePathSymbol
			: null,
		_ => null
	};
	
	public TraitSymbol? ResolveTrait(ITypeNode node)
	{
		var text = node.SourceLocation.GetText().ToString();
		Symbol? symbol;
		switch (node)
		{
			case IdentifierTypeNode identifier:
				symbol = Resolve(identifier.Token.Text);
				break;
			
			case QualifiedTypeNode qualified:
				symbol = ResolveQualifiedName(qualified.Parts);
				if (symbol is null)
					return null;
				
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{text}' is not a trait"));
				return null;
		}
		
		switch (symbol)
		{
			case TraitSymbol trait:
				return trait;
			
			case null:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
					$"Trait '{text}' not found in this scope"));
				
				return null;
			
			case AmbiguousSymbol:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{text}' is ambiguous"));
				return null;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{text}' is not a trait"));
				return null;
		}
	}
	
	public Symbol? ResolveTypeName(ITypeNode node) => node switch
	{
		IdentifierTypeNode identifier => Resolve(identifier.Token.Text),
		QualifiedTypeNode qualified => ResolveQualifiedName(qualified.Parts),
		_ => null
	};
	
	public Symbol? ResolveQualifiedName(ImmutableArray<Token> parts)
	{
		var first = parts[0];
		if (Resolve(first.Text) is { } symbol)
			return ResolveMembers(symbol, parts, 1);
		
		Diagnostics.Add(DiagnosticReporter.ReportUndefinedModule(first.SourceLocation, first.Text,
			GetAllSymbols().OfType<ModulePathSymbol>().Select(static m => m.Name).Distinct()));
		
		return null;
	}
	
	public Symbol? ResolveMembers(Symbol symbol, ImmutableArray<Token> parts, int start)
	{
		for (var i = start; i < parts.Length; i++)
		{
			if (symbol is not ModulePathSymbol module)
			{
				Diagnostics.Add(ReportNotModule(symbol, parts[..i]));
				return null;
			}
			
			if (ResolveMember(module, parts[i].Text) is not { } member)
			{
				Diagnostics.Add(ReportUndefinedMember(parts[i].SourceLocation, module, parts[i].Text));
				return null;
			}
			
			symbol = member;
		}
		
		return symbol;
	}
	
	public static Diagnostic ReportNotModule(Symbol symbol, ImmutableArray<Token> path)
	{
		var name = string.Join('.', path.Select(static p => p.Text));
		var problem = symbol is AmbiguousSymbol ? "is ambiguous" : "is not a module";
		return new(DiagnosticSeverity.Error, GetSpan(path), $"'{name}' {problem}");
	}
	
	public static SourceLocation GetSpan(ImmutableArray<Token> parts)
	{
		var (source, range) = parts[0].SourceLocation;
		return new(source, range.Join(parts[^1].SourceLocation.Range));
	}
	
	public IEnumerable<Symbol> GetAllSymbols()
	{
		// TODO None of this will work with function overloads :(
		
		for (var scope = LocalScope; scope is not null; scope = scope.Parent)
			foreach (var symbol in scope.Symbols)
				yield return symbol;
		
		if (ContainingFunction is { } function)
			foreach (var param in function.Symbol.Parameters)
				yield return param;
		
		if (!TypeParameters.IsDefault)
			foreach (var typeParameter in TypeParameters)
				yield return typeParameter;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
			foreach (var child in type.Children.Values)
				yield return child;
		
		foreach (var file in File.Module.Files)
			foreach (var fileSet in file.Symbols.Values)
				foreach (var fileSymbol in fileSet)
					if (IsVisible(fileSymbol))
						yield return fileSymbol;
		
		if (Imports is { } imports)
			foreach (var import in imports.ImportedSymbols)
				yield return import;
		
		foreach (var primitive in NativeSymbols.PrimitiveTypes)
			yield return primitive;
		
		if (Modules is { } modules)
			foreach (var module in modules.Root.Children.Values)
				yield return module;
	}
	
	public TypeSymbol ResolveType(ITypeNode node) => node switch
	{
		IdentifierTypeNode n => ResolveNamedType(n.Token),
		QualifiedTypeNode n => ResolveQualifiedType(n),
		GenericTypeNode n => ResolveGenericType(n),
		FunctionTypeNode n => ResolveFunctionType(n),
		BorrowTypeNode n => ResolveBorrowType(ResolveType(n.Target), n.IsMutable),
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol ResolveBorrowType(TypeSymbol target, bool isMutable) =>
		target is InvalidType ? target : TypePool.GetBorrowType(target, isMutable);
	
	private FunctionType ResolveFunctionType(FunctionTypeNode node)
	{
		var modes = node.ParameterModes.Select(SymbolCollector.GetMode).ToArray();
		var parameterTypes = new TypeSymbol[node.ParameterTypes.Length];
		for (var i = 0; i < parameterTypes.Length; i++)
		{
			var typeNode = node.ParameterTypes[i];
			if (typeNode is BorrowTypeNode borrow)
			{
				Diagnostics.Add(DiagnosticReporter.ReportBorrowParameter(borrow, null,
					node.ParameterModes[i] is not null, node.IsExternal));
				
				modes[i] = borrow.IsMutable ? ParameterMode.Mut : ParameterMode.ReadOnly;
				typeNode = borrow.Target;
			}
			
			parameterTypes[i] = TypePool.GetPassedType(ResolveType(typeNode), modes[i]);
		}
		
		var returnType = node.ReturnType is { } returnTypeNode ? ResolveType(returnTypeNode) : NativeSymbols.Void;
		var functionType = TypePool.GetFunctionType(node.IsExternal, parameterTypes, modes, returnType);
		if (functionType.IsExternal && ExtSignatureTypes is { } signatureTypes)
		{
			for (var i = 0; i < node.ParameterTypes.Length; i++)
				signatureTypes.Add(functionType.GetDeclaredType(i), node.ParameterTypes[i]);
			
			if (node.ReturnType is not null)
				signatureTypes.Add(returnType, node.ReturnType);
		}
		
		return functionType;
	}
	
	private TypeSymbol ResolveNamedType(Token name)
	{
		switch (Resolve(name.Text))
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic:
				Diagnostics.Add(ReportTypeArgumentCount(name.SourceLocation, generic));
				break;
			
			case TypeSymbol type:
				return type;
			
			case null:
				ReportUndefinedType(name);
				break;
			
			case AmbiguousSymbol:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{name.Text}' is ambiguous"));
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{name.Text}' is not a type"));
				break;
		}
		
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol ResolveQualifiedType(QualifiedTypeNode node)
	{
		var name = string.Join('.', node.Parts.Select(static p => p.Text));
		switch (ResolveQualifiedName(node.Parts))
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic:
				Diagnostics.Add(ReportTypeArgumentCount(node.SourceLocation, generic));
				break;
			
			case TypeSymbol type:
				return type;
			
			case null:
				break;
			
			case AmbiguousSymbol:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{name}' is ambiguous"));
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{name}' is not a type"));
				break;
		}
		
		return NativeSymbols.Invalid;
	}
	
	private void ReportUndefinedType(Token name) => Diagnostics.Add(ReportHidden(name.Text, name.SourceLocation) ??
	                                                                DiagnosticReporter.ReportUndefinedType(
		                                                                name.SourceLocation, name.Text,
		                                                                GetVisibleTypeNames()));
	
	private IEnumerable<string> GetVisibleTypeNames() => GetAllSymbols()
		.OfType<TypeSymbol>()
		.Select(static t => t.Name)
		.Distinct()
		.Order();
	
	public TypeSymbol? TryResolveExpressionAsType(IExpressionNode expression) => expression switch
	{
		VarExpressionNode v => Resolve(v.Identifier.Text) as TypeSymbol,
		IndexerExpressionNode i => TryResolveGenericType(i),
		AccessExpressionNode a => ResolveModule(a.Target) is { } module
			? ResolveMember(module, a.Member.Text) as TypeSymbol
			: null,
		BorrowExpressionNode { Value: ArrayExpressionNode { Values: [var element] } } b =>
			TryResolveExpressionAsType(element) is { } target ? ResolveBorrowType(target, b.IsMutable) : null,
		TypeExpressionNode t => ResolveType(t.Type),
		_ => null
	};
	
	private TypeSymbol? TryResolveGenericType(IndexerExpressionNode node)
	{
		if (GenericTypes?.TryGetValue(node, out var cached) == true)
			return cached;
		
		var result = ResolveGenericExpression(node);
		if (GenericTypes is { } genericTypes)
			genericTypes[node] = result;
		
		return result;
	}
	
	private TypeSymbol? ResolveGenericExpression(IndexerExpressionNode node)
	{
		var symbol = node.Target switch
		{
			VarExpressionNode variable => Resolve(variable.Identifier.Text),
			AccessExpressionNode access => ResolveModule(access.Target) is { } module
				? ResolveMember(module, access.Member.Text)
				: null,
			_ => null
		};
		
		if (symbol is NamedTypeSymbol { IsGenericDefinition: true } definition)
			return InstantiateType(definition, [..node.Arguments.Select(ResolveTypeExpression)],
				[..node.Arguments.Select(static argument => argument.SourceLocation)], node.SourceLocation);
		
		if (node.Target is not VarExpressionNode target ||
		    Resolve(target.Identifier.Text) is not (null or TypeSymbol) ||
		    !TypePool.BuiltinGenericTypeArguments.ContainsKey(target.Identifier.Text))
			return null;
		
		var typeArgs = new List<IGenericArgument>(node.Arguments.Length);
		foreach (var argument in node.Arguments)
		{
			if (TryResolveExpressionAsType(argument) is { } typeArg)
			{
				typeArgs.Add(new GenericTypeArgument(typeArg));
				continue;
			}
			
			if (EvaluateLength(argument) is not { } length)
				return NativeSymbols.Invalid;
			
			typeArgs.Add(new GenericConstArgument(length));
		}
		
		return TypePool.ResolveBuiltinGenericType(target.Identifier.Text, typeArgs);
	}
	
	public static Diagnostic ReportTypeArgumentCount(SourceLocation location, NamedTypeSymbol generic) =>
		ReportTypeArgumentCount(location, generic.Name, generic.TypeParameters.Length);
	
	public static Diagnostic ReportTypeArgumentCount(SourceLocation location, string name, int count) =>
		new(DiagnosticSeverity.Error, location, $"'{name}' takes {DescribeTypeArguments(count)}");
	
	private static string DescribeTypeArguments(int count) => count switch
	{
		0 => "no type arguments",
		1 => "one type argument",
		_ => $"{count} type arguments"
	};
	
	private TypeSymbol InstantiateType(NamedTypeSymbol definition, IReadOnlyList<TypeSymbol> arguments,
		IReadOnlyList<SourceLocation> locations, SourceLocation location)
	{
		if (arguments.Count != definition.TypeParameters.Length)
		{
			Diagnostics.Add(ReportTypeArgumentCount(location, definition));
			return NativeSymbols.Invalid;
		}
		
		var typePool = TypePool;
		var violations = definition.TypeParameters
			.Select((parameter, i) => (Message: typePool.FindConstraintViolation(parameter, arguments[i]), Index: i))
			.Where(static pair => pair.Message is not null)
			.ToList();
		
		foreach (var (message, index) in violations)
			Diagnostics.Add(new(DiagnosticSeverity.Error, locations[index], message!));
		
		return violations.Count > 0 ? NativeSymbols.Invalid : TypePool.Instantiate(definition, [..arguments]);
	}
	
	public TypeSymbol ResolveTypeExpression(IExpressionNode expression)
	{
		switch (TryResolveExpressionAsType(expression))
		{
			case NamedTypeSymbol { IsGenericDefinition: true } generic when expression is not IndexerExpressionNode:
				Diagnostics.Add(ReportTypeArgumentCount(expression.SourceLocation, generic));
				return NativeSymbols.Invalid;
			
			case { } type:
				return type;
			
			default:
				Report(expression, $"'{expression.SourceLocation.GetText()}' is not a type");
				return NativeSymbols.Invalid;
		}
	}
	
	private TypeSymbol ResolveGenericTypeArgument(IGenericArgumentNode node) => node switch
	{
		TypeArgumentNode argument => ResolveType(argument.Type),
		IdentifierArgumentNode argument => ResolveNamedType(argument.Identifier),
		ExpressionArgumentNode argument => ResolveTypeExpression(argument.Expression),
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol? ResolveTypeArgument(IGenericArgumentNode node) => node switch
	{
		TypeArgumentNode t => ResolveType(t.Type),
		IdentifierArgumentNode i => Resolve(i.Identifier.Text) as TypeSymbol,
		ExpressionArgumentNode e => TryResolveExpressionAsType(e.Expression),
		_ => null
	};
	
	private BigInteger? ResolveConstIntArgument(IGenericArgumentNode node) => node switch
	{
		ExpressionArgumentNode e => EvaluateLength(e.Expression),
		IdentifierArgumentNode i => ResolveConstIntIdentifier(i.Identifier),
		_ => null
	};
	
	private BigInteger? ResolveConstIntIdentifier(Token identifier)
	{
		if (Resolve(identifier.Text) is not null)
			return EvaluateLength(new VarExpressionNode(identifier));
		
		ReportUndefinedType(identifier);
		return null;
	}
	
	private BigInteger? EvaluateLength(IExpressionNode expression)
	{
		switch (EvaluateConstant?.Invoke(expression, this))
		{
			case InvalidConstant:
				return null;
			
			case IntegerConstant { Value.Sign: >= 0 } length:
				return length.Value;
			
			case IntegerConstant:
				Report(expression, "Array length can't be negative");
				return null;
			
			case null:
				Report(expression, expression is VarExpressionNode name
					? $"'{name.Identifier.Text}' is not a type or a constant"
					: "Array length must be a constant");
				
				return null;
			
			default:
				Report(expression, "Array length must be an integer");
				return null;
		}
	}
	
	private void Report(IExpressionNode node, string message) =>
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, message));
	
	private TypeSymbol ResolveGenericType(GenericTypeNode node)
	{
		var name = node.Identifier;
		if (!node.Qualifiers.IsEmpty ||
		    !TypePool.BuiltinGenericTypeArguments.TryGetValue(name.Text, out var expectedArguments))
			return ResolveUserGenericType(node);
		
		var typeArgs = new List<IGenericArgument>(node.Arguments.Length);
		
		foreach (var arg in node.Arguments)
		{
			if (ResolveTypeArgument(arg) is { } typeArg)
			{
				if (typeArg == NativeSymbols.Invalid)
					return NativeSymbols.Invalid;
				
				typeArgs.Add(new GenericTypeArgument(typeArg));
				continue;
			}
			
			if (ResolveConstIntArgument(arg) is not { } constVal)
				return NativeSymbols.Invalid;
			
			typeArgs.Add(new GenericConstArgument(constVal));
		}
		
		if (TypePool.ResolveBuiltinGenericType(name.Text, typeArgs.ToArray()) is { } type)
			return type;
		
		Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation, $"'{name.Text}' takes {expectedArguments}"));
		return NativeSymbols.Invalid;
	}
	
	private TypeSymbol ResolveUserGenericType(GenericTypeNode node)
	{
		var name = node.Identifier;
		var symbol = node.Qualifiers.IsEmpty ? Resolve(name.Text) : ResolveQualifiedName([..node.Qualifiers, name]);
		switch (symbol)
		{
			case NamedTypeSymbol { IsGenericDefinition: true } definition:
				return InstantiateType(definition, [..node.Arguments.Select(ResolveGenericTypeArgument)],
					[..node.Arguments.Select(static argument => argument.SourceLocation)], node.SourceLocation);
			
			case TypeSymbol type:
				Diagnostics.Add(new(DiagnosticSeverity.Error, node.SourceLocation,
					$"'{type.Name}' takes no type arguments"));
				
				break;
			
			case null when node.Qualifiers.IsEmpty:
				Diagnostics.Add(ReportHidden(name.Text, name.SourceLocation) ??
				                DiagnosticReporter.ReportUndefinedType(name.SourceLocation, name.Text,
					                GetVisibleTypeNames().Concat(TypePool.BuiltinGenericTypeArguments.Keys)));
				
				break;
			
			case null:
				break;
			
			default:
				Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{name.Text}' is not a type"));
				break;
		}
		
		return NativeSymbols.Invalid;
	}
}