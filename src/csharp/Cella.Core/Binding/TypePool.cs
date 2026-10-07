using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed class TypePool
{
	public ConversionTable ConversionTable { get; }
	public OperatorRegistry OperatorRegistry { get; }
	public SizeTable SizeTable { get; }
	public Action<TypeSymbol>? TypeCompleter { get; set; }
	
	private readonly Dictionary<TypeSymbol, OrderedDictionary<string, MemberSymbol>> _members = [];
	private readonly Dictionary<(TypeSymbol, BigInteger), ArrayType> _arrayTypes = [];
	private readonly Dictionary<TypeSymbol, PointerType> _pointerTypes = [];
	private readonly Dictionary<(TypeSymbol, bool), BorrowType> _borrowTypes = [];
	private readonly Dictionary<TraitSymbol, DynType> _dynTypes = [];
	private readonly Dictionary<TypeParameterSymbol, DynType> _parameterDynTypes = [];
	private readonly Dictionary<TraitSymbol, TraitType> _traitTypes = [];
	private readonly Dictionary<TraitSymbol, ImmutableArray<FunctionSymbol>> _dynMembers = [];
	private readonly Dictionary<TraitSymbol, ImmutableArray<FunctionInfo>> _dynMemberInfos = [];
	private readonly List<FunctionType> _functionTypes = [];
	private readonly Dictionary<TypedMemberSymbol, TypeSymbol> _memberTypes = [];
	private readonly Dictionary<TypeSymbol, TypeFacts> _facts = [];
	private readonly Dictionary<TypeSymbol, List<FunctionInfo>> _constructors = [];
	private readonly Dictionary<TypeSymbol, FunctionInfo> _destructors = [];
	private readonly Dictionary<TypeSymbol, IReadOnlySet<FieldSymbol>> _destructorMoves = [];
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
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<TraitSymbol>> _bounds = [];
	private readonly Dictionary<TypeParameterSymbol, ImmutableArray<TypeParameterSymbol>> _parameterBounds = [];
	private readonly List<(ImplSymbol Impl, TypeSymbol Target)> _memberBlocks = [];
	private const int MaxInstanceDepth = 32;
	
	public TypePool(ConversionTable conversionTable, OperatorRegistry operatorRegistry, SizeTable sizeTable)
	{
		ConversionTable = conversionTable;
		OperatorRegistry = operatorRegistry;
		SizeTable = sizeTable;
		SizeTable.Completer = Complete;
		
		CreateNativeMembers();
	}
	
	public void AddConstructor(TypeSymbol type, FunctionInfo info) => _constructors.GetOrAdd(type).Add(info);
	
	public void AddConformance(Conformance conformance) => _conformances.Add(conformance);
	
	public IEnumerable<Conformance> FindConformances(TypeSymbol type) =>
		_conformances.Where(conformance => Matches(conformance, type));
	
	public void SetBounds(TypeParameterSymbol parameter, ImmutableArray<TraitSymbol> traits) =>
		_bounds[parameter] = traits;
	
	public ImmutableArray<TraitSymbol> GetBounds(TypeParameterSymbol parameter) =>
		_bounds.GetValueOrDefault(parameter, []);
	
	public void SetParameterBounds(TypeParameterSymbol parameter, ImmutableArray<TypeParameterSymbol> traits) =>
		_parameterBounds[parameter] = traits;
	
	public ImmutableArray<TypeParameterSymbol> GetParameterBounds(TypeParameterSymbol parameter) =>
		_parameterBounds.GetValueOrDefault(parameter, []);
	
	public bool Conforms(TypeSymbol type, TraitSymbol trait) => type switch
	{
		InvalidType => true,
		TypeParameterSymbol parameter => GetBounds(parameter).Contains(trait),
		_ => FindConformance(type, trait) is not null
	};
	
	public bool Conforms(TypeSymbol type, TypeParameterSymbol trait) => type switch
	{
		InvalidType => true,
		TypeParameterSymbol parameter => GetParameterBounds(parameter).Contains(trait),
		_ => false
	};
	
	public bool Conforms(TypeSymbol type, DynType dyn) =>
		dyn.Trait is { } trait ? Conforms(type, trait) : Conforms(type, dyn.Parameter!);
	
	public Conformance? FindConformance(TypeSymbol type, TraitSymbol trait) =>
		_conformances.FirstOrDefault(conformance => conformance.Trait == trait && Matches(conformance, type));
	
	private bool Matches(Conformance conformance, TypeSymbol type) =>
		type is not TypeParameterSymbol && conformance.Target == type.OriginalDefinition &&
		Satisfies(conformance.Parameters, type.TypeArguments);
	
	private bool Satisfies(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TypeSymbol> arguments) =>
		arguments.Length == parameters.Length && FindViolation(parameters, arguments) is null;
	
	private string? FindViolation(ImmutableArray<TypeParameterSymbol> parameters, ImmutableArray<TypeSymbol> arguments)
	{
		var map = CreateMap(parameters, arguments);
		return parameters.Select((parameter, i) => FindConstraintViolation(parameter, arguments[i], map))
			.FirstOrDefault(static violation => violation is not null);
	}
	
	public void AddMemberBlock(ImplSymbol impl, TypeSymbol target) => _memberBlocks.Add((impl, target));
	
	public IEnumerable<ImplSymbol> FindMemberBlocks(TypeSymbol type) => type is TypeParameterSymbol
		? []
		: _memberBlocks
			.Where(block => block.Target == type.OriginalDefinition &&
			                Satisfies(block.Impl.TypeParameters, type.TypeArguments))
			.Select(static block => block.Impl);
	
	public string? FindBlockViolation(TypeSymbol type, string name) => type is TypeParameterSymbol
		? null
		: _memberBlocks
			.Where(block => block.Target == type.OriginalDefinition &&
			                block.Impl.TypeParameters.Length == type.TypeArguments.Length &&
			                (block.Impl.Functions.Any(method => method.Name == name) ||
			                 block.Impl.Properties.Any(property => property.Name == name)))
			.Select(block => FindViolation(block.Impl.TypeParameters, type.TypeArguments))
			.FirstOrDefault(static violation => violation is not null);
	
	public IEnumerable<FunctionSymbol> GetWitnessFunctions() => _conformances
		.SelectMany(static conformance => conformance.Witnesses.Values)
		.OfType<FunctionWitness>()
		.Select(static witness => witness.Function);
	
	public Witness? FindWitness(TypeSymbol self, FunctionSymbol requirement) =>
		requirement.Trait is { } trait && FindConformance(self, trait) is { } conformance
			? conformance.Witnesses.GetValueOrDefault(requirement)
			: null;
	
	public ImmutableArray<TypeSymbol> GetWitnessArguments(TypeSymbol self, FunctionSymbol witness,
		IEnumerable<TypeSymbol> declared)
	{
		if (witness.Trait is not null)
			return [self, ..declared];
		
		if (witness.Impl is null || FindConformance(self, witness.Impl) is not { } conformance)
			return [..self.TypeArguments, ..declared];
		
		var map = CreateMap(conformance.Parameters, self.TypeArguments);
		return [..witness.Impl.TypeParameters.Select(parameter => map[parameter]), ..declared];
	}
	
	private Conformance? FindConformance(TypeSymbol self, ImplSymbol impl) =>
		_conformances.FirstOrDefault(conformance => conformance.Impl == impl && Matches(conformance, self));
	
	public IReadOnlyList<FunctionInfo> GetConstructors(TypeSymbol type)
	{
		Complete(type);
		return _constructors.TryGetValue(type, out var list) ? list : [];
	}
	
	public void SetDestructor(TypeSymbol type, FunctionInfo info) => _destructors[type] = info;
	
	public FunctionInfo? GetDestructor(TypeSymbol type)
	{
		Complete(type);
		return _destructors.TryGetValue(type, out var info) ? info : null;
	}
	
	public void SetDestructorMoves(TypeSymbol type, IReadOnlySet<FieldSymbol> fields) =>
		_destructorMoves[type] = fields;
	
	public bool IsMovedByDestructor(TypeSymbol type, FieldSymbol field) =>
		_destructorMoves.TryGetValue(type.OriginalDefinition, out var fields) &&
		fields.Any(moved => moved.Name == field.Name);
	
	public bool NeedsDrop(TypeSymbol type) => GetFacts(type).NeedsDrop;
	public bool IsCopy(TypeSymbol type) => GetFacts(type).IsCopy;
	public bool HasDefault(TypeSymbol type) => GetFacts(type).HasDefault;
	public bool HasDefault(FieldSymbol field) => !field.IsRequired && HasDefault(GetTypeOfMember(field));
	
	public bool HoldsBorrows(TypeSymbol type) => type switch
	{
		BorrowType or DynType => true,
		TypeParameterSymbol parameter => !parameter.IsNoref,
		StringType => type == NativeSymbols.Str,
		NamedTypeSymbol { TypeArguments.IsEmpty: false } named => named.IsRef || PartsHoldBorrows(named),
		NamedTypeSymbol named => named.IsRef,
		ArrayType array => HoldsBorrows(array.ElementType),
		_ => false
	};
	
	private bool PartsHoldBorrows(NamedTypeSymbol type)
	{
		if (!_borrowChecks.Add(type))
			return false;
		
		var holds = GetParts(type).Any(HoldsBorrows);
		_borrowChecks.Remove(type);
		return holds;
	}
	
	public bool PassesByPointer(TypeSymbol type, ParameterMode mode) =>
		mode == ParameterMode.ReadOnly && NeedsDrop(type);
	
	public bool IsViewRecord(TypeSymbol type) => type is RecordSymbol { IsRef: true } or EnumSymbol { IsRef: true } &&
	                                             IsCopy(type);
	
	public bool HasDestructor(TypeSymbol type) => HasDestructor(type, []);
	
	private bool HasDestructor(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
	{
		InvalidType => true,
		TypeParameterSymbol parameter => parameter.HasDrop,
		RecordSymbol { HasDestructor: true } => true,
		_ => visited.Add(type) && GetParts(type).Any(part => HasDestructor(part, visited))
	};
	
	public bool HasNew(TypeSymbol type) => type switch
	{
		InvalidType => true,
		TypeParameterSymbol parameter => parameter.HasNew,
		TraitType => false,
		_ when GetConstructors(type).Count > 0 => FindNewConstructor(type) is not null,
		RecordSymbol record => GetMembers(record).OfType<FieldSymbol>().All(HasDefault),
		_ => HasDefault(type)
	};
	
	public FunctionInfo? FindNewConstructor(TypeSymbol type)
	{
		var floor = type is NamedTypeSymbol named
			? (Visibility)Math.Max((int)named.Definition.Visibility, (int)Visibility.Module)
			: Visibility.Public;
		
		return GetConstructors(type)
			.Where(constructor => constructor.Signature.ParameterTypes.Length == 1 &&
			                      constructor.Symbol.Visibility >= floor)
			.Select(static constructor => (FunctionInfo?)constructor)
			.FirstOrDefault();
	}
	
	public RecordSymbol? FindDestructor(TypeSymbol type) => FindDestructor(type, []);
	
	private RecordSymbol? FindDestructor(TypeSymbol type, HashSet<TypeSymbol> visited)
	{
		if (!NeedsDrop(type) || !visited.Add(type))
			return null;
		
		if (type is RecordSymbol { HasDestructor: true } record)
			return record;
		
		return GetParts(type)
			.Select(part => FindDestructor(part, visited))
			.FirstOrDefault(static found => found is not null);
	}
	
	private TypeFacts GetFacts(TypeSymbol type)
	{
		if (_facts.TryGetValue(type, out var facts))
			return facts;
		
		_facts[type] = TypeFacts.Plain;
		facts = type switch
		{
			RecordSymbol r => Combine(r.HasDestructor, GetParts(r)) with
			{
				HasDefault = !r.HasDestructor && GetMembers(r).OfType<FieldSymbol>().All(HasDefault)
			},
			EnumSymbol e => Combine(false, GetParts(e)) with
			{
				HasDefault = FindCase(e, BigInteger.Zero) is { } zeroCase ? zeroCase.Fields.IsEmpty : e.IsExternal
			},
			ArrayType => Combine(false, GetParts(type)),
			FunctionType or BorrowType => TypeFacts.Plain with { HasDefault = false },
			TypeParameterSymbol parameter => new(!parameter.IsCopy, parameter.IsCopy, false),
			DynType => new(true, false, false),
			_ => TypeFacts.Plain
		};
		
		_facts[type] = facts;
		return facts;
	}
	
	private IEnumerable<TypeSymbol> GetParts(TypeSymbol type) => type switch
	{
		RecordSymbol r => GetMembers(r).OfType<FieldSymbol>().Select(GetTypeOfMember),
		EnumSymbol e => e.Cases.SelectMany(c => GetPayloadTypes(e, c)),
		ArrayType array => [array.ElementType],
		_ => []
	};
	
	private TypeFacts Combine(bool hasDestructor, IEnumerable<TypeSymbol> parts)
	{
		var facts = parts.Select(GetFacts).ToArray();
		return new(hasDestructor || facts.Any(static f => f.NeedsDrop),
			!hasDestructor && facts.All(static f => f.IsCopy),
			facts.All(static f => f.HasDefault));
	}
	
	public static IReadOnlyDictionary<string, string> BuiltinGenericTypeArguments { get; } =
		new Dictionary<string, string>
		{
			["ptr"] = "one type argument",
			["array"] = "an element type and an optional length"
		};
	
	public TypeSymbol? ResolveBuiltinGenericType(string name, IReadOnlyList<IGenericArgument> typeArgs) => name switch
	{
		"ptr" when typeArgs.Count == 0 => NativeSymbols.VoidPtr,
		"ptr" when typeArgs is [GenericTypeArgument { Type: { } t }] => GetPointerType(t),
		"array" when typeArgs is [GenericTypeArgument { Type: { } t }, GenericConstArgument { Value: var v }] =>
			GetArrayType(t, v),
		"array" when typeArgs is [GenericTypeArgument { Type: { } t }] =>
			new ArrayType(t, BigInteger.MinusOne), // Don't use GetArrayType; we don't want to actually create it
		_ => null
	};
	
	private void CreatePointerArithmetic(PointerType ptrType)
	{
		foreach (var offsetType in new[] { NativeSymbols.IntSize, NativeSymbols.UIntSize })
		{
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpPlus, ptrType, offsetType));
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpMinus, ptrType, offsetType));
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpPlusEqual, ptrType, offsetType));
			OperatorRegistry.Create(new PointerOffsetImpl(TokenType.OpMinusEqual, ptrType, offsetType));
		}
		
		OperatorRegistry.Create(new PointerDifferenceImpl(ptrType));
	}
	
	public FunctionType GetFunctionType(bool isExternal, IEnumerable<TypeSymbol> parameterTypes,
		IEnumerable<ParameterMode> parameterModes, TypeSymbol returnType)
	{
		var parameters = parameterTypes.ToImmutableArray();
		var modes = parameterModes.ToImmutableArray();
		var existing = _functionTypes.Find(type => type.IsExternal == isExternal && type.ReturnType == returnType &&
		                                           type.ParameterTypes.SequenceEqual(parameters) &&
		                                           type.ParameterModes.SequenceEqual(modes));
		
		if (existing is not null)
			return existing;
		
		var functionType = new FunctionType(isExternal, parameters, modes, returnType);
		_functionTypes.Add(functionType);
		var size = isExternal ? StorageSize.Ptr : (ISize)StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr);
		SizeTable.Register(functionType, size);
		
		if (isExternal)
			ConversionTable.Add(new FreeConversion(functionType, NativeSymbols.VoidPtr, ConversionKind.Explicit));
		
		return functionType;
	}
	
	public TypeSymbol GetPassedType(TypeSymbol type, ParameterMode mode) =>
		mode == ParameterMode.Mut && type is not InvalidType ? GetPointerType(type) : type;
	
	public PointerType GetPointerType(TypeSymbol baseType)
	{
		if (_pointerTypes.TryGetValue(baseType, out var existing))
			return existing;
		
		var ptrType = new PointerType(baseType);
		_pointerTypes[baseType] = ptrType;
		if (baseType is DynType)
		{
			SizeTable.Register(ptrType, FatPointerSize);
			return ptrType;
		}
		
		SizeTable.Register(ptrType, StorageSize.Ptr);
		
		// All pointers can be implicitly converted to ptr / explicitly converted from ptr
		ConversionTable.Add(new NativeConversion(ptrType, PointerType.VoidPtr, ConversionKind.Implicit, 1));
		ConversionTable.Add(new NativeConversion(PointerType.VoidPtr, ptrType, ConversionKind.Explicit, 0));
		
		// All pointers can be explicitly converted to/from usize and isize
		ConversionTable.Add(new NativeConversion(ptrType, NativeSymbols.UIntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.UIntSize, ptrType, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(ptrType, NativeSymbols.IntSize, ConversionKind.Explicit, 0));
		ConversionTable.Add(new NativeConversion(NativeSymbols.IntSize, ptrType, ConversionKind.Explicit, 0));
		
		// Pointer arithmetic
		CreatePointerArithmetic(ptrType);
		
		return ptrType;
	}
	
	public BorrowType GetBorrowType(TypeSymbol target, bool isMutable)
	{
		if (_borrowTypes.TryGetValue((target, isMutable), out var existing))
			return existing;
		
		var borrowType = new BorrowType(target, isMutable);
		_borrowTypes[(target, isMutable)] = borrowType;
		SizeTable.Register(borrowType, target is DynType ? FatPointerSize : StorageSize.Ptr);
		if (isMutable)
			ConversionTable.Add(new FreeConversion(borrowType, GetBorrowType(target, false), ConversionKind.Implicit));
		
		return borrowType;
	}
	
	private static ISize FatPointerSize => StorageSize.Sum(StorageSize.Ptr, StorageSize.Ptr);
	
	public static bool IsFatPointer(TypeSymbol type) =>
		type is PointerType { BaseType: DynType } or BorrowType { Target: DynType };
	
	public DynType GetDynType(TraitSymbol trait)
	{
		if (_dynTypes.TryGetValue(trait, out var existing))
			return existing;
		
		var dynType = new DynType(trait);
		_dynTypes[trait] = dynType;
		return dynType;
	}
	
	public DynType GetDynType(TypeParameterSymbol parameter)
	{
		if (_parameterDynTypes.TryGetValue(parameter, out var existing))
			return existing;
		
		var dynType = new DynType(parameter);
		_parameterDynTypes[parameter] = dynType;
		return dynType;
	}
	
	public TraitType GetTraitType(TraitSymbol trait)
	{
		if (_traitTypes.TryGetValue(trait, out var existing))
			return existing;
		
		var traitType = new TraitType(trait);
		_traitTypes[trait] = traitType;
		return traitType;
	}
	
	public ImmutableArray<FunctionSymbol>? FindDynMembers(TraitSymbol trait) =>
		_dynMembers.TryGetValue(trait, out var members) ? members : null;
	
	public ImmutableArray<FunctionInfo> GetDynMemberInfos(TraitSymbol trait) => _dynMemberInfos[trait];
	
	public void SetDynMembers(TraitSymbol trait, ImmutableArray<FunctionInfo> members)
	{
		_dynMembers[trait] = [..members.Select(static member => member.Symbol)];
		_dynMemberInfos[trait] = members;
	}
	
	public int GetDynSlot(TraitSymbol trait, FunctionSymbol member) => _dynMembers[trait].IndexOf(member);
	
	public static DynType? FindValueDyn(TypeSymbol type) => type switch
	{
		DynType dyn => dyn,
		ArrayType array => FindValueDyn(array.ElementType),
		FunctionType function => FindValueDyn(function.ReturnType) ?? function.ParameterTypes
			.Where((_, i) => function.ParameterModes[i] == ParameterMode.Own)
			.Select(FindValueDyn)
			.FirstOrDefault(static dyn => dyn is not null),
		_ => null
	};
	
	public ArrayType GetArrayType(TypeSymbol elementType, BigInteger length)
	{
		var key = (elementType, length);
		if (_arrayTypes.TryGetValue(key, out var existing))
			return existing;
		
		var arrayType = new ArrayType(elementType, length);
		_arrayTypes[key] = arrayType;
		CreateArrayMembers(arrayType);
		SizeTable.Register(arrayType,
			() => StorageSize.Product(SizeTable.TryGetSize(elementType) ?? StorageSize.Const(0), length));
		
		// To pointer
		var arrayPtrType = GetPointerType(arrayType);
		var elementPtrType = GetPointerType(elementType);
		ConversionTable.Add(new NativeConversion(arrayPtrType, elementPtrType, ConversionKind.Implicit, 1));
		
		return arrayType;
	}
	
	public void RegisterRecord(RecordSymbol record) => SizeTable.Register(record, () =>
	{
		var fieldSizes = GetMembers(record)
			.OfType<FieldSymbol>()
			.Select(GetTypeOfMember)
			.Select(type => SizeTable.TryGetSize(type) ?? StorageSize.Const(0));
		
		return StorageSize.Sum(fieldSizes);
	});
	
	public void RegisterEnum(EnumSymbol enumType, IntegerType tagType, IReadOnlyList<BigInteger> values)
	{
		_tagTypes[enumType] = tagType;
		for (var i = 0; i < values.Count; i++)
			_caseValues[enumType.Cases[i]] = values[i];
		
		SizeTable.Register(enumType, () =>
		{
			var tagSize = SizeTable.GetSize(tagType);
			if (!enumType.HasPayload)
				return tagSize;
			
			var payloadSizes = enumType.Cases.Select(ISize (enumCase) => StorageSize.Sum(enumCase.Fields
				.Select(field => SizeTable.TryGetSize(GetPayloadType(field)) ?? StorageSize.Const(0))));
			
			return StorageSize.Sum(tagSize, StorageSize.Max(payloadSizes));
		});
		
		if (enumType.HasPayload)
			return;
		
		OperatorRegistry.Create(new NativeImpl(TokenType.OpEqualEqual, NativeSymbols.Bool, enumType, enumType));
		OperatorRegistry.Create(new NativeImpl(TokenType.OpBangEqual, NativeSymbols.Bool, enumType, enumType));
	}
	
	public void RegisterMatchEnum(EnumSymbol enumType, TypeSymbol matchedType,
		IReadOnlyList<ImmutableArray<BigInteger>> values)
	{
		_matchedTypes[enumType] = matchedType;
		_tagTypes[enumType] = enumType.Cases.Length <= byte.MaxValue + 1 ? NativeSymbols.UInt8 : NativeSymbols.UInt32;
		for (var i = 0; i < values.Count; i++)
			_matchValues[enumType.Cases[i]] = values[i];
		
		SizeTable.Register(enumType, () => SizeTable.TryGetSize(matchedType) ?? StorageSize.Const(0));
		ConversionTable.Add(new MatchConversion(matchedType, enumType));
		if (matchedType is IntegerType or PointerType || matchedType == NativeSymbols.Bool ||
		    matchedType == NativeSymbols.CStr)
			ConversionTable.Add(new MatchConversion(enumType, matchedType));
		
		if (enumType.HasPayload)
			return;
		
		OperatorRegistry.Create(new NativeImpl(TokenType.OpEqualEqual, NativeSymbols.Bool, enumType, enumType));
		OperatorRegistry.Create(new NativeImpl(TokenType.OpBangEqual, NativeSymbols.Bool, enumType, enumType));
	}
	
	public TypeSymbol GetMatchedType(EnumSymbol enumType)
	{
		Complete(enumType);
		return _matchedTypes.GetValueOrDefault(enumType) ?? NativeSymbols.Invalid;
	}
	
	public ImmutableArray<BigInteger> GetMatchValues(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		Complete(enumType);
		return _matchValues.GetValueOrDefault(enumCase, []);
	}
	
	public static EnumCaseSymbol? GetElseCase(EnumSymbol enumType) =>
		enumType.Cases.FirstOrDefault(static enumCase => enumCase.Node.Else is not null);
	
	public static bool HasNull(TypeSymbol type) => type is PointerType or FunctionType or BorrowType
		                                               or TypeParameterSymbol { HasNull: true } ||
	                                               type == NativeSymbols.CStr;
	
	public string? FindConstraintViolation(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map = null) => argument switch
	{
		InvalidType => null,
		_ when parameter.IsTrait => IsTraitArgument(argument) ? null : $"'{argument.Name}' is not a trait",
		_ when IsTraitArgument(argument) => $"'{argument.Name}' is not a type",
		DynType => $"Cannot use '{argument.Name}' by value",
		_ when parameter.IsNoref && HoldsBorrows(argument) => $"Cannot store borrows in '{parameter.Name}'",
		_ when parameter.HasNull && !HasNull(argument) => $"'{argument.Name}' has no null",
		_ when parameter.IsCopy && !IsCopy(argument) => $"Cannot copy '{argument.Name}'",
		_ when parameter.HasDrop && !HasDestructor(argument) => $"'{argument.Name}' has no destructor",
		_ when parameter.HasNew && !HasNew(argument) => $"'{argument.Name}' has no constructor without parameters",
		_ when GetBounds(parameter).FirstOrDefault(trait => !Conforms(argument, trait)) is { } trait =>
			$"'{argument.Name}' doesn't implement '{trait.Name}'",
		_ when FindUnmetBound(parameter, argument, map) is { } trait =>
			$"'{argument.Name}' doesn't implement '{trait}'",
		_ => null
	};
	
	public static bool IsTraitArgument(TypeSymbol type) => type is TraitType or TypeParameterSymbol { IsTrait: true };
	
	private string? FindUnmetBound(TypeParameterSymbol parameter, TypeSymbol argument,
		IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol>? map)
	{
		foreach (var bound in GetParameterBounds(parameter))
		{
			switch (map?.GetValueOrDefault(bound) ?? bound)
			{
				case TraitType { Trait: var trait } when !Conforms(argument, trait):
					return trait.Name;
				
				case TypeParameterSymbol trait when !Conforms(argument, trait):
					return trait.Name;
			}
		}
		
		return null;
	}
	
	public void RegisterPayloadField(FieldSymbol field, TypeSymbol type) => _memberTypes[field] = type;
	
	public ImmutableArray<TypeSymbol> GetPayloadTypes(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		Complete(enumType);
		return [..enumCase.Fields.Select(GetPayloadType)];
	}
	
	public IEnumerable<FieldSymbol> GetPayloadFields(EnumSymbol enumType)
	{
		Complete(enumType);
		return enumType.Cases.SelectMany(static enumCase => enumCase.Fields);
	}
	
	private TypeSymbol GetPayloadType(FieldSymbol field) =>
		_memberTypes.GetValueOrDefault(field) ?? NativeSymbols.Invalid;
	
	public void RegisterEnumConversions(EnumSymbol enumType)
	{
		foreach (var integerType in NativeSymbols.PureIntegerTypes)
		{
			ConversionTable.Add(new EnumConversion(enumType, integerType));
			ConversionTable.Add(new EnumConversion(integerType, enumType));
		}
	}
	
	public IntegerType GetTagType(EnumSymbol enumType)
	{
		Complete(enumType);
		return _tagTypes.GetValueOrDefault(enumType, NativeSymbols.UInt8);
	}
	
	public BigInteger GetCaseValue(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		Complete(enumType);
		return _caseValues.GetValueOrDefault(enumCase, enumCase.Index);
	}
	
	public EnumCaseSymbol? FindCase(EnumSymbol enumType, BigInteger value) => enumType.IsMatch
		? enumType.Cases.FirstOrDefault(enumCase => GetMatchValues(enumType, enumCase).Contains(value)) ??
		  GetElseCase(enumType)
		: enumType.Cases.FirstOrDefault(enumCase => GetCaseValue(enumType, enumCase) == value);
	
	public void RegisterMember(TypeSymbol containingType, TypedMemberSymbol member, TypeSymbol memberType)
	{
		_members.GetOrAdd(containingType)[member.Name] = member;
		_memberTypes[member] = memberType;
	}
	
	public MemberSymbol? ResolveMember(TypeSymbol type, string name)
	{
		Complete(type);
		return _members.GetValueOrDefault(type)?.GetValueOrDefault(name);
	}
	
	public int GetFieldIndex(TypeSymbol type, MemberSymbol member)
	{
		Complete(type);
		return _members[type].IndexOf(member.Name);
	}
	
	public IReadOnlyList<MemberSymbol> GetMembers(TypeSymbol type)
	{
		Complete(type);
		return _members.TryGetValue(type, out var members) ? members.Values : [];
	}
	
	private void Complete(TypeSymbol type)
	{
		if (type is NamedTypeSymbol { IsGenericInstance: true } instance)
			CompleteInstance(instance);
		else if (type is NamedTypeSymbol)
			TypeCompleter?.Invoke(type);
	}
	
	public TypeSymbol Instantiate(NamedTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments)
	{
		if (typeArguments.Any(static argument => argument is InvalidType) ||
		    typeArguments.Max(GetInstanceDepth) >= MaxInstanceDepth)
			return NativeSymbols.Invalid;
		
		if (typeArguments.SequenceEqual(definition.TypeParameters))
			return definition;
		
		var instances = _instances.GetOrAdd(definition);
		if (instances.Find(instance => instance.TypeArguments.SequenceEqual(typeArguments)) is { } existing)
			return existing;
		
		var statics = definition.StaticFields
			.Select(static field => new GlobalSymbol(field.Syntax, field.Visibility))
			.ToImmutableArray();
		
		NamedTypeSymbol created = definition switch
		{
			RecordSymbol record => new RecordSymbol(record, typeArguments, record.Members.Select(CopyMember))
			{
				StaticFields = statics
			},
			EnumSymbol enumType => new EnumSymbol(enumType, typeArguments, enumType.Cases.Select(CopyCase))
			{
				StaticFields = statics
			},
			_ => throw new InvalidOperationException()
		};
		
		foreach (var field in statics)
			field.ContainingType = created;
		
		instances.Add(created);
		return created;
	}
	
	private static MemberSymbol CopyMember(MemberSymbol member) =>
		member is FieldSymbol field ? CopyField(field) : member;
	
	private static EnumCaseSymbol CopyCase(EnumCaseSymbol enumCase) =>
		new(enumCase.Node, enumCase.Index, enumCase.Fields.Select(CopyField));
	
	private static FieldSymbol CopyField(FieldSymbol field) => new(field.Name, field.Node, field.IsMutable);
	
	private static int GetInstanceDepth(TypeSymbol type) => type switch
	{
		NamedTypeSymbol { IsGenericInstance: true } named => 1 + named.TypeArguments.Max(GetInstanceDepth),
		PointerType pointer => GetInstanceDepth(pointer.BaseType),
		BorrowType borrow => GetInstanceDepth(borrow.Target),
		ArrayType array => GetInstanceDepth(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Max(GetInstanceDepth),
		_ => 0
	};
	
	private void CompleteInstance(NamedTypeSymbol instance)
	{
		if (!_completedInstances.Add(instance))
			return;
		
		var map = CreateMap(instance.Definition.TypeParameters, instance.TypeArguments);
		switch (instance)
		{
			case RecordSymbol record:
				CompleteRecordInstance(record, (RecordSymbol)record.Definition, map);
				break;
			
			case EnumSymbol enumType:
				CompleteEnumInstance(enumType, (EnumSymbol)enumType.Definition, map);
				break;
		}
	}
	
	private void CompleteRecordInstance(RecordSymbol instance, RecordSymbol definition,
		Dictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		var fields = instance.Members
			.OfType<FieldSymbol>()
			.DistinctBy(static field => field.Name)
			.ToDictionary(static field => field.Name);
		
		foreach (var field in GetMembers(definition).OfType<FieldSymbol>())
			RegisterMember(instance, fields[field.Name], Substitute(GetTypeOfMember(field), map));
		
		RegisterRecord(instance);
		foreach (var constructor in GetConstructors(definition))
			AddConstructor(instance, InstantiateFunction(constructor, instance.TypeArguments));
		
		if (GetDestructor(definition) is { } destructor)
			SetDestructor(instance, InstantiateFunction(destructor, instance.TypeArguments));
	}
	
	private void CompleteEnumInstance(EnumSymbol instance, EnumSymbol definition,
		Dictionary<TypeParameterSymbol, TypeSymbol> map)
	{
		for (var i = 0; i < definition.Cases.Length; i++)
		{
			var payloadTypes = GetPayloadTypes(definition, definition.Cases[i]);
			for (var j = 0; j < payloadTypes.Length; j++)
				RegisterPayloadField(instance.Cases[i].Fields[j], Substitute(payloadTypes[j], map));
		}
		
		if (definition.IsMatch)
		{
			RegisterMatchEnum(instance, Substitute(GetMatchedType(definition), map),
				[..definition.Cases.Select(enumCase => GetMatchValues(definition, enumCase))]);
			
			return;
		}
		
		RegisterEnum(instance, GetTagType(definition),
			[..definition.Cases.Select(enumCase => GetCaseValue(definition, enumCase))]);
		
		if (!instance.HasPayload)
			RegisterEnumConversions(instance);
	}
	
	public static Dictionary<TypeParameterSymbol, TypeSymbol> CreateMap(
		IReadOnlyList<TypeParameterSymbol> parameters, IReadOnlyList<TypeSymbol> arguments) =>
		parameters.Zip(arguments).ToDictionary(static pair => pair.First, static pair => pair.Second);
	
	public static bool ContainsTypeParameters(TypeSymbol type) => type switch
	{
		TypeParameterSymbol or DynType { Parameter: not null } => true,
		NamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameters),
		PointerType pointer => ContainsTypeParameters(pointer.BaseType),
		BorrowType borrow => ContainsTypeParameters(borrow.Target),
		ArrayType array => ContainsTypeParameters(array.ElementType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType).Any(ContainsTypeParameters),
		_ => false
	};
	
	public TypeSymbol Substitute(TypeSymbol type, IReadOnlyDictionary<TypeParameterSymbol, TypeSymbol> map) =>
		map.Count == 0 || !ContainsTypeParameters(type)
			? type
			: type switch
			{
				TypeParameterSymbol parameter => map.GetValueOrDefault(parameter, parameter),
				DynType { Parameter: { } parameter } => map.GetValueOrDefault(parameter) switch
				{
					TraitType argument => GetDynType(argument.Trait),
					TypeParameterSymbol argument => GetDynType(argument),
					InvalidType argument => argument,
					_ => type
				},
				NamedTypeSymbol named => Instantiate(named.Definition,
					[..named.TypeArguments.Select(argument => Substitute(argument, map))]),
				PointerType pointer => GetPointerType(Substitute(pointer.BaseType, map)),
				BorrowType borrow => GetBorrowType(Substitute(borrow.Target, map), borrow.IsMutable),
				ArrayType { Length.Sign: < 0 } array => new ArrayType(Substitute(array.ElementType, map), array.Length),
				ArrayType array => GetArrayType(Substitute(array.ElementType, map), array.Length),
				FunctionType function => GetFunctionType(function.IsExternal,
					function.ParameterTypes.Select(parameter => Substitute(parameter, map)), function.ParameterModes,
					Substitute(function.ReturnType, map)),
				_ => type
			};
	
	public FunctionInfo InstantiateFunction(FunctionInfo function, ImmutableArray<TypeSymbol> typeArguments)
	{
		var parameters = function.Symbol.TypeParameters;
		if (parameters.IsEmpty || typeArguments.SequenceEqual(parameters))
			return function.TypeArguments.IsDefaultOrEmpty ? function : GetGenericDefinition(function.Symbol);
		
		var definition = function.TypeArguments.IsDefaultOrEmpty ? function : GetGenericDefinition(function.Symbol);
		_genericFunctions[definition.Symbol] = definition;
		var instances = _functionInstances.GetOrAdd(definition.Symbol);
		var index = instances.FindIndex(instance => instance.TypeArguments.SequenceEqual(typeArguments));
		if (index >= 0)
			return instances[index];
		
		var map = CreateMap(parameters, typeArguments);
		var signature = definition.Signature;
		var instantiated = definition with
		{
			MangledName = null,
			Signature = new FunctionSignature(signature.ParameterTypes.Select(type => Substitute(type, map)),
				Substitute(signature.ReturnType, map), signature.IsVariadic, signature.ParameterModes),
			TypeArguments = typeArguments,
			Declared = signature
		};
		
		instances.Add(instantiated);
		return instantiated;
	}
	
	public FunctionInfo GetGenericDefinition(FunctionSymbol function) => _genericFunctions[function];
	
	public void RegisterGenericGlobal(GlobalInfo definition) => _genericGlobals[definition.Symbol] = definition;
	
	public GlobalInfo InstantiateGlobal(GlobalSymbol global, ModuleIndex? modules)
	{
		if (_globalInstances.TryGetValue(global, out var existing))
			return existing;
		
		var instance = (NamedTypeSymbol)global.ContainingType!;
		var definition = _genericGlobals[instance.Definition.GetStaticField(global.Name)!];
		var map = CreateMap(instance.Definition.TypeParameters, instance.TypeArguments);
		var instantiated = definition with
		{
			MangledName = Mangling.MangleInstantiation(definition.MangledName, instance.TypeArguments, modules),
			Symbol = global,
			Type = Substitute(definition.Type, map)
		};
		
		_globalInstances[global] = instantiated;
		return instantiated;
	}
	
	public bool TryGetTypeOfMember(MemberSymbol member, [NotNullWhen(true)] out TypeSymbol? type)
	{
		if (member is TypedMemberSymbol m)
			return _memberTypes.TryGetValue(m, out type);
		
		type = null;
		return false;
	}
	
	public TypeSymbol GetTypeOfMember(TypedMemberSymbol member) => _memberTypes[member];
	
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
		
		// TODO constructors, str.toCstr(), str.getCharLength(), etc.
		CreateStrMembers();
		
		// Operations
		CreatePointerArithmetic(NativeSymbols.VoidPtr);
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

public interface IGenericArgument;
public sealed record GenericTypeArgument(TypeSymbol Type) : IGenericArgument;
public sealed record GenericConstArgument(BigInteger Value) : IGenericArgument;