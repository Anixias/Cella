using System.Collections.Immutable;
using LLVMSharp.Interop;

namespace Cella.Core.CodeGen;

internal enum CPassKind
{
	Direct,
	Integer,
	Indirect
}

internal readonly record struct CPass(CPassKind Kind, LLVMTypeRef Type);

internal readonly record struct CSignature(ImmutableArray<CPass> Parameters, CPass Return)
{
	public LLVMTypeRef CreateFunctionType(bool isVariadic)
	{
		var pointerType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
		var parameterTypes = new List<LLVMTypeRef>(Parameters.Length + 1);
		if (Return.Kind == CPassKind.Indirect)
			parameterTypes.Add(pointerType);
		
		foreach (var parameter in Parameters)
			parameterTypes.Add(parameter.Kind == CPassKind.Indirect ? pointerType : parameter.Type);
		
		var returnType = Return.Kind == CPassKind.Indirect ? LLVMTypeRef.Void : Return.Type;
		return LLVMTypeRef.CreateFunction(returnType, parameterTypes.ToArray(), isVariadic);
	}
}

internal sealed class CAbi(LLVMTargetDataRef targetData, string targetTriple)
{
	private readonly bool _isWindowsX64 = targetTriple.StartsWith("x86_64-") && targetTriple.Contains("-windows");
	
	public CSignature Classify(IEnumerable<LLVMTypeRef> parameterTypes, LLVMTypeRef returnType) =>
		new(parameterTypes.Select(ClassifyValue).ToImmutableArray(), ClassifyValue(returnType));
	
	private CPass ClassifyValue(LLVMTypeRef type)
	{
		if (type.Kind is not (LLVMTypeKind.LLVMStructTypeKind or LLVMTypeKind.LLVMArrayTypeKind))
			return new(CPassKind.Direct, type);
		
		if (!_isWindowsX64)
			throw new NotSupportedException(
				$"Records cannot be passed to or returned from ext functions on '{targetTriple}' yet");
		
		var size = targetData.ABISizeOfType(type);
		return size is 1 or 2 or 4 or 8
			? new(CPassKind.Integer, LLVMTypeRef.CreateInt((uint)size * 8))
			: new(CPassKind.Indirect, type);
	}
}