using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Symbols;

public abstract class Symbol(string name)
{
	public string Name { get; } = name;
}

public sealed class ModuleSymbol(string name, params IEnumerable<SourceLocation> declarations)
	: Symbol(name)
{
	public ImmutableArray<SourceLocation> Declarations { get; } = declarations.ToImmutableArray();
}

public interface IDefinedSymbol
{
	SourceLocation Definition { get; }
}

public sealed class FunctionSymbol
(
	string name,
	IEnumerable<ParameterSymbol> parameters,
	TypeSymbol? returnType,
	SourceLocation definition
) : Symbol(name), IDefinedSymbol
{
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public TypeSymbol? ReturnType { get; } = returnType;
	public SourceLocation Definition { get; } = definition;
	public string? MangledName { get; set;  }
}

public abstract class TypeSymbol(string name) : Symbol(name);

public sealed class InvalidType() : TypeSymbol("?");

public sealed class PrimitiveType(string name, PrimitiveTypeKind kind) : TypeSymbol(name)
{
	public PrimitiveTypeKind Kind { get; } = kind;
}

public abstract class VariableSymbol(string name, TypeSymbol type, SourceLocation definition)
	: Symbol(name), IDefinedSymbol
{
	public TypeSymbol Type { get; } = type;
	public SourceLocation Definition { get; } = definition;
}

// TODO: Initializer? Or is that stored elsewhere?
public sealed class ParameterSymbol(string name, TypeSymbol type, SourceLocation definition)
	: VariableSymbol(name, type, definition);