using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Symbols;

public abstract class Symbol(string name, params IEnumerable<SourceLocation> declarations)
{
	public string Name { get; } = name;
	public ImmutableArray<SourceLocation> Declarations { get; } = declarations.ToImmutableArray();
}

public sealed class ModuleSymbol(string name, params IEnumerable<SourceLocation> declarations) : Symbol(name, declarations);

public sealed class FunctionSymbol
(
	string name,
	IEnumerable<ParameterSymbol> parameters,
	TypeSymbol? returnType,
	params IEnumerable<SourceLocation> declarations
) : Symbol(name, declarations)
{
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public TypeSymbol? ReturnType { get; } = returnType;
}

public abstract class TypeSymbol(string name, params IEnumerable<SourceLocation> declarations)
	: Symbol(name, declarations);

public sealed class InvalidType() : TypeSymbol("?");

public sealed class PrimitiveType(string name) : TypeSymbol(name);

public abstract class VariableSymbol(string name, TypeSymbol type, params IEnumerable<SourceLocation> declarations)
	: Symbol(name, declarations)
{
	public TypeSymbol Type { get; } = type;
}

// TODO: Initializer? Or is that stored elsewhere?
public sealed class ParameterSymbol(string name, TypeSymbol type, params IEnumerable<SourceLocation> declarations)
	: VariableSymbol(name, type, declarations);