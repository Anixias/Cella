using System.Collections.Immutable;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

internal enum CPassKind
{
	Direct,
	Coerced,
	Indirect
}

internal readonly record struct CPass(CPassKind Kind, LLVMTypeRef Type);

internal readonly record struct CSignature(ImmutableArray<CPass> Parameters, CPass Return)
{
	public LLVMTypeRef CreateFunctionType(bool isVariadic)
	{
		var context = Return.Type.Context;
		var pointerType = LLVMTypeRef.CreatePointer(context.Int8Type, 0);
		var parameterTypes = new List<LLVMTypeRef>(Parameters.Length + 1);
		if (Return.Kind == CPassKind.Indirect)
			parameterTypes.Add(pointerType);
		
		foreach (var parameter in Parameters)
			parameterTypes.Add(parameter.Kind == CPassKind.Indirect ? pointerType : parameter.Type);
		
		var returnType = Return.Kind == CPassKind.Indirect ? context.VoidType : Return.Type;
		return LLVMTypeRef.CreateFunction(returnType, parameterTypes.ToArray(), isVariadic);
	}
}

internal sealed class CAbi(LLVMTargetDataRef targetData, string targetTriple)
{
	private readonly bool _isWindowsX64 = targetTriple.StartsWith("x86_64-") && targetTriple.Contains("-windows");
	
	public CSignature Classify(IEnumerable<LLVMTypeRef> parameterTypes, LLVMTypeRef returnType) =>
		new(parameterTypes.Select(type => ClassifyValue(type, false)).ToImmutableArray(),
			ClassifyValue(returnType, true));
	
	private CPass ClassifyValue(LLVMTypeRef type, bool isReturn)
	{
		if (_isWindowsX64 && type is { Kind: LLVMTypeKind.LLVMIntegerTypeKind, IntWidth: 128 })
			return isReturn
				? new(CPassKind.Coerced, LLVMTypeRef.CreateVector(type.Context.Int64Type, 2))
				: new(CPassKind.Indirect, type);
		
		if (type.Kind is not (LLVMTypeKind.LLVMStructTypeKind or LLVMTypeKind.LLVMArrayTypeKind))
			return new(CPassKind.Direct, type);
		
		if (!_isWindowsX64)
			throw new NotSupportedException(
				$"Records cannot be passed to or returned from ext functions on '{targetTriple}' yet");
		
		var size = targetData.ABISizeOfType(type);
		return size is 1 or 2 or 4 or 8
			? new(CPassKind.Coerced, type.Context.GetIntType((uint)size * 8))
			: new(CPassKind.Indirect, type);
	}
}