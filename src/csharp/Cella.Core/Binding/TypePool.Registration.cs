using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Conversions;
using Cella.Core.Binding.Operations;
using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public sealed partial class TypePool
{
	public void RegisterRecord(RecordSymbol record) => SizeTable.Register(record, () =>
	{
		var fieldSizes = GetMembers(record)
			.OfType<FieldSymbol>()
			.Select(GetTypeOfMember)
			.Select(type => SizeTable.TryGetSize(type) ?? StorageSize.Const(0))
			.ToList();
		
		return !record.IsUnion ? StorageSize.Sum(fieldSizes)
			: fieldSizes.Count > 0 ? StorageSize.Max(fieldSizes)
			: StorageSize.Const(0);
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
	
	private bool IsExtCompatible(TypeSymbol type) => FindForeign(type) is null &&
	                                                 ExtSignatureTypes.FindUnconstrained(type) is null &&
	                                                 FindDestructor(type) is null && FindPlainEnum(type) is null;
	
	public TypeSymbol? FindForeign(TypeSymbol type) => FindForeign(type, []);
	
	private TypeSymbol? FindForeign(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
	{
		DynType or FStrType or BorrowType or FunctionType { IsExternal: false } or NamedTypeSymbol { IsRef: true } =>
			type,
		StringType when type == NativeSymbols.Str => type,
		PointerType pointer => FindFatTarget(pointer.BaseType),
		FunctionType function => function.ParameterTypes.Append(function.ReturnType)
			.Select(part => FindForeign(part, visited))
			.FirstOrDefault(static foreign => foreign is not null),
		ArrayType or RecordSymbol or EnumSymbol when visited.Add(type) => GetParts(type)
			.Select(part => FindForeign(part, visited))
			.FirstOrDefault(static foreign => foreign is not null),
		_ => null
	};
	
	private static TypeSymbol? FindFatTarget(TypeSymbol type) => type switch
	{
		DynType or FStrType => type,
		PointerType pointer => FindFatTarget(pointer.BaseType),
		BorrowType borrow => FindFatTarget(borrow.Target),
		ArrayType array => FindFatTarget(array.ElementType),
		_ => null
	};
	
	public EnumSymbol? FindPlainEnum(TypeSymbol type) => FindPlainEnum(type, []);
	
	private EnumSymbol? FindPlainEnum(TypeSymbol type, HashSet<TypeSymbol> visited)
	{
		if (type is EnumSymbol { IsExternal: false, IsMatch: false } plainEnum)
			return plainEnum;
		
		if (!visited.Add(type))
			return null;
		
		IEnumerable<TypeSymbol> parts = type switch
		{
			EnumSymbol enumType => enumType.Cases.SelectMany(c => GetPayloadTypes(enumType, c)),
			RecordSymbol record => GetMembers(record).OfType<FieldSymbol>().Select(GetTypeOfMember),
			ArrayType array => [array.ElementType],
			FunctionType { IsExternal: true } function =>
				[..function.ParameterTypes.Select((_, i) => function.GetDeclaredType(i)), function.ReturnType],
			_ => []
		};
		
		return parts.Select(part => FindPlainEnum(part, visited)).FirstOrDefault(static found => found is not null);
	}
	
	public static bool HasNull(TypeSymbol type) => type is PointerType or FunctionType or BorrowType
		                                               or TypeParameterSymbol { HasNull: true } ||
	                                               type == NativeSymbols.CStr;
	
	public void RegisterPayloadField(FieldSymbol field, TypeSymbol type) => _memberTypes[field] = type;
	
	public ImmutableArray<TypeSymbol> GetPayloadTypes(EnumSymbol enumType, EnumCaseSymbol enumCase)
	{
		if (!enumCase.Fields.All(_memberTypes.ContainsKey))
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
		if (_caseValues.TryGetValue(enumCase, out var value))
			return value;
		
		Complete(enumType);
		return _caseValues.GetValueOrDefault(enumCase, enumCase.Index);
	}
	
	public void RegisterCaseValue(EnumCaseSymbol enumCase, BigInteger value) => _caseValues[enumCase] = value;
	
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
}