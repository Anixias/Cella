using System.Numerics;
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
	public Scope? LocalScope { get; init; }
	public TypePool TypePool { get; init; }
	public DiagnosticList Diagnostics { get; init; }
	
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
		
		return NativeSymbols.Resolve(name);
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
	}
	
	public TypeSymbol ResolveType(ITypeNode node) => node switch
	{
		IdentifierTypeNode n => ResolveNamedType(n.Token),
		GenericTypeNode n => ResolveGenericType(n),
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
	
	private void ReportUndefinedType(Token name) => Diagnostics.Add(
		DiagnosticReporter.ReportUndefinedType(name.SourceLocation, name.Text, GetVisibleTypeNames()));
	
	private IEnumerable<string> GetVisibleTypeNames() => GetAllSymbols()
		.OfType<TypeSymbol>()
		.Select(static t => t.Name)
		.Distinct()
		.Order();
	
	private TypeSymbol ResolveTypeArgument(IGenericArgumentNode node) => node switch
	{
		TypeArgumentNode t => ResolveType(t.Type),
		IdentifierArgumentNode i => Resolve(i.Identifier.Text) as TypeSymbol ?? NativeSymbols.Invalid,
		ExpressionArgumentNode e => TypePool.TryResolveExpressionAsType(e.Expression, Resolve)
		                            ?? NativeSymbols.Invalid,
		_ => NativeSymbols.Invalid
	};
	
	private BigInteger? ResolveConstIntArgument(IGenericArgumentNode node) => node switch
	{
		ExpressionArgumentNode e => ResolveConstIntExpression(e.Expression),
		IdentifierArgumentNode i => ResolveConstIntIdentifier(i.Identifier),
		_ => null
	};
	
	private BigInteger? ResolveConstIntIdentifier(Token identifier)
	{
		// TODO Named constants
		if (Resolve(identifier.Text) is null)
			ReportUndefinedType(identifier);
		else
			Diagnostics.Add(new(DiagnosticSeverity.Error, identifier.SourceLocation,
				$"'{identifier.Text}' is not a type or an integer literal"));
		
		return null;
	}
	
	// TODO Need to implement constant expression evaluation prior to resolution?
	// TODO Cannot handle negative integers
	private BigInteger? ResolveConstIntExpression(IExpressionNode node) => node switch
	{
		LiteralExpressionNode { Token.Type: TokenType.IntegerLiteral } e =>
			Scanner.TryParseInteger(e.Token.AsSpan(), out var value) ? value : null,
		
		_ => null
	};
	
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
			var typeArg = ResolveTypeArgument(arg);
			if (typeArg != NativeSymbols.Invalid)
			{
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