using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Core.Symbols;

public interface IDefinedSymbol
{
	SourceLocation Definition { get; }
}

public abstract class Symbol(string name)
{
	public string Name { get; } = name;
}

public sealed class AssemblySymbol(string name, SymbolTable symbolTable, SignatureTable signatureTable,
	FunctionInfo? entryPoint) : Symbol(name)
{
	public SymbolTable SymbolTable { get; } = symbolTable;
	public SignatureTable SignatureTable { get; } = signatureTable;
	public FunctionInfo? EntryPoint { get; } = entryPoint;
}

public sealed class ModuleSymbol(ModuleName moduleName,
	params IEnumerable<SourceLocation> declarations) : Symbol(moduleName.Text)
{
	public ModuleName ModuleName { get; } = moduleName;
	public ImmutableArray<SourceLocation> Declarations { get; } = declarations.ToImmutableArray();
}

public sealed class FileSymbol(FileNode syntax, ModuleSymbol module, IEnumerable<Symbol> symbols)
	: Symbol(syntax.FileName)
{
	public FileNode Syntax { get; } = syntax;
	public ModuleSymbol Module { get; } = module;
	public ImmutableDictionary<string, Symbol> Symbols { get; } = symbols.ToImmutableDictionary(static s => s.Name);
	// TODO Symbols by name won't work with overloaded functions
}

// TODO Access visibility modifiers
// TODO Type parameter symbols
public sealed class FunctionSymbol
(
	string name,
	FunctionNode syntax,
	FunctionInfo? containingFunction,
	IEnumerable<ParameterSymbol> parameters
) : Symbol(name), IDefinedSymbol
{
	public FunctionNode Syntax { get; } = syntax;
	public FunctionInfo? ContainingFunction { get; } = containingFunction;
	
	public ImmutableDictionary<string, ParameterSymbol> Parameters { get; } =
		parameters.ToImmutableDictionary(static s => s.Name);
	
	public SourceLocation Definition { get; } = syntax.SourceLocation;
}

public abstract class TypeSymbol(string name, TypeSymbol? containingType = null, params IEnumerable<Symbol> children)
	: Symbol(name)
{
	public TypeSymbol? ContainingType { get; } = containingType;
	public ImmutableDictionary<string, Symbol> Children { get; } = children.ToImmutableDictionary(static s => s.Name);
}

public sealed class InvalidType() : TypeSymbol("??");

public sealed class PrimitiveType(string name, PrimitiveTypeKind kind) : TypeSymbol(name)
{
	public PrimitiveTypeKind Kind { get; } = kind;
}

public abstract class VariableSymbol(string name, SourceLocation definition)
	: Symbol(name), IDefinedSymbol
{
	public SourceLocation Definition { get; } = definition;
}

// TODO: Syntax node?
// TODO: Initializer? Or is that stored elsewhere?
public sealed class ParameterSymbol(string name, SourceLocation definition)
	: VariableSymbol(name, definition);

// TODO Throw errors when symbols resolved as ambiguous
public sealed class AmbiguousSymbol(string name, IEnumerable<Symbol> candidates) : Symbol(name)
{
	public ImmutableArray<Symbol> Candidates { get; } = candidates.ToImmutableArray();
}