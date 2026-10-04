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
	public Func<IExpressionNode, ResolutionContext, Constant?>? EvaluateConstant { get; init; }
	
	public IEnumerable<string> GetQualifiers()
	{
		var result = new List<string>();
		
		// TODO Nested functions in functions not supported
		for (var f = ContainingFunction; f is not null; f = f.Value.Symbol.ContainingFunction)
			result.Add(Mangling.Mangle(f.Value.Symbol, f.Value.Signature));
		
		for (var t = ContainingType; t is not null; t = t.ContainingType)
			result.Add(Mangling.Mangle(t));
		
		result.Reverse();
		
		var moduleParts = File.Module.ModuleName.Text.Split('.');
		result.InsertRange(0, moduleParts);
		return result;
	}
	
	private static Symbol? ResolveFrom(string name, IReadOnlyCollection<Symbol> candidates) => candidates switch
	{
		{ Count: > 1 } => new AmbiguousSymbol(name, candidates),
		{ Count: 1 } => candidates.First(),
		_ => null
	};
	
	public Symbol? Resolve(string name)
	{
		// TODO None of this will work with function overloads :(
		
		if (LocalScope?.Resolve(name) is { } localSymbol)
			return localSymbol;
		
		// TODO Also check type parameters
		if (ContainingFunction?.Symbol.Parameters.FirstOrDefault(p => p.Name == name) is { } param)
			return param;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
			if (type.Children.TryGetValue(name, out var member))
				return member;
		
		foreach (var file in File.Module.Files)
			if (file.Symbols.TryGetValue(name, out var fileSet))
				return ResolveFrom(name, fileSet);
		
		if (Imports?.Resolve(name) is { Length: > 0 } imports)
			return ResolveFrom(name, imports);
		
		return NativeSymbols.Resolve(name) ?? Modules?.Root.Children.GetValueOrDefault(name);
	}
	
	public static Symbol? ResolveMember(ModulePathSymbol module, string name) =>
		module.Members.TryGetValue(name, out var members)
			? ResolveFrom(name, members)
			: module.Children.GetValueOrDefault(name);
	
	public ModulePathSymbol? ResolveModule(IExpressionNode node) => node switch
	{
		VarExpressionNode v => Resolve(v.Identifier.Text) as ModulePathSymbol,
		AccessExpressionNode a => ResolveModule(a.Target) is { } module
			? ResolveMember(module, a.Member.Text) as ModulePathSymbol
			: null,
		_ => null
	};
	
	public Symbol? ResolveQualifiedName(ImmutableArray<Token> parts)
	{
		var first = parts[0];
		if (Resolve(first.Text) is { } symbol)
			return ResolveMembers(symbol, parts, 1, Diagnostics);
		
		Diagnostics.Add(DiagnosticReporter.ReportUndefinedModule(first.SourceLocation, first.Text,
			GetAllSymbols().OfType<ModulePathSymbol>().Select(static m => m.Name).Distinct()));
		
		return null;
	}
	
	public static Symbol? ResolveMembers(Symbol symbol, ImmutableArray<Token> parts, int start,
		DiagnosticList diagnostics)
	{
		for (var i = start; i < parts.Length; i++)
		{
			if (symbol is not ModulePathSymbol module)
			{
				diagnostics.Add(ReportNotModule(symbol, parts[..i]));
				return null;
			}
			
			if (ResolveMember(module, parts[i].Text) is not { } member)
			{
				diagnostics.Add(DiagnosticReporter.ReportUndefinedMember(parts[i].SourceLocation, module,
					parts[i].Text));
				
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
		
		// TODO Also check type parameters
		if (ContainingFunction is { } function)
			foreach (var param in function.Symbol.Parameters)
				yield return param;
		
		for (var type = ContainingType; type is not null; type = type.ContainingType)
			foreach (var child in type.Children.Values)
				yield return child;
		
		foreach (var file in File.Module.Files)
			foreach (var fileSet in file.Symbols.Values)
				foreach (var fileSymbol in fileSet)
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
		FunctionTypeNode n => TypePool.GetFunctionType(n.IsExternal, n.ParameterTypes.Select(ResolveType),
			n.ReturnType is { } returnType ? ResolveType(returnType) : NativeSymbols.Void),
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol ResolveNamedType(Token name)
	{
		switch (Resolve(name.Text))
		{
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
	
	private void ReportUndefinedType(Token name) => Diagnostics.Add(
		DiagnosticReporter.ReportUndefinedType(name.SourceLocation, name.Text, GetVisibleTypeNames()));
	
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
		_ => null
	};
	
	private TypeSymbol? TryResolveGenericType(IndexerExpressionNode node)
	{
		// TODO AccessExpressionNode for module.GenericType[T]
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
		if (!TypePool.BuiltinGenericTypeArguments.TryGetValue(name.Text, out var expectedArguments))
		{
			Diagnostics.Add(DiagnosticReporter.ReportUndefinedType(name.SourceLocation, name.Text,
				TypePool.BuiltinGenericTypeArguments.Keys));
			
			return NativeSymbols.Invalid;
		}
		
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
}