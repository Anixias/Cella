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
	
	Module,
	Project,
	
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
		public static Visibility FromKeyword(Token? keyword) => keyword?.Type switch
		{
			TokenType.KeywordPub => Visibility.Public,
			TokenType.KeywordMod => Visibility.Module,
			TokenType.KeywordPvt => Visibility.Private,
			_ => Visibility.Project
		};
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
	Visibility visibility,
	FunctionInfo? containingFunction,
	IEnumerable<ParameterSymbol> parameters,
	FunctionKind kind
)
	: Symbol(name), IExportable
{
	public IDeclarationNode Syntax { get; } = syntax;
	public Visibility Visibility { get; } = visibility;
	public FunctionInfo? ContainingFunction { get; } = containingFunction;
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public FunctionKind Kind { get; } = kind;
	public SourceLocation Definition { get; } = syntax.SourceLocation;
	public bool IsExternal => Kind == FunctionKind.External || Syntax is FunctionNode { IsExternal: true };
	
	public bool IsConversion =>
		Syntax is FunctionNode { Identifier.Type: TokenType.KeywordAs or TokenType.KeywordNew };
	
	public PropertySymbol? Property { get; internal set; }
	public TraitSymbol? Trait { get; internal set; }
	public ImplSymbol? Impl { get; internal set; }
	public ImmutableArray<TypeParameterSymbol> TypeParameters { get; init; } = [];
	public ImmutableArray<TypeParameterSymbol> DeclaredTypeParameters { get; init; } = [];
	public ImmutableArray<LocalVariableSymbol> Captures { get; internal set; } = [];
	public bool OwnsCaptures { get; internal set; }
}

public abstract class TypeSymbol(string name, TypeSymbol? containingType = null, params IEnumerable<Symbol> children)
	: Symbol(name)
{
	public TypeSymbol? ContainingType { get; } = containingType;
	public ImmutableDictionary<string, Symbol> Children { get; } = children.ToImmutableDictionary(static s => s.Name);
	
	public virtual ImmutableArray<TypeSymbol> TypeArguments => [];
	public virtual TypeSymbol OriginalDefinition => this;
	
	public virtual IEnumerable<MethodSymbol> GetFunctions(string name) => [];
	public virtual GlobalSymbol? GetStaticField(string name) => null;
	public virtual PropertySymbol? GetProperty(string name) => null;
}

public sealed class TypeParameterSymbol(string name, IEnumerable<Token> keywords) : TypeSymbol(name)
{
	private readonly ImmutableArray<Token> _keywords = keywords.ToImmutableArray();
	
	public bool IsNoref => Has(TokenType.KeywordNoref) || IsAtomic;
	public bool HasNull => Has(TokenType.KeywordNull);
	public bool IsCopy => Has(TokenType.KeywordCopy) || IsAtomic;
	public bool IsAtomic => Has(TokenType.KeywordAtomic);
	public bool HasDrop => Has(TokenType.KeywordDrop);
	public bool HasNew => Has(TokenType.KeywordNew);
	public bool IsTrait => Has(TokenType.KeywordTrait);
	public bool IsValue { get; init; }
	public TypeSymbol? ValueType { get; internal set; }
	
	private bool Has(TokenType keyword) => _keywords.Any(token => token.Type == keyword);
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
				
				return target is FStrType ? 2 : int.MaxValue;
			
			default:
				return int.MaxValue;
		}
	}
}

public sealed class InterpolatedStringType() : UntypedType("interpolated string")
{
	public static InterpolatedStringType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode)
	{
		if (mode == MaterializationMode.Default)
			return target == NativeSymbols.Str ? 0 : int.MaxValue;
		
		if (target is FStrType)
			return 0;
		
		if (target == NativeSymbols.Str)
			return 1;
		
		return target == NativeSymbols.CStr ? 2 : int.MaxValue;
	}
}

public sealed class CaseNameType() : UntypedType("case name")
{
	public static CaseNameType Instance { get; } = new();
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode) => int.MaxValue;
}

public sealed class FunctionGroupType(string functionName, IEnumerable<FunctionInfo> functions, string name)
	: UntypedType(name)
{
	public string FunctionName { get; } = functionName;
	public ImmutableArray<FunctionInfo> Functions { get; } = functions.ToImmutableArray();
	
	public FunctionInfo? Find(FunctionType type) => Find(type, false) ?? Find(type, true);
	
	private FunctionInfo? Find(FunctionType type, bool allowsStandIns)
	{
		foreach (var function in Functions)
		{
			if (Matches(function, type, allowsStandIns))
				return function;
		}
		
		return null;
	}
	
	public static bool Matches(FunctionInfo function, FunctionType type, bool allowsStandIns = false)
	{
		var signature = function.Signature;
		var adapts = allowsStandIns && !type.IsExternal;
		return !signature.IsVariadic && signature.ParameterTypes.Length == type.ParameterTypes.Length &&
		       (signature.ReturnType == type.ReturnType ||
		        adapts && FunctionType.CanAdapt(signature.ReturnType, type.ReturnType)) &&
		       Enumerable.Range(0, type.ParameterTypes.Length).All(i => ParameterMatches(signature, type, i,
			       allowsStandIns, adapts));
	}
	
	private static bool ParameterMatches(FunctionSignature signature, FunctionType type, int index,
		bool allowsStandIns, bool adapts)
	{
		var mode = signature.GetMode(index);
		var parameter = signature.ParameterTypes[index];
		if (!allowsStandIns)
			return mode == type.ParameterModes[index] && parameter == type.ParameterTypes[index];
		
		return adapts
			? mode.CanStandInOrAdapt(parameter, type.ParameterModes[index], type.ParameterTypes[index])
			: mode.CanStandIn(parameter, type.ParameterModes[index], type.ParameterTypes[index]);
	}
	
	public override int MaterializationCost(TypeSymbol target, MaterializationMode mode) =>
		target is FunctionType type && Find(type) is { } function
			? (function.Symbol.IsExternal == type.IsExternal ? 0 : 1) + (type.IsRef ? 1 : 0) +
			  (Matches(function, type) ? 0 : 1)
			: int.MaxValue;
}

public sealed class FunctionType : TypeSymbol
{
	public bool IsExternal { get; }
	public bool IsRef { get; }
	public ImmutableArray<TypeSymbol> ParameterTypes { get; }
	public ImmutableArray<ParameterMode> ParameterModes { get; }
	public TypeSymbol ReturnType { get; }
	public string PlainName { get; }
	
	public FunctionType(bool isExternal, bool isRef, ImmutableArray<TypeSymbol> parameterTypes,
		ImmutableArray<ParameterMode> parameterModes, TypeSymbol returnType)
		: base(BuildName(isExternal, isRef, parameterTypes, parameterModes, returnType))
	{
		IsExternal = isExternal;
		IsRef = isRef;
		ParameterTypes = parameterTypes;
		ParameterModes = parameterModes;
		ReturnType = returnType;
		PlainName = isRef ? BuildName(isExternal, false, parameterTypes, parameterModes, returnType) : Name;
	}
	
	public TypeSymbol GetDeclaredType(int index) => ParameterModes[index].GetDeclaredType(ParameterTypes[index]);
	
	public static bool CanStandIn(FunctionType source, FunctionType target) =>
		!source.IsExternal && !target.IsExternal && (!source.IsRef || target.IsRef) &&
		SignatureStandsIn(source, target);
	
	public static bool CanAdapt(TypeSymbol source, TypeSymbol target) =>
		source is FunctionType { IsExternal: true } external && target is FunctionType { IsExternal: false } function &&
		SignatureStandsIn(external, function);
	
	public static bool SignatureStandsIn(FunctionType source, FunctionType target) =>
		source.ReturnType == target.ReturnType && source.ParameterTypes.Length == target.ParameterTypes.Length &&
		Enumerable.Range(0, source.ParameterTypes.Length).All(i => source.ParameterModes[i]
			.CanStandIn(source.ParameterTypes[i], target.ParameterModes[i], target.ParameterTypes[i]));
	
	private static string BuildName(bool isExternal, bool isRef, ImmutableArray<TypeSymbol> parameterTypes,
		ImmutableArray<ParameterMode> parameterModes, TypeSymbol returnType)
	{
		var prefix = isExternal ? "ext fun" : isRef ? "ref fun" : "fun";
		var parameters = string.Join(", ", parameterTypes.Select((type, i) => parameterModes[i].Describe(type)));
		var signature = returnType == NativeSymbols.Void ? parameters
			: parameters.Length == 0 ? $"-> {returnType.Name}"
			: $"{parameters} -> {returnType.Name}";
		
		return $"{prefix}[{signature}]";
	}
}

public sealed class ClosureType
(
	FunctionInfo function,
	ImmutableArray<TypeSymbol> captureTypes,
	FunctionType signature,
	string definitionName
) : TypeSymbol($"own {signature.Name}")
{
	public FunctionInfo Function { get; } = function;
	public ImmutableArray<TypeSymbol> CaptureTypes { get; } = captureTypes;
	public FunctionType Signature { get; } = signature;
	public string DefinitionName { get; } = definitionName;
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

public sealed class BorrowType(TypeSymbol target, bool isMutable)
	: TypeSymbol($"{(isMutable ? "mut" : "imm")}[{target.Name}]")
{
	public TypeSymbol Target { get; } = target;
	public bool IsMutable { get; } = isMutable;
}

public sealed class DynType : TypeSymbol
{
	public DynType(TraitType instance) : base($"dyn[{instance.Name}]")
	{
		Instance = instance;
		TraitName = instance.Name;
	}
	
	public DynType(TypeParameterSymbol parameter) : base($"dyn[{parameter.Name}]")
	{
		Parameter = parameter;
		TraitName = parameter.Name;
	}
	
	public DynType(FunctionType function) : base($"dyn[{function.Name}]")
	{
		Function = function;
		TraitName = function.Name;
	}
	
	public TraitType? Instance { get; }
	public FunctionType? Function { get; }
	public TraitSymbol? Trait => Instance?.Trait;
	public ImmutableArray<TypeSymbol> TraitArguments => Instance?.Arguments ?? [];
	public TypeParameterSymbol? Parameter { get; }
	public string TraitName { get; }
}

public sealed class TraitType(TraitSymbol trait, ImmutableArray<TypeSymbol> arguments)
	: TypeSymbol(arguments.IsEmpty
		? trait.Name
		: $"{trait.Name}[{string.Join(", ", arguments.Select(static argument => argument.Name))}]")
{
	public TraitSymbol Trait { get; } = trait;
	public ImmutableArray<TypeSymbol> Arguments { get; } = arguments;
}

public sealed class FStrType(DynType value) : TypeSymbol($"fstr[{value.TraitName}]")
{
	public DynType Value { get; } = value;
}

public sealed class ArrayType(TypeSymbol elementType, BigInteger length, TypeParameterSymbol? lengthParameter = null)
	: TypeSymbol(lengthParameter is not null
		? $"array[{elementType.Name}, {lengthParameter.Name}]"
		: $"array[{elementType.Name}, {length}]"), IPrimitiveType
{
	public TypeSymbol ElementType { get; } = elementType;
	public BigInteger Length { get; } = length;
	public TypeParameterSymbol? LengthParameter { get; } = lengthParameter;
	public PrimitiveTypeKind Kind { get; } = PrimitiveTypeKind.Array;
}

public sealed class ValueArgumentType(BigInteger value) : TypeSymbol(value.ToString())
{
	public BigInteger Value { get; } = value;
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
	public bool IsLoopBinding { get; init; }
	public bool IsDeferred { get; init; }
	public VariableSymbol? Captured { get; init; }
	public Constant? ConstantValue { get; init; }
}

// TODO: Initializer? Or is that stored elsewhere?
public sealed class GlobalSymbol(GlobalNode syntax, Visibility visibility)
	: VariableSymbol(syntax.Identifier.Text), IExportable
{
	public GlobalNode Syntax { get; } = syntax;
	public bool IsMutable => Syntax.IsMutable;
	public Visibility Visibility { get; } = visibility;
	
	public Visibility WriteVisibility { get; } =
		syntax.WriteVisibility is { } write ? Visibility.FromKeyword(write) : visibility;
	
	public TypeSymbol? ContainingType { get; internal set; }
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
	
	public static bool CanStandIn(this ParameterMode mode, TypeSymbol type, ParameterMode expectedMode,
		TypeSymbol expectedType) =>
		mode == expectedMode && type == expectedType ||
		mode == ParameterMode.ReadOnly && expectedMode == ParameterMode.Own &&
		type is FunctionType { IsRef: true, IsExternal: false } borrowed &&
		expectedType is FunctionType { IsRef: false, IsExternal: false } owned &&
		borrowed.ReturnType == owned.ReturnType && borrowed.ParameterTypes.SequenceEqual(owned.ParameterTypes) &&
		borrowed.ParameterModes.SequenceEqual(owned.ParameterModes);
	
	public static bool CanStandInOrAdapt(this ParameterMode mode, TypeSymbol type, ParameterMode expectedMode,
		TypeSymbol expectedType) =>
		mode.CanStandIn(type, expectedMode, expectedType) || mode != ParameterMode.Mut &&
		expectedMode != ParameterMode.Mut && FunctionType.CanAdapt(expectedType, type);
	
	public static string Describe(this ParameterMode mode, TypeSymbol passedType) => mode switch
	{
		ParameterMode.Mut => $"mut {mode.GetDeclaredType(passedType).Name}",
		ParameterMode.Own => $"own {passedType.Name}",
		_ when passedType is FunctionType function => function.PlainName,
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

public abstract class NamedTypeSymbol : TypeSymbol, IExportable
{
	public ImmutableArray<TypeParameterSymbol> TypeParameters { get; }
	public NamedTypeSymbol Definition { get; }
	public override ImmutableArray<TypeSymbol> TypeArguments { get; }
	public override TypeSymbol OriginalDefinition => Definition;
	public bool IsGenericDefinition => !TypeParameters.IsEmpty;
	public bool IsGenericInstance => Definition != this;
	public ImmutableArray<GlobalSymbol> StaticFields { get; init; } = [];
	public ImmutableArray<PropertySymbol> Properties { get; init; } = [];
	public Visibility Visibility { get; }
	public abstract bool IsRef { get; }
	
	public override GlobalSymbol? GetStaticField(string name) => StaticFields.FirstOrDefault(f => f.Name == name);
	public override PropertySymbol? GetProperty(string name) => Properties.FirstOrDefault(p => p.Name == name);
	
	protected NamedTypeSymbol(string name, Token? visibility, ImmutableArray<TypeParameterSymbol> typeParameters)
		: base(name, null, typeParameters.DistinctBy(static p => p.Name))
	{
		TypeParameters = typeParameters;
		Definition = this;
		TypeArguments = [..typeParameters];
		Visibility = Visibility.FromKeyword(visibility);
	}
	
	protected NamedTypeSymbol(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments)
		: base($"{definition.Name}[{string.Join(", ", typeArguments.Select(static a => a.Name))}]")
	{
		TypeParameters = [];
		Definition = definition;
		TypeArguments = typeArguments;
		Visibility = definition.Visibility;
		Properties = definition.Properties;
	}
}

public sealed class RecordSymbol : NamedTypeSymbol
{
	public RecordNode Node { get; }
	public ImmutableArray<MemberSymbol> Members { get; }
	public ImmutableArray<TypeSymbol> NestedTypes { get; }
	public bool HasDestructor => Members.Any(static m => m is MethodSymbol { Function.Kind: FunctionKind.Destructor });
	public override bool IsRef => Node.IsRef;
	
	public override IEnumerable<MethodSymbol> GetFunctions(string name) => Members
		.OfType<MethodSymbol>()
		.Where(m => m.Name == name && m.Function.Kind is FunctionKind.Method or FunctionKind.Free);
	
	public RecordSymbol(string name, RecordNode node, IEnumerable<MemberSymbol> members,
		IEnumerable<TypeSymbol> nestedTypes, ImmutableArray<TypeParameterSymbol> typeParameters)
		: base(name, node.Visibility, typeParameters)
	{
		Node = node;
		Members = members.ToImmutableArray();
		NestedTypes = nestedTypes.ToImmutableArray();
	}
	
	public RecordSymbol(RecordSymbol definition, ImmutableArray<TypeSymbol> typeArguments,
		IEnumerable<MemberSymbol> members)
		: base(definition, typeArguments)
	{
		Node = definition.Node;
		Members = members.ToImmutableArray();
		NestedTypes = definition.NestedTypes;
	}
}

public sealed class EnumSymbol : NamedTypeSymbol
{
	public EnumNode Node { get; }
	public ImmutableArray<EnumCaseSymbol> Cases { get; }
	public ImmutableArray<MethodSymbol> Functions { get; }
	public bool HasPayload => Cases.Any(static c => c.Fields.Length > 0);
	public bool IsExternal => Node.IsExternal;
	public bool IsMatch => Node.IsMatch;
	public override bool IsRef => Node.IsRef;
	
	public EnumSymbol(EnumNode node, IEnumerable<EnumCaseSymbol> cases, IEnumerable<MethodSymbol> functions,
		ImmutableArray<TypeParameterSymbol> typeParameters)
		: base(node.Identifier.Text, node.Visibility, typeParameters)
	{
		Node = node;
		Cases = cases.ToImmutableArray();
		Functions = functions.ToImmutableArray();
	}
	
	public EnumSymbol(EnumSymbol definition, ImmutableArray<TypeSymbol> typeArguments,
		IEnumerable<EnumCaseSymbol> cases)
		: base(definition, typeArguments)
	{
		Node = definition.Node;
		Cases = cases.ToImmutableArray();
		Functions = definition.Functions;
	}
	
	public override IEnumerable<MethodSymbol> GetFunctions(string name) => Functions.Where(f => f.Name == name);
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
	public Visibility Visibility { get; } = Visibility.FromKeyword(node?.Visibility);
	public Visibility WriteVisibility { get; } = Visibility.FromKeyword(node?.WriteVisibility ?? node?.Visibility);
}

public sealed class PropertySymbol(string name) : TypedMemberSymbol(name)
{
	public FieldSymbol? BackingField { get; init; }
	public AccessorImpl? Getter { get; init; }
	public AccessorImpl? Setter { get; init; }
	public PropertyNode? Node { get; init; }
	public Visibility Visibility { get; init; } = Visibility.Public;
	public TypeSymbol? ContainingType { get; internal set; }
	public TraitSymbol? Trait { get; internal set; }
	public ImmutableArray<TypeSymbol> TraitArguments { get; init; }
	public ImplSymbol? Impl { get; internal set; }
	public bool IsStatic => (Getter ?? Setter) is FunctionAccessor { Function.Kind: FunctionKind.Free };
}

public sealed class IndexerSymbol(string name, IEnumerable<ParameterSymbol> parameters) : TypedMemberSymbol(name)
{
	public ImmutableArray<ParameterSymbol> Parameters { get; } = parameters.ToImmutableArray();
	public AccessorImpl? Getter { get; init; }
	public AccessorImpl? Setter { get; init; }
}

public sealed class TraitSymbol
(
	TraitNode node,
	TypeParameterSymbol self,
	IEnumerable<MethodSymbol> functions,
	IEnumerable<PropertySymbol> properties,
	IEnumerable<FunctionSymbol> constructors
) : Symbol(node.Identifier.Text), IExportable
{
	public TraitNode Node { get; } = node;
	public TypeParameterSymbol Self { get; } = self;
	public ImmutableArray<TypeParameterSymbol> TypeParameters { get; init; } = [];
	public ImmutableArray<MethodSymbol> Functions { get; } = functions.ToImmutableArray();
	public ImmutableArray<PropertySymbol> Properties { get; } = properties.ToImmutableArray();
	public ImmutableArray<FunctionSymbol> Constructors { get; } = constructors.ToImmutableArray();
	public Visibility Visibility { get; } = Visibility.FromKeyword(node.Visibility);
	
	public IEnumerable<MethodSymbol> GetFunctions(string name) => Functions.Where(f => f.Name == name);
	public PropertySymbol? GetProperty(string name) => Properties.FirstOrDefault(p => p.Name == name);
}

public sealed class ImplSymbol
(
	ImplNode node,
	ImmutableArray<TypeParameterSymbol> typeParameters,
	IEnumerable<MethodSymbol> functions,
	IEnumerable<PropertySymbol> properties
) : Symbol("impl")
{
	public ImplNode Node { get; } = node;
	public ImmutableArray<TypeParameterSymbol> TypeParameters { get; } = typeParameters;
	public ImmutableArray<MethodSymbol> Functions { get; } = functions.ToImmutableArray();
	public ImmutableArray<PropertySymbol> Properties { get; } = properties.ToImmutableArray();
}

public sealed class MethodSymbol(string name, FunctionSymbol function) : MemberSymbol(name)
{
	public FunctionSymbol Function { get; } = function;
	public ImmutableArray<TypeSymbol> TraitArguments { get; init; }
	public bool HasReceiver => Function.Kind is not FunctionKind.Free;
}

public abstract record AccessorImpl;
public sealed record NativeAccessor(NativeMemberIntrinsic Intrinsic) : AccessorImpl;
public sealed record FunctionAccessor(FunctionSymbol Function) : AccessorImpl;

public sealed class NativeConstructorNode(NativeMemberIntrinsic intrinsic) : IDeclarationNode
{
	public NativeMemberIntrinsic Intrinsic { get; } = intrinsic;
	public SourceLocation SourceLocation => SourceLocation.None;
}

public sealed class CapturedSymbol(string name) : Symbol(name);

// TODO Throw errors when symbols resolved as ambiguous
public sealed class AmbiguousSymbol(string name, IEnumerable<Symbol> candidates) : Symbol(name)
{
	public ImmutableArray<Symbol> Candidates { get; } = candidates.ToImmutableArray();
}

public enum NativeMemberIntrinsic
{
	ArrayLength,
	StrByteLength,
	StrData,
	StrNew,
	FStrHoles,
	ArrayIndexGet,
	ArrayIndexSet,
	SpanIndexGet,
	SpanIndexSet,
	ViewIndexGet
}