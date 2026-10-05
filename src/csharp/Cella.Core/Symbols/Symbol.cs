using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Binding.Constants;
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

public static class VisibilityExtensions
{
	extension(Visibility visibility)
	{
		public static Visibility FromModifiers(IEnumerable<Token> tokens) =>
			tokens.Any(static t => t.Type == TokenType.KeywordPub)
				? Visibility.Public
				: Visibility.Private;
	}
}

public abstract class Symbol(string name)
{
	public string Name { get; } = name;
}

public sealed class AssemblySymbol
(
	string name,
	SymbolTable symbolTable,
	SignatureTable signatureTable,
	FunctionInfo? entryPoint
) : Symbol(name)
{
	public SymbolTable SymbolTable { get; } = symbolTable;
	public SignatureTable SignatureTable { get; } = signatureTable;
	public FunctionInfo? EntryPoint { get; } = entryPoint;
}

public sealed class ModuleSymbol
(
	ModuleName moduleName,
	params IEnumerable<SourceLocation> declarations
) : Symbol(moduleName.Text)
{
	public ModuleName ModuleName { get; } = moduleName;
	public ImmutableArray<SourceLocation> Declarations { get; } = declarations.ToImmutableArray();
	public List<FileSymbol> Files { get; } = [];
}

public sealed class ModulePathSymbol(string path, ModulePathSymbol? parent)
	: Symbol(path[(path.LastIndexOf('.') + 1)..])
{
	public string Path { get; } = path;
	public ModulePathSymbol? Parent { get; } = parent;
	public Dictionary<string, List<Symbol>> Members { get; } = [];
	public Dictionary<string, List<Symbol>> PrivateMembers { get; } = [];
	public Dictionary<string, ModulePathSymbol> Children { get; } = [];
}

public sealed class FileSymbol(FileNode syntax, ModuleSymbol module, IEnumerable<Symbol> symbols)
	: Symbol(syntax.FileName)
{
	public FileNode Syntax { get; } = syntax;
	public string FullPath { get; } = syntax.FullPath;
	public ModuleSymbol Module { get; } = module;
	
	public ImmutableDictionary<string, HashSet<Symbol>> Symbols { get; } = symbols
		.GroupBy(static s => s.Name)
		.ToImmutableDictionary(static g => g.Key, static g => g.ToHashSet());
	
	// TODO Symbols by name won't work with overloaded functions
}

public enum FunctionKind
{
	Free,
	Method,
	Constructor,
	Destructor,
	External
}

// TODO Type parameter symbols
public sealed class FunctionSymbol
(
	string name,
	IDeclarationNode syntax,
	IEnumerable<Token> modifiers,
	FunctionInfo? containingFunction,
	IEnumerable<ParameterSymbol> parameters,
	FunctionKind kind
)
	: Symbol(name), IExportable
{
	public IDeclarationNode Syntax { get; } = syntax;
	public Visibility Visibility { get; } = Visibility.FromModifiers(modifiers);
	public FunctionInfo? ContainingFunction { get; } = containingFunction;
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public FunctionKind Kind { get; } = kind;
	public SourceLocation Definition { get; } = syntax.SourceLocation;
	public bool IsExternal => Kind == FunctionKind.External || Syntax is FunctionNode { IsExternal: true };
}

public abstract class TypeSymbol(string name, TypeSymbol? containingType = null, params IEnumerable<Symbol> children)
	: Symbol(name)
{
	public TypeSymbol? ContainingType { get; } = containingType;
	public ImmutableDictionary<string, Symbol> Children { get; } = children.ToImmutableDictionary(static s => s.Name);
}

public sealed class InvalidType : TypeSymbol
{
	public static InvalidType Instance { get; } = new();
	
	private InvalidType() : base("??")
	{
	}
}

public interface IPrimitiveType
{
	PrimitiveTypeKind Kind { get; }
}

public class PrimitiveType(string name, PrimitiveTypeKind kind) : TypeSymbol(name), IPrimitiveType
{
	public PrimitiveTypeKind Kind { get; } = kind;
}

public sealed class IntegerType(string name, PrimitiveTypeKind kind, bool isSigned)
	: PrimitiveType(name, kind)
{
	public bool IsSigned { get; } = isSigned;
}

public sealed class FloatType(string name, PrimitiveTypeKind kind) : PrimitiveType(name, kind);
public sealed class StringType(string name, PrimitiveTypeKind kind) : PrimitiveType(name, kind);

public enum MaterializationMode
{
	Default,
	Overload
}

public abstract class UntypedType(string name) : TypeSymbol(name)
{
	public abstract int MaterializationCost(TypeSymbol target, MaterializationMode mode);
}

public sealed class NeverType() : UntypedType("never")
{
	public static NeverType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode) => 0;
}

public sealed class UntypedIntegerType() : UntypedType("integer literal")
{
	public static UntypedIntegerType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode)
	{
		if (target == NativeSymbols.Int32)
			return 0;
		
		switch (mode)
		{
			case MaterializationMode.Overload:
				if (target == NativeSymbols.Int8)
					return 1;
				
				if (target == NativeSymbols.UInt8)
					return 2;
				
				if (target == NativeSymbols.Int16)
					return 3;
				
				if (target == NativeSymbols.UInt16)
					return 4;
				
				if (target == NativeSymbols.UInt32)
					return 5;
				
				if (target == NativeSymbols.Int64)
					return 6;
				
				if (target == NativeSymbols.UInt64)
					return 7;
				
				if (target == NativeSymbols.Int128)
					return 8;
				
				if (target == NativeSymbols.UInt128)
					return 9;
				
				if (target == NativeSymbols.IntSize)
					return 10;
				
				if (target == NativeSymbols.UIntSize)
					return 11;
				
				if (target == NativeSymbols.Float64)
					return 12;
				
				if (target == NativeSymbols.Float32)
					return 13;
				
				return int.MaxValue;
			
			default:
				if (target == NativeSymbols.Int64)
					return 1;
				
				if (target == NativeSymbols.Int128)
					return 2;
				
				return int.MaxValue;
		}
	}
}

public sealed class UntypedFloatType() : UntypedType("float literal")
{
	public static UntypedFloatType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode)
	{
		if (target == NativeSymbols.Float64)
			return 0;
		
		return mode == MaterializationMode.Overload && target == NativeSymbols.Float32 ? 1 : int.MaxValue;
	}
}

public sealed class UntypedNullType() : UntypedType("null")
{
	public static UntypedNullType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode) => mode switch
	{
		MaterializationMode.Overload => target is PointerType ? 0 : int.MaxValue,
		_ => target == NativeSymbols.VoidPtr ? 0 : int.MaxValue
	};
}

public sealed class UntypedStringType() : UntypedType("string literal")
{
	public static UntypedStringType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode)
	{
		if (target == NativeSymbols.Str)
			return 0;
		
		switch (mode)
		{
			case MaterializationMode.Overload:
				if (target == NativeSymbols.CStr)
					return 1;
				
				return int.MaxValue;
			
			default:
				return int.MaxValue;
		}
	}
}

public sealed class FunctionGroupType(string functionName, IEnumerable<FunctionInfo> functions, string name)
	: UntypedType(name)
{
	public string FunctionName { get; } = functionName;
	public ImmutableArray<FunctionInfo> Functions { get; } = functions.ToImmutableArray();
	
	public FunctionInfo? Find(FunctionType type)
	{
		foreach (var function in Functions)
		{
			var signature = function.Signature;
			if (!signature.IsVariadic && signature.ReturnType == type.ReturnType &&
			    signature.ParameterTypes.SequenceEqual(type.ParameterTypes) &&
			    type.ParameterModes.Select((_, i) => signature.GetMode(i)).SequenceEqual(type.ParameterModes))
				return function;
		}
		
		return null;
	}
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode) =>
		target is FunctionType type && Find(type) is { } function
			? function.Symbol.IsExternal == type.IsExternal ? 0 : 1
			: int.MaxValue;
}

public sealed class FunctionType : TypeSymbol
{
	public bool IsExternal { get; }
	public ImmutableArray<TypeSymbol> ParameterTypes { get; }
	public ImmutableArray<ParameterMode> ParameterModes { get; }
	public TypeSymbol ReturnType { get; }
	
	public FunctionType(bool isExternal, ImmutableArray<TypeSymbol> parameterTypes,
		ImmutableArray<ParameterMode> parameterModes, TypeSymbol returnType)
		: base(BuildName(isExternal, parameterTypes, parameterModes, returnType))
	{
		IsExternal = isExternal;
		ParameterTypes = parameterTypes;
		ParameterModes = parameterModes;
		ReturnType = returnType;
	}
	
	public TypeSymbol GetDeclaredType(int index) => ParameterModes[index].GetDeclaredType(ParameterTypes[index]);
	
	private static string BuildName(bool isExternal, ImmutableArray<TypeSymbol> parameterTypes,
		ImmutableArray<ParameterMode> parameterModes, TypeSymbol returnType)
	{
		var prefix = isExternal ? "ext fun" : "fun";
		var parameters = parameterTypes.Select((type, i) => parameterModes[i].Describe(type));
		var name = $"{prefix}({string.Join(", ", parameters)})";
		return returnType == NativeSymbols.Void ? name : $"{name} -> {returnType.Name}";
	}
}

public sealed class PointerType : TypeSymbol, IPrimitiveType
{
	public static PointerType VoidPtr { get; } = new("ptr", NativeSymbols.Void);
	
	private PointerType(string name, TypeSymbol baseType) : base(name)
	{
		BaseType = baseType;
	}
	
	public PointerType(TypeSymbol baseType) : this($"ptr[{baseType.Name}]", baseType)
	{
	}
	
	public TypeSymbol BaseType { get; }
	public PrimitiveTypeKind Kind { get; } = PrimitiveTypeKind.Pointer;
}

public sealed class ArrayType(TypeSymbol elementType, BigInteger length)
	: TypeSymbol(length.Sign < 0 ? $"array[{elementType.Name}]" : $"array[{elementType.Name}, {length}]"),
		IPrimitiveType
{
	public TypeSymbol ElementType { get; } = elementType;
	public BigInteger Length { get; } = length;
	public PrimitiveTypeKind Kind { get; } = PrimitiveTypeKind.Array;
}

public abstract class VariableSymbol(string name) : Symbol(name);

public sealed class LocalVariableSymbol(Token identifier, TypeSymbol type, bool isMutable)
	: VariableSymbol(identifier.Text)
{
	public Token Identifier { get; } = identifier;
	public TypeSymbol Type { get; } = type;
	public bool IsMutable { get; } = isMutable;
	public bool IsPatternBinding { get; init; }
	public bool IsBorrowBinding { get; init; }
	public bool IsMutBinding { get; init; }
	public bool IsDeferred { get; init; }
	public Constant? ConstantValue { get; init; }
}

// TODO: Initializer? Or is that stored elsewhere?
public sealed class GlobalSymbol(GlobalNode syntax) : VariableSymbol(syntax.Identifier.Text), IExportable
{
	public GlobalNode Syntax { get; } = syntax;
	public bool IsMutable => Syntax.IsMutable;
	public Visibility Visibility { get; } = Visibility.FromModifiers(syntax.Modifiers);
	public SourceLocation Definition { get; } = syntax.SourceLocation;
}

public enum ParameterMode
{
	ReadOnly,
	Mut,
	Own
}

public static class ParameterModeExtensions
{
	public static TypeSymbol GetDeclaredType(this ParameterMode mode, TypeSymbol passedType) =>
		mode == ParameterMode.Mut && passedType is PointerType pointer ? pointer.BaseType : passedType;
	
	public static string Describe(this ParameterMode mode, TypeSymbol passedType) => mode switch
	{
		ParameterMode.Mut => $"mut {mode.GetDeclaredType(passedType).Name}",
		ParameterMode.Own => $"own {passedType.Name}",
		_ => passedType.Name
	};
}

public sealed class ParameterSymbol : VariableSymbol
{
	public Token Identifier { get; }
	public SourceLocation Definition { get; }
	public ParameterMode Mode { get; init; }
	
	public ParameterSymbol(Token identifier) : base(identifier.Text)
	{
		Identifier = identifier;
		Definition = identifier.SourceLocation;
	}
	
	public ParameterSymbol(string name, SourceLocation definition) : base(name)
	{
		Identifier = default;
		Definition = definition;
	}
}

public sealed class LabelSymbol(Token identifier) : Symbol(identifier.Text)
{
	public Token Identifier { get; } = identifier;
	public SourceLocation Definition { get; } = identifier.SourceLocation;
}

public sealed class RecordSymbol : TypeSymbol, IExportable
{
	public RecordNode Node { get; }
	public ImmutableArray<MemberSymbol> Members { get; }
	public ImmutableArray<TypeSymbol> NestedTypes { get; }
	public Visibility Visibility { get; }
	public bool HasDestructor => Members.Any(static m => m is MethodSymbol { Function.Kind: FunctionKind.Destructor });
	
	public RecordSymbol(string name, RecordNode node, IEnumerable<MemberSymbol> members,
		IEnumerable<TypeSymbol> nestedTypes) : base(name)
	{
		Node = node;
		Members = members.ToImmutableArray();
		NestedTypes = nestedTypes.ToImmutableArray();
		Visibility = Visibility.FromModifiers(node.Modifiers);
	}
}

public sealed class EnumSymbol : TypeSymbol, IExportable
{
	public EnumNode Node { get; }
	public ImmutableArray<EnumCaseSymbol> Cases { get; }
	public Visibility Visibility { get; }
	public bool HasPayload => Cases.Any(static c => c.Fields.Length > 0);
	public bool IsExternal => Node.IsExternal;
	
	public EnumSymbol(EnumNode node, IEnumerable<EnumCaseSymbol> cases) : base(node.Identifier.Text)
	{
		Node = node;
		Cases = cases.ToImmutableArray();
		Visibility = Visibility.FromModifiers(node.Modifiers);
	}
}

public sealed class EnumCaseSymbol(EnumCaseNode node, int index, IEnumerable<FieldSymbol> fields)
	: Symbol(node.Identifier.Text)
{
	public EnumCaseNode Node { get; } = node;
	public int Index { get; } = index;
	public ImmutableArray<FieldSymbol> Fields { get; } = fields.ToImmutableArray();
}

public abstract class MemberSymbol(string name) : Symbol(name);
public abstract class TypedMemberSymbol(string name) : MemberSymbol(name);

public sealed class FieldSymbol(string name, FieldNode? node, bool isMutable) : TypedMemberSymbol(name)
{
	public FieldNode? Node { get; } = node;
	public bool IsMutable { get; } = isMutable;
	public bool IsRequired { get; } = node?.Modifiers.Any(static m => m.Type == TokenType.KeywordReq) ?? false;
}

public sealed class PropertySymbol(string name) : TypedMemberSymbol(name)
{
	public FieldSymbol? BackingField { get; init; }
	public AccessorImpl? Getter { get; init; }
	public AccessorImpl? Setter { get; init; }
}

public sealed class IndexerSymbol(string name, IEnumerable<ParameterSymbol> parameters) : TypedMemberSymbol(name)
{
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public AccessorImpl? Getter { get; init; }
	public AccessorImpl? Setter { get; init; }
}

public sealed class MethodSymbol(FunctionSymbol function, SelfReferenceKind selfReferenceKind)
	: MemberSymbol(function.Name)
{
	public FunctionSymbol Function { get; } = function;
	public SelfReferenceKind SelfReferenceKind { get; } = selfReferenceKind;
}

public abstract record AccessorImpl;
public sealed record NativeAccessor(NativeMemberIntrinsic Intrinsic) : AccessorImpl;
public sealed record FunctionAccessor(FunctionSymbol Function) : AccessorImpl;

// TODO Throw errors when symbols resolved as ambiguous
public sealed class AmbiguousSymbol(string name, IEnumerable<Symbol> candidates) : Symbol(name)
{
	public ImmutableArray<Symbol> Candidates { get; } = candidates.ToImmutableArray();
}

public enum SelfReferenceKind
{
	None,
	Mutable,
	Immutable
}

public enum NativeMemberIntrinsic
{
	ArrayLength,
	StrByteLength,
	StrData,
	ArrayIndexGet,
	ArrayIndexSet,
	SpanIndexGet,
	SpanIndexSet,
	ViewIndexGet
}