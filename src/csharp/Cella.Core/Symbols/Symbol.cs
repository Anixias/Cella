using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Syntax;
using Cella.Core.Syntax.Nodes;
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

public interface IExportable
{
	Visibility Visibility { get; }
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

// TODO Type parameter symbols
public sealed class FunctionSymbol
(
	string name,
	IFunctionNode syntax,
	FunctionInfo? containingFunction,
	IEnumerable<ParameterSymbol> parameters
) : Symbol(name), IExportable
{
	public IFunctionNode Syntax { get; } = syntax;
	
	public FunctionInfo? ContainingFunction { get; } = containingFunction;
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public SourceLocation Definition { get; } = syntax.SourceLocation;
	
	public Visibility Visibility { get; } = syntax.Modifiers.Any(static t => t.Type == TokenType.KeywordPub)
		? Visibility.Public
		: Visibility.Private;
}

public abstract class TypeSymbol(string name, TypeSymbol? containingType = null, params IEnumerable<Symbol> children)
	: Symbol(name)
{
	public TypeSymbol? ContainingType { get; } = containingType;
	public ImmutableDictionary<string, Symbol> Children { get; } = children.ToImmutableDictionary(static s => s.Name);
	public abstract ISize Size { get; }
}

public sealed class InvalidType : TypeSymbol
{
	public static InvalidType Instance { get; } = new();
	
	public override ISize Size => new ConstSize(0);
	
	private InvalidType() : base("??")
	{
	}
}

public interface IPrimitiveType
{
	PrimitiveTypeKind Kind { get; }
}

public class PrimitiveType(string name, PrimitiveTypeKind kind, ISize size) : TypeSymbol(name), IPrimitiveType
{
	public PrimitiveTypeKind Kind { get; } = kind;
	public override ISize Size { get; } = size;
}

public sealed class IntegerType(string name, PrimitiveTypeKind kind, ISize size, bool isSigned)
	: PrimitiveType(name, kind, size)
{
	public bool IsSigned { get; } = isSigned;
}

public enum PointerKind
{
	Unsafe,
	Mutable,
	Immutable,
	Owning
}

public sealed class PointerType(TypeSymbol baseType, PointerKind pointerKind)
	: TypeSymbol(BuildName(baseType, pointerKind)), IPrimitiveType
{
	public TypeSymbol BaseType { get; } = baseType;
	public PrimitiveTypeKind Kind { get; } = PrimitiveTypeKind.Pointer;
	public PointerKind PointerKind { get; } = pointerKind;
	public override ISize Size { get; } = StorageSize.Ptr;
	
	public static string BuildName(TypeSymbol baseType, PointerKind pointerKind) => pointerKind switch
	{
		PointerKind.Mutable => $"mut {baseType.Name}",
		PointerKind.Immutable => $"imm {baseType.Name}",
		PointerKind.Owning => $"own {baseType.Name}",
		_ => $"ptr {baseType.Name}"
	};
}

public sealed class ArrayType(TypeSymbol elementType, BigInteger length)
	: TypeSymbol($"array[{elementType.Name} * {length}]"), IPrimitiveType
{
	public TypeSymbol ElementType { get; } = elementType;
	public BigInteger Length { get; } = length;
	public PrimitiveTypeKind Kind { get; } = PrimitiveTypeKind.Array;
	public override ISize Size { get; } = StorageSize.Product(elementType.Size, length);
}

public sealed class SpanType(TypeSymbol elementType) : TypeSymbol($"span[{elementType.Name}]"), IPrimitiveType
{
	public TypeSymbol ElementType { get; } = elementType;
	public PrimitiveTypeKind Kind { get; } = PrimitiveTypeKind.Span;
	public override ISize Size { get; } = StorageSize.Sum(NativeSymbols.UIntSize.Size, StorageSize.Ptr);
}

// TODO ViewType

public abstract class VariableSymbol(string name) : Symbol(name);

// TODO: Bind whether it has a constant initializer and no reassignments
public sealed class LocalVariableSymbol(VarStatementNode syntax, TypeSymbol type)
	: VariableSymbol(syntax.Identifier.Text)
{
	public VarStatementNode Syntax { get; } = syntax;
	public TypeSymbol Type { get; } = type;
	public SourceLocation Definition { get; } = syntax.SourceLocation;
}

// TODO: Initializer? Or is that stored elsewhere?
public sealed class ParameterSymbol(Token identifier) : VariableSymbol(identifier.Text)
{
	public Token Identifier { get; } = identifier;
	public SourceLocation Definition { get; } = identifier.SourceLocation;
}

public sealed class LabelSymbol(Token identifier) : Symbol(identifier.Text)
{
	public Token Identifier { get; } = identifier;
	public SourceLocation Definition { get; } = identifier.SourceLocation;
}

public abstract class MemberSymbol(string name, TypeSymbol type) : VariableSymbol(name)
{
	public TypeSymbol Type { get; } = type;
}

public sealed class IntrinsicMemberSymbol(string name, TypeSymbol type) : MemberSymbol(name, type);

public sealed class DefinedMemberSymbol(Token identifier, TypeSymbol type) : MemberSymbol(identifier.Text, type)
{
	public Token Identifier { get; } = identifier;
	public SourceLocation Definition { get; } = identifier.SourceLocation;
}

// TODO Throw errors when symbols resolved as ambiguous
public sealed class AmbiguousSymbol(string name, IEnumerable<Symbol> candidates) : Symbol(name)
{
	public ImmutableArray<Symbol> Candidates { get; } = candidates.ToImmutableArray();
}