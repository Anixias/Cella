using System.Collections.Immutable;
using Cella.Core.Binding;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Syntax.Nodes.Declarations;
using Cella.Core.Text;

namespace Cella.Core.Symbols;

public enum Visibility
{
	/// <summary>
	/// Only visible within the containing symbol
	/// </summary>
	Private,
	
	/// <summary>
	/// Visible to other assemblies, exported
	/// </summary>
	Public
}

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
	
	public Visibility Visibility { get; } = syntax.Modifiers.Any(static t => t.Type == TokenType.KeywordPub)
		? Visibility.Public
		: Visibility.Private;
	
	public FunctionInfo? ContainingFunction { get; } = containingFunction;
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
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

public abstract class VariableSymbol(Token identifier)
	: Symbol(identifier.GetText()), IDefinedSymbol
{
	public SourceLocation Definition { get; } = identifier.SourceLocation;
}

// TODO: Syntax node?
// TODO: Initializer? Or is that stored elsewhere?
// TODO: Bind whether it has a constant initializer and no reassignments
public sealed class LocalVariableSymbol(VarStatementNode syntax, TypeSymbol type)
	: VariableSymbol(syntax.Identifier)
{
	public VarStatementNode Syntax { get; } = syntax;
	public TypeSymbol Type { get; } = type;
}

// TODO: Syntax node?
// TODO: Initializer? Or is that stored elsewhere?
public sealed class ParameterSymbol(Token identifier)
	: VariableSymbol(identifier);

// TODO Throw errors when symbols resolved as ambiguous
public sealed class AmbiguousSymbol(string name, IEnumerable<Symbol> candidates) : Symbol(name)
{
	public ImmutableArray<Symbol> Candidates { get; } = candidates.ToImmutableArray();
}