using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Conversions;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
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
	
	public bool HasDefaultPart(TypeSymbol type) => HasDefaultPart(type, []);
	
	private bool HasDefaultPart(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
	{
		TypeParameterSymbol or InvalidType => true,
		_ when HasDefault(type) => true,
		RecordSymbol { HasDestructor: true } => false,
		RecordSymbol record => visited.Add(record) && GetMembers(record)
			.OfType<FieldSymbol>()
			.Any(field => !field.IsRequired && HasDefaultPart(GetTypeOfMember(field), visited)),
		ArrayType array => HasDefaultPart(array.ElementType, visited),
		_ => false
	};
	
	public bool HoldsPointers(TypeSymbol type) => HoldsPointers(type, []);
	
	private bool HoldsPointers(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
	{
		PointerType => true,
		ArrayType array => HoldsPointers(array.ElementType, visited),
		RecordSymbol or EnumSymbol => visited.Add(type) && GetParts(type).Any(part => HoldsPointers(part, visited)),
		_ => false
	};
	
	public bool HoldsBorrows(TypeSymbol type) => HoldsBorrows(type, true);
	
	private bool HoldsBorrows(TypeSymbol type, bool countsParameters) => type switch
	{
		BorrowType or DynType or FStrType or FunctionType { IsRef: true } => true,
		ClosureType closure => closure.CaptureTypes.Any(capture => HoldsBorrows(capture, countsParameters)),
		TypeParameterSymbol parameter => countsParameters && !parameter.IsNoref,
		StringType => type == NativeSymbols.Str,
		NamedTypeSymbol { TypeArguments.IsEmpty: false } named => named.IsRef ||
		                                                          PartsHoldBorrows(named, countsParameters),
		NamedTypeSymbol named => named.IsRef,
		ArrayType array => HoldsBorrows(array.ElementType, countsParameters),
		_ => false
	};
	
	public bool CanHoldClosures(TypeSymbol type) => type switch
	{
		FunctionType function => function.IsRef,
		ClosureType closure => closure.CaptureTypes.Any(CanHoldClosures),
		BorrowType borrow => CanHoldClosures(borrow.Target),
		DynType => true,
		ArrayType array => CanHoldClosures(array.ElementType),
		NamedTypeSymbol named => HoldsBorrows(named) && PartsHoldClosures(named),
		_ => false
	};
	
	private bool PartsHoldClosures(NamedTypeSymbol type)
	{
		if (!_borrowChecks.Add(type))
			return false;
		
		var holds = GetParts(type).Any(CanHoldClosures);
		_borrowChecks.Remove(type);
		return holds;
	}
	
	private bool PartsHoldBorrows(NamedTypeSymbol type, bool countsParameters)
	{
		if (!_borrowChecks.Add(type))
			return false;
		
		var holds = GetParts(type).Any(part => HoldsBorrows(part, countsParameters));
		_borrowChecks.Remove(type);
		return holds;
	}
	
	public bool IsAtomic(TypeSymbol type) => type switch
	{
		InvalidType => true,
		IntegerType { Kind: not (PrimitiveTypeKind.Int128 or PrimitiveTypeKind.UInt128) } => true,
		FloatType => true,
		PrimitiveType { Kind: PrimitiveTypeKind.Bool } => true,
		StringType => type == NativeSymbols.CStr,
		PointerType => !IsFatPointer(type),
		FunctionType { IsExternal: true } => true,
		EnumSymbol { IsMatch: true } enumType => IsAtomic(GetMatchedType(enumType)),
		EnumSymbol enumType => !enumType.HasPayload && IsAtomic(GetTagType(enumType)),
		TypeParameterSymbol parameter => parameter.IsAtomic,
		_ => false
	};
	
	public static bool PassesByPointer(ParameterMode mode) => mode == ParameterMode.Imm;
	
	public static bool PassesByPointer(FunctionInfo function, int index) =>
		PassesByPointer(function.DeclaredSignature.GetMode(index));
	
	public bool IsViewRecord(TypeSymbol type) => type is RecordSymbol { IsRef: true } or EnumSymbol { IsRef: true } &&
	                                             GetParts(type).All(IsCopy);
	
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
		TypeParameterSymbol parameter => parameter.HasNew || parameter.HasDefault || GetParameterConstructors(parameter)
			.Any(static constructor => constructor.Signature.ParameterTypes.Length == 1),
		TraitType => false,
		RecordSymbol when GetConstructors(type).Count > 0 => FindNewConstructor(type) is not null,
		_ => HasDefault(type)
	};
	
	public bool DeclaresConstructors(TypeSymbol type) => type is NamedTypeSymbol && GetConstructors(type).Count > 0;
	
	public bool IsDefaultConstructor(FunctionInfo constructor) =>
		constructor.Symbol.Syntax is ConstructorNode { Default: not null };
	
	private bool HasDefaultConstructor(TypeSymbol type)
	{
		var floor = GetConstructorFloor(type);
		return GetConstructors(type)
			.Any(constructor => IsDefaultConstructor(constructor) && constructor.Symbol.Visibility >= floor);
	}
	
	public FunctionInfo? FindNewConstructor(TypeSymbol type) =>
		FindConstructorWitness(type, new FunctionSignature([GetPointerType(type)], NativeSymbols.Void, false,
			[ParameterMode.Mut])) is FunctionWitness { Info: var constructor }
			? constructor
			: null;
	
	public bool ReachesConstructor(FunctionSignature candidate, FunctionSignature expected, bool allowsStandIns = false)
	{
		if (expected.HasVariadicParameter || !candidate.HasVariadicParameter && candidate.Defaults.IsEmpty)
			return MatchesConstructor(candidate, expected, allowsStandIns);
		
		var given = expected.ParameterTypes.Length;
		var fixedCount = candidate.FixedCount;
		var required = fixedCount - candidate.Defaults.Length;
		if (given < required || given > fixedCount && !candidate.HasVariadicParameter)
			return false;
		
		for (var i = 0; i < Math.Min(given, fixedCount); i++)
		{
			if (!ParameterMatches(candidate.GetMode(i), candidate.ParameterTypes[i], expected, i, allowsStandIns))
				return false;
		}
		
		for (var i = given; i < fixedCount; i++)
		{
			if (candidate.Defaults[i - required] is ParametricConstant)
				return false;
		}
		
		if (given <= fixedCount)
			return true;
		
		if (FindVariadicElement(candidate.GetDeclaredType(fixedCount)) is not { } element)
			return false;
		
		for (var i = fixedCount; i < given; i++)
		{
			if (!ParameterMatches(ParameterMode.ReadOnly, element, expected, i, allowsStandIns))
				return false;
		}
		
		return true;
	}
	
	private static bool ParameterMatches(ParameterMode mode, TypeSymbol type, FunctionSignature expected, int index,
		bool allowsStandIns) => allowsStandIns
		? mode.CanStandInOrAdapt(type, expected.GetMode(index), expected.ParameterTypes[index])
		: type == expected.ParameterTypes[index] && mode == expected.GetMode(index);
	
	private static Visibility GetConstructorFloor(TypeSymbol type) => type is NamedTypeSymbol named
		? (Visibility)Math.Max((int)named.Definition.Visibility, (int)Visibility.Module)
		: Visibility.Public;
	
	private static bool IsVisibleWithType(TypeSymbol type, Visibility member) =>
		member >= Visibility.Project || member == Visibility.Module && GetConstructorFloor(type) == Visibility.Module;
	
	public Witness? FindArgumentsWitness(TypeSymbol type) => FindRunWitness(type, NativeSymbols.Str);
	
	private FunctionSignature GetRunSignature(TypeSymbol type, TypeSymbol element) =>
		new([GetPointerType(type), GetPointerType(element), NativeSymbols.UIntSize], NativeSymbols.Void, false,
			[ParameterMode.Mut, ParameterMode.ReadOnly, ParameterMode.ReadOnly]);
	
	public string? DescribeHiddenRun(TypeSymbol type, TypeSymbol? element = null) =>
		type is RecordSymbol &&
		GetMembers(type).OfType<FieldSymbol>().Select(GetTypeOfMember).ToList() is
			[PointerType { BaseType: var found }, var count] &&
		count == NativeSymbols.UIntSize && (element is null || found == element)
			? DescribeHiddenFields(type, GetRunSignature(type, found))
			: null;
	
	public Witness? FindRunWitness(TypeSymbol type, TypeSymbol element)
	{
		var expected = GetRunSignature(type, element);
		
		if (type is TypeParameterSymbol)
			return GetConstructors(type).FirstOrDefault(constructor =>
				MatchesConstructor(constructor.Signature, expected, true)) is { } bound
				? new FunctionWitness(bound.Symbol, bound)
				: null;
		
		var witness = FindConstructorWitness(type, expected);
		return witness is FunctionWitness or MemberwiseWitness ? witness : null;
	}
	
	public TypeSymbol? FindVariadicElement(TypeSymbol type) => FindRunElements(type) is [var element] ? element : null;
	
	public IReadOnlyList<TypeSymbol> FindRunElements(TypeSymbol type)
	{
		var constructors = GetConstructors(type);
		IEnumerable<ImmutableArray<TypeSymbol>> shapes =
			constructors.Count > 0 || type is not RecordSymbol { IsUnion: false }
				? constructors.Select(static constructor => constructor.Signature.ParameterTypes.RemoveAt(0))
				: [[..GetMembers(type).OfType<FieldSymbol>().Select(GetTypeOfMember)]];
		
		return
		[
			..shapes
				.Select(static shape => shape is [PointerType { BaseType: var element }, var count] &&
				                        count == NativeSymbols.UIntSize
					? element
					: null)
				.OfType<TypeSymbol>()
				.Distinct()
				.Where(element => FindRunWitness(type, element) is not null)
		];
	}
	
	public Witness? FindConstructorWitness(TypeSymbol type, FunctionSignature expected)
	{
		var declared = GetConstructors(type);
		var floor = GetConstructorFloor(type);
		var visible = declared.Where(constructor => constructor.Symbol.Visibility >= floor).ToList();
		var match = visible
			.Where(constructor => ReachesConstructor(constructor.Signature, expected))
			.Concat(visible.Where(constructor => ReachesConstructor(constructor.Signature, expected, true)))
			.Select(static constructor => new FunctionWitness(constructor.Symbol, constructor))
			.FirstOrDefault();
		
		if (match is not null)
			return match;
		
		if (declared.Count > 0 && type is RecordSymbol)
			return FindConversionWitness(type, expected);
		
		return FindConstructionWitness(type, expected) ?? FindConversionWitness(type, expected);
	}
	
	public Witness? FindConversionWitness(TypeSymbol self, FunctionSignature expected)
	{
		if (expected.ParameterTypes.Length != 2 || expected.GetMode(1) == ParameterMode.Mut ||
		    expected.HasVariadicParameter)
			return null;
		
		var source = expected.ParameterTypes[1];
		var canConsume = expected.GetMode(1) == ParameterMode.Own || IsCopy(source);
		return GetExplicitConversions(source)
			.Where(conversion => conversion.Signature.ReturnType == self &&
			                     conversion.Symbol.Visibility >= GetConstructorFloor(source) &&
			                     (canConsume || conversion.Signature.GetMode(0) != ParameterMode.Own))
			.Select(static conversion =>
				new ConversionWitness(new FunctionConversion(conversion, ConversionKind.Explicit, 0)))
			.FirstOrDefault();
	}
	
	private IReadOnlyList<FunctionInfo> GetExplicitConversions(TypeSymbol type)
	{
		Complete(type);
		if (_explicitConversions.TryGetValue(type, out var list))
			return list;
		
		if (type is not NamedTypeSymbol { IsGenericInstance: true } instance)
			return [];
		
		list =
		[
			..GetExplicitConversions(instance.Definition)
				.Select(conversion => InstantiateFunction(conversion, instance.TypeArguments))
		];
		
		_explicitConversions[type] = list;
		return list;
	}
	
	public Witness? FindConstructionWitness(TypeSymbol self, FunctionSignature expected)
	{
		if (expected.HasVariadicParameter)
			return null;
		
		var count = expected.ParameterTypes.Length - 1;
		if (count == 0 && HasDefault(self))
			return new DefaultWitness();
		
		if (self is not RecordSymbol)
			return count == 1 && IsConvertible(expected) && FindConversion(expected.ParameterTypes[1], self) is
				{ } conversion
				? new ConversionWitness(conversion)
				: null;
		
		var fields = GetMembers(self).OfType<FieldSymbol>().ToList();
		return MatchesFields((RecordSymbol)self, fields, expected) && DescribeHiddenField(self, fields) is null
			? new MemberwiseWitness()
			: null;
	}
	
	public string? DescribeHiddenFields(TypeSymbol self, FunctionSignature expected) =>
		self is RecordSymbol record && GetConstructors(self).Count == 0 &&
		GetMembers(self).OfType<FieldSymbol>().ToList() is var fields && MatchesFields(record, fields, expected)
			? DescribeHiddenField(self, fields)
			: null;
	
	private bool MatchesFields(RecordSymbol record, List<FieldSymbol> fields, FunctionSignature expected)
	{
		var count = expected.ParameterTypes.Length - 1;
		if (fields.Count != count || record.IsUnion)
			return false;
		
		for (var i = 0; i < count; i++)
		{
			var type = GetTypeOfMember(fields[i]);
			var mode = expected.GetMode(i + 1);
			var given = expected.ParameterTypes[i + 1];
			if (mode == ParameterMode.Mut || given != type && !FunctionType.CanAdapt(given, type) ||
			    mode.IsReadOnly() && !IsCopy(type))
				return false;
		}
		
		return true;
	}
	
	private static string? DescribeHiddenField(TypeSymbol self, IEnumerable<FieldSymbol> fields) => fields
		.Select(field => !IsVisibleWithType(self, field.Visibility)
			? $"'{field.Name}' is private to its {DiagnosticReporter.DescribeScope(field.Visibility, true)}"
			: !IsVisibleWithType(self, field.WriteVisibility)
				? $"'{field.Name}' is read-only outside its {DiagnosticReporter.DescribeScope(field.WriteVisibility, true)}"
				: null)
		.FirstOrDefault(static hint => hint is not null);
	
	private bool IsConvertible(FunctionSignature expected) => expected.GetMode(1) switch
	{
		ParameterMode.Own => true,
		ParameterMode.ReadOnly or ParameterMode.Imm => IsCopy(expected.ParameterTypes[1]),
		_ => false
	};
	
	private Conversion? FindConversion(TypeSymbol from, TypeSymbol to) =>
		from == to ? new IdentityConversion(to) : ConversionTable.FindExplicit(from, to);
	
	private bool HasConstructor(TypeSymbol type, FunctionSignature expected) => type switch
	{
		InvalidType => true,
		TypeParameterSymbol parameter => GetParameterConstructors(parameter)
			.Any(constructor => MatchesConstructor(constructor.Signature, expected, true)),
		_ => FindConstructorWitness(type, expected) is not null
	};
	
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
				HasDefault = GetConstructors(r).Count > 0
					? HasDefaultConstructor(r)
					: !r.HasDestructor && GetMembers(r).OfType<FieldSymbol>().All(HasDefault)
			},
			EnumSymbol e => Combine(false, GetParts(e)) with
			{
				HasDefault = e.IsExternal || FindCase(e, BigInteger.Zero) is { Fields.IsEmpty: true }
			},
			ArrayType => Combine(false, GetParts(type)),
			ClosureType => Combine(false, GetParts(type)) with { IsCopy = false, HasDefault = false },
			FunctionType or BorrowType or FStrType => TypeFacts.Plain with { HasDefault = false },
			TypeParameterSymbol parameter => new(!parameter.IsCopy, parameter.IsCopy, parameter.HasDefault),
			DynType => new(true, false, false),
			_ => TypeFacts.Plain
		};
		
		_facts[type] = facts;
		return facts;
	}
	
	public bool DropReadsBorrows(TypeSymbol type) => DropReadsBorrows(type, []);
	
	private bool DropReadsBorrows(TypeSymbol type, HashSet<TypeSymbol> visited) =>
		NeedsDrop(type) && HoldsBorrows(type) && visited.Add(type) &&
		(type is TypeParameterSymbol or RecordSymbol { HasDestructor: true } ||
		 GetParts(type).Any(part => DropReadsBorrows(part, visited)));
	
	private IEnumerable<TypeSymbol> GetParts(TypeSymbol type) => type switch
	{
		RecordSymbol r => GetMembers(r).OfType<FieldSymbol>().Select(GetTypeOfMember),
		EnumSymbol e => e.Cases.SelectMany(c => GetPayloadTypes(e, c)),
		ArrayType array => [array.ElementType],
		ClosureType closure => closure.CaptureTypes,
		_ => []
	};
	
	private TypeFacts Combine(bool hasDestructor, IEnumerable<TypeSymbol> parts)
	{
		var facts = parts.Select(GetFacts).ToArray();
		return new(hasDestructor || facts.Any(static f => f.NeedsDrop),
			!hasDestructor && facts.All(static f => f.IsCopy),
			facts.All(static f => f.HasDefault));
	}
}