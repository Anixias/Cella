using Cella.Core.Binding;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

public sealed partial class CodeGenerator
{
	private LLVMTypeRef OpaquePointer => LLVMTypeRef.CreatePointer(_context.Int8Type, 0u);
	private LLVMTypeRef DropGlueType => LLVMTypeRef.CreateFunction(_context.VoidType, [OpaquePointer]);
	
	private LLVMTypeRef PanicType =>
		LLVMTypeRef.CreateFunction(_context.VoidType, [OpaquePointer, _context.Int32Type]);
	
	private LLVMTypeRef FatPointerType => _context.GetStructType([OpaquePointer, OpaquePointer], false);
	
	private LLVMTypeRef MapPassedPointer(TypeSymbol declared) =>
		declared is DynType ? FatPointerType : OpaquePointer;
	
	private LLVMTypeRef MapParameterType(TypeSymbol type, ParameterMode mode) =>
		TypePool.PassesByPointer(mode) ? MapPassedPointer(type) : MapValueType(type);
	
	private LLVMTypeRef[] MapParameterTypes(FunctionInfo function) => MapParameterTypes(function, MapValueType);
	
	private LLVMTypeRef[] MapParameterTypes(FunctionInfo function, Func<TypeSymbol, LLVMTypeRef> mapValue)
	{
		var declared = function.DeclaredSignature;
		return
		[
			..function.Symbol.Captures.IsEmpty ? [] : new[] { OpaquePointer },
			..function.Signature.ParameterTypes.Select((type, i) =>
				TypePool.PassesByPointer(function, i)
					? MapPassedPointer(declared.ParameterTypes[i])
					: mapValue(type))
		];
	}
	
	private TypeSymbol Substitute(TypeSymbol type) => _typePool.Substitute(type, substitution);
	
	private static bool IsEmitted(FunctionInfo function) => !IsOpenGeneric(function) && IsActive(function.Symbol);
	
	internal static bool IsActive(FunctionSymbol function) =>
		function.Syntax is not FunctionNode { When.IsActive: false } &&
		(function.ContainingFunction is not { } containing || IsActive(containing.Symbol));
	
	private static bool IsOpenGeneric(FunctionInfo function) =>
		!function.Symbol.TypeParameters.IsEmpty && (function.TypeArguments.IsDefaultOrEmpty ||
		                                            function.TypeArguments.Any(TypePool.ContainsTypeParameters));
	
	private FunctionInfo SubstituteFunction(FunctionInfo function)
	{
		if (substitution.Count == 0 || function.Symbol.TypeParameters.IsEmpty)
			return function;
		
		IEnumerable<TypeSymbol> arguments = function.TypeArguments.IsDefaultOrEmpty
			? function.Symbol.TypeParameters
			: function.TypeArguments;
		
		return _typePool.InstantiateFunction(function, [..arguments.Select(Substitute)]);
	}
	
	private LLVMTypeRef MapTypeSymbol(TypeSymbol? symbol)
	{
		if (symbol is null)
			return _context.VoidType;
		
		symbol = Substitute(symbol);
		if (current.Types.TryGetValue(symbol, out var type))
			return type;
		
		switch (symbol)
		{
			case ArrayType arrayType:
			{
				var elementType = MapTypeSymbol(arrayType.ElementType);
				var llvmArray = LLVMTypeRef.CreateArray2(elementType, (ulong)arrayType.Length);
				current.Types[symbol] = llvmArray;
				return llvmArray;
			}
			
			case FunctionType functionType:
			{
				var llvmFunctionType = functionType.IsExternal
					? OpaquePointer
					: _context.GetStructType([OpaquePointer, OpaquePointer], false);
				
				current.Types[symbol] = llvmFunctionType;
				return llvmFunctionType;
			}
			
			case ClosureType closure:
			{
				var closureType = _context.GetStructType([..closure.CaptureTypes.Select(MapTypeSymbol)], false);
				current.Types[symbol] = closureType;
				return closureType;
			}
			
			case PointerType or BorrowType:
			{
				var pointerType = TypePool.IsFatPointer(symbol) ? FatPointerType : OpaquePointer;
				current.Types[symbol] = pointerType;
				return pointerType;
			}
			
			case FStrType:
			{
				var fstrType = _context.GetStructType(
					[MapTypeSymbol(NativeSymbols.UIntSize), OpaquePointer, OpaquePointer], false);
				
				current.Types[symbol] = fstrType;
				return fstrType;
			}
			
			case DynType:
				throw new InvalidOperationException($"'{symbol.Name}' has no size");
			
			case TypeParameterSymbol:
				throw new InvalidOperationException($"Type parameter '{symbol.Name}' has no type argument");
			
			case EnumSymbol enumType:
				return CreateEnumType(enumType);
			
			default:
				return CreateType(symbol);
		}
	}
	
	private LLVMTypeRef CreateType(TypeSymbol type)
	{
		if (current.Types.TryGetValue(type, out var existing))
			return existing;
		
		var typeRef = _context.CreateNamedStruct(type.Name);
		current.Types[type] = typeRef;
		
		var fieldTypes = _typePool
			.GetMembers(type)
			.OfType<FieldSymbol>()
			.Select(f => _typePool.GetTypeOfMember(f))
			.Select(MapTypeSymbol)
			.ToArray();
		
		if (type is RecordSymbol { IsUnion: true })
		{
			typeRef.StructSetBody(fieldTypes.Length == 0 ? [] : [CreateUnionStorage(fieldTypes)], false);
			return typeRef;
		}
		
		// TODO Allow controlling packed?
		typeRef.StructSetBody(fieldTypes, false);
		return typeRef;
	}
	
	private LLVMTypeRef CreateUnionStorage(LLVMTypeRef[] memberTypes)
	{
		var alignment = memberTypes.Max(type => _targetData.ABIAlignmentOfType(type));
		var size = memberTypes.Max(type => _targetData.ABISizeOfType(type));
		var count = (uint)((size + alignment - 1) / alignment);
		return LLVMTypeRef.CreateArray(_context.GetIntType(alignment * 8), count);
	}
	
	private LLVMTypeRef CreateEnumType(EnumSymbol enumType)
	{
		if (enumType.IsMatch)
		{
			var matchedType = MapTypeSymbol(_typePool.GetMatchedType(enumType));
			current.Types[enumType] = matchedType;
			return matchedType;
		}
		
		var typeRef = _context.CreateNamedStruct(enumType.Name);
		current.Types[enumType] = typeRef;
		
		var tagType = MapTypeSymbol(_typePool.GetTagType(enumType));
		var payloadTypes = enumType.Cases.Where(static c => c.Fields.Length > 0).Select(GetPayloadType).ToList();
		if (payloadTypes.Count == 0)
		{
			typeRef.StructSetBody([tagType], false);
			return typeRef;
		}
		
		var alignment = payloadTypes.Max(type => _targetData.ABIAlignmentOfType(type));
		var size = payloadTypes.Max(type => _targetData.ABISizeOfType(type));
		var count = (uint)((size + alignment - 1) / alignment);
		typeRef.StructSetBody([tagType, LLVMTypeRef.CreateArray(_context.GetIntType(alignment * 8), count)], false);
		return typeRef;
	}
	
	private LLVMTypeRef GetPayloadType(EnumCaseSymbol enumCase) => _context.GetStructType(
		[..enumCase.Fields.Select(field => MapTypeSymbol(_typePool.GetTypeOfMember(field)))], false);
}
