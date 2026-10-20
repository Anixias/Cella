using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public ConversionTable ConversionTable { get; }
	public OperatorRegistry OperatorRegistry { get; }
	public SizeTable SizeTable { get; }
	public Action<TypeSymbol>? TypeCompleter { get; set; }
	public Action<TraitSymbol>? TraitCompleter { get; set; }
	public Func<BigInteger, IntegerStorageType, bool>? IntegerFits { get; set; }
	
	public List<(TypeSymbol Type, SourceLocation Location, FunctionSymbol? Owner, ErrorContainment.Owner ErrorOwner)>
		SizeChecks { get; } = [];
	
	public Dictionary<FunctionSymbol, List<(TypeSymbol Type, SourceLocation Location)>> GenericSizeChecks { get; } = [];
	public Dictionary<FunctionSymbol, List<(TypeSymbol Type, SourceLocation Location)>> GenericNullChecks { get; } = [];
	
	private readonly Dictionary<TypeSymbol, OrderedDictionary<string, MemberSymbol>> _members = [];
	private readonly Dictionary<(TypeSymbol, BigInteger), ArrayType> _arrayTypes = [];
	private readonly Dictionary<(TypeSymbol, TypeParameterSymbol), ArrayType> _parametricArrayTypes = [];
	private readonly Dictionary<BigInteger, ValueArgumentType> _valueArguments = [];
	private readonly Dictionary<TypeSymbol, PointerType> _pointerTypes = [];
	private readonly Dictionary<(TypeSymbol, bool), BorrowType> _borrowTypes = [];
	private readonly Dictionary<TraitType, DynType> _dynTypes = [];
	private readonly Dictionary<TypeParameterSymbol, DynType> _parameterDynTypes = [];
	private readonly Dictionary<TraitSymbol, List<TraitType>> _traitTypes = [];
	private readonly Dictionary<DynType, FStrType> _fstrTypes = [];
	private readonly Dictionary<TraitSymbol, ImmutableArray<FunctionSymbol>> _dynMembers = [];
	private readonly Dictionary<TraitSymbol, ImmutableArray<FunctionInfo>> _dynMemberInfos = [];
	private readonly List<FunctionType> _functionTypes = [];
	private readonly List<ClosureType> _closureTypes = [];
	private readonly Dictionary<FunctionType, DynType> _functionDynTypes = [];
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<FunctionType>> _functionBounds = [];
	private readonly Dictionary<TypedMemberSymbol, TypeSymbol> _memberTypes = [];
	private readonly Dictionary<TypeSymbol, TypeFacts> _facts = [];
	private readonly Dictionary<TypeSymbol, List<FunctionInfo>> _constructors = [];
	private readonly Dictionary<TypeSymbol, List<FunctionInfo>> _explicitConversions = [];
	private readonly Dictionary<TraitSymbol, List<FunctionInfo>> _traitConstructors = [];
	private readonly Dictionary<TypeSymbol, FunctionInfo> _destructors = [];
	private readonly Dictionary<FunctionSymbol, FunctionInfo> _nativeFunctions = [];
	private readonly Dictionary<EnumSymbol, IntegerType> _tagTypes = [];
	private readonly Dictionary<EnumCaseSymbol, BigInteger> _caseValues = [];
	private readonly Dictionary<EnumSymbol, TypeSymbol> _matchedTypes = [];
	private readonly Dictionary<EnumCaseSymbol, ImmutableArray<BigInteger>> _matchValues = [];
	private readonly Dictionary<NamedTypeSymbol, List<NamedTypeSymbol>> _instances = [];
	private readonly HashSet<NamedTypeSymbol> _completedInstances = [];
	private readonly HashSet<NamedTypeSymbol> _borrowChecks = [];
	private readonly Dictionary<FunctionSymbol, List<FunctionInfo>> _functionInstances = [];
	private readonly Dictionary<FunctionSymbol, FunctionInfo> _genericFunctions = [];
	private readonly Dictionary<GlobalSymbol, GlobalInfo> _genericGlobals = [];
	private readonly Dictionary<GlobalSymbol, GlobalInfo> _globalInstances = [];
	private readonly List<Conformance> _conformances = [];
	private readonly Dictionary<TypeSymbol, List<Conformance>> _conformancesByTarget = [];
	private readonly List<Conformance> _arrayConformances = [];
	private readonly List<Conformance> _pointerConformances = [];
	private readonly List<Conformance> _blanketConformances = [];
	public Func<TypeSymbol, GlobalSymbol, Constants.Constant?>? TraitConstantValue { get; set; }
	public Func<GlobalSymbol, TypeSymbol>? GlobalTypeOf { get; set; }
	
	private readonly Dictionary<(TypeSymbol Owner, GlobalSymbol Constant), TypeParameterSymbol> _constantProjections =
		[];
	
	private readonly HashSet<TypeParameterSymbol> _blanketBoundsInProgress = [];
	private readonly HashSet<(TypeSymbol Type, TraitSymbol Trait)> _blanketLookupsInProgress = [];
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<TraitType>> _bounds = [];
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<TypeParameterSymbol>> _parameterBounds = [];
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<FunctionInfo>> _constructorBounds = [];
	private readonly Dictionary<TraitSymbol, ImmutableArray<TraitType>> _requiredTraits = [];
	private readonly List<(ImplSymbol Impl, TypeSymbol Target)> _memberBlocks = [];
	
	public TypePool(ConversionTable conversionTable, OperatorRegistry operatorRegistry, SizeTable sizeTable)
	{
		ConversionTable = conversionTable;
		OperatorRegistry = operatorRegistry;
		SizeTable = sizeTable;
		SizeTable.Completer = Complete;
		ConversionTable.CallableAs = IsCallableAs;
		
		CreateNativeMembers();
	}
	
	public void AddConstructor(TypeSymbol type, FunctionInfo info) => _constructors.GetOrAdd(type).Add(info);
	
	public void AddExplicitConversion(TypeSymbol type, FunctionInfo info) =>
		_explicitConversions.GetOrAdd(type).Add(info);
	
	public void AddTraitConstructor(TraitSymbol trait, FunctionInfo info) =>
		_traitConstructors.GetOrAdd(trait).Add(info);
	
	public void CreateNativeMembers()
	{
		// ptr <-> usize/isize
		var ptr = NativeSymbols.VoidPtr;
		ConversionTable.Add(new NativeConversion(ptr, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, ptr, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(ptr, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, ptr, ConversionKind.Explicit, 0));
		
		// cstr <-> ptr[i8]/ptr[u8]/usize/isize
		var cstr = NativeSymbols.CStr;
		var ptrI8 = GetPointerType(NativeSymbols.Int8);
		ConversionTable.Add(new FreeConversion(cstr, ptrI8, ConversionKind.Implicit));
		ConversionTable.Add(new FreeConversion(ptrI8, cstr, ConversionKind.Explicit));
		var ptrU8 = GetPointerType(NativeSymbols.UInt8);
		ConversionTable.Add(new FreeConversion(cstr, ptrU8, ConversionKind.Implicit));
		ConversionTable.Add(new FreeConversion(ptrU8, cstr, ConversionKind.Explicit));
		ConversionTable.Add(new NativeConversion(cstr, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, cstr, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(cstr, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, cstr, ConversionKind.Explicit, 0));
		
		CreateStrMembers();
		
		// Operations
		CreatePointerArithmetic(NativeSymbols.VoidPtr);
		OperatorRegistry.CreatePointerComparisons(cstr);
	}
	
	private void CreateStrMembers()
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new PropertySymbol("byteLength")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.StrByteLength)
		};
		
		RegisterMember(NativeSymbols.Str, length, lengthType);
		
		var ptrType = GetPointerType(NativeSymbols.UInt8);
		var data = new PropertySymbol("data")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.StrData)
		};
		
		RegisterMember(NativeSymbols.Str, data, ptrType);
		
		ParameterSymbol[] parameters =
		[
			new("self", SourceLocation.None) { Mode = ParameterMode.Mut },
			new("data", SourceLocation.None),
			new("byteLength", SourceLocation.None)
		];
		
		var constructor = new FunctionSymbol("str.new", new NativeConstructorNode(NativeMemberIntrinsic.StrNew),
			Visibility.Public, null, parameters, FunctionKind.Constructor);
		
		var signature = new FunctionSignature([GetPointerType(NativeSymbols.Str), ptrType, lengthType],
			NativeSymbols.Void, false, [ParameterMode.Mut, ParameterMode.ReadOnly, ParameterMode.ReadOnly]);
		
		AddConstructor(NativeSymbols.Str, new FunctionInfo("str.new", constructor, signature, null, null, null));
	}
	
	public void CreateArrayMembers(ArrayType type)
	{
		var lengthType = NativeSymbols.UIntSize;
		var length = new PropertySymbol("length")
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.ArrayLength)
		};
		
		RegisterMember(type, length, lengthType);
	}
	
	private readonly record struct TypeFacts(bool NeedsDrop, bool IsCopy, bool HasDefault)
	{
		public static TypeFacts Plain => new(false, true, true);
	}
}