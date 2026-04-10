using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public readonly struct ResolutionContext
{
	public FileSymbol File { get; init; }
	public TypeSymbol? ContainingType { get; init; }
	public FunctionInfo? ContainingFunction { get; init; }
	public ImportEnvironment? Imports { get; init; }
	public Scope? LocalScope { get; init; }
	public TypePool TypePool { get; init; }
	public TypeMemberTable TypeMemberTable { get; init; }
	
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
	
	private static Symbol? ResolveFrom(string name, ImmutableArray<Symbol> candidates) => candidates switch
	{
		{ Length: > 1 } => new AmbiguousSymbol(name, candidates),
		{ Length: 1 } => candidates[0],
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
		
		if (File.Symbols.TryGetValue(name, out var fileSymbol))
			return fileSymbol;
		
		if (Imports?.Resolve(name) is { Length: > 0 } imports)
			return ResolveFrom(name, imports);
		
		return NativeSymbols.Resolve(name);
	}
	
	public TypeSymbol ResolveType(ITypeNode node) => node switch
	{
		IdentifierTypeNode n => Resolve(n.Token.Text) as TypeSymbol ?? NativeSymbols.Invalid, // TODO Diagnostics
		GenericTypeNode n => ResolveGenericType(n),
		_ => NativeSymbols.Invalid
	};
	
	private TypeSymbol ResolveTypeArgument(IGenericArgumentNode node) => node switch
    {
        TypeArgumentNode t => ResolveType(t.Type),
        IdentifierArgumentNode i => Resolve(i.Identifier.Text) as TypeSymbol ?? NativeSymbols.Invalid,
        _ => NativeSymbols.Invalid
    };
	
    private BigInteger? ResolveConstIntArgument(IGenericArgumentNode node) => node switch
    {
        ExpressionArgumentNode e => ResolveConstIntExpression(e.Expression),
        IdentifierArgumentNode i => ResolveConstIntIdentifier(i.Identifier),
        _ => null
    };
    
    private BigInteger? ResolveConstIntIdentifier(Token identifier) =>
	    throw new NotImplementedException();
    
    // TODO Need to implement constant expression evaluation prior to resolution?
    // TODO Cannot handle negative integers
    private BigInteger? ResolveConstIntExpression(IExpressionNode node) => node switch
    {
        LiteralExpressionNode e when e.Token.Type == TokenType.IntegerLiteral
            => BigInteger.TryParse(e.Token.AsSpan(), out var value) ? value : null,
        
        _ => null
    };
    
    private TypeSymbol ResolveGenericType(GenericTypeNode node) => node.Identifier.Text switch
    {
        "span" when node.Arguments.Length == 1
            => ResolveSpanType(node),

        "array" when node.Arguments.Length == 2
            => ResolveArrayType(node),

        _ => NativeSymbols.Invalid
    };
    
    private TypeSymbol ResolveSpanType(GenericTypeNode node)
    {
        var elementType = ResolveTypeArgument(node.Arguments[0]);
        return elementType == NativeSymbols.Invalid
            ? NativeSymbols.Invalid
            : TypePool.GetSpanType(elementType);
    }
    
    private TypeSymbol ResolveArrayType(GenericTypeNode node)
    {
        var elementType = ResolveTypeArgument(node.Arguments[0]);
        var length = ResolveConstIntArgument(node.Arguments[1]);

        if (elementType == NativeSymbols.Invalid || length is null || length.Value < 0)
            return NativeSymbols.Invalid;

        return TypePool.GetArrayType(elementType, length.Value);
    }
}