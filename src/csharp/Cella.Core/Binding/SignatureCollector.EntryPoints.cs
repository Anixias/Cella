using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void ReportEntryPoint()
	{
		if (_entryPointName is null)
			return;
		
		_entryPoints.RemoveAll(entryPoint => !BuildsArguments(entryPoint.Signature));
		if (_entryPoints.Count == 1)
			return;
		
		if (_entryPoints.Count > 1)
		{
			var reportedAsDuplicates = _entryPoints.All(entryPoint => _entryPoints.Any(other => other != entryPoint &&
				TypePool.ParametersMatch(entryPoint.Signature, other.Signature, false) &&
				AreVisibleTogether(entryPoint.Symbol, entryPoint.File!, other.Symbol, other.File!)));
			
			if (reportedAsDuplicates)
				return;
			
			foreach (var entryPoint in _entryPoints)
				Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(entryPoint.Symbol.Syntax).SourceLocation,
					$"The program has more than one '{_entryPointName}' function"));
			
			return;
		}
		
		var candidates = _builder.Functions.Values
			.Where(f => f.Symbol is { Kind: FunctionKind.Free } && f.Symbol.Name == _entryPointName)
			.ToList();
		
		if (candidates.Count == 0)
		{
			Diagnostics.Add(new(DiagnosticSeverity.Error, SourceLocation.None,
				$"The program has no '{_entryPointName}' function"));
			
			return;
		}
		
		foreach (var candidate in candidates)
		{
			Diagnostics.Add(candidate.Symbol.Syntax is FunctionNode { TypeParameters: [var first, ..] }
				? new(DiagnosticSeverity.Error, first.Identifier.SourceLocation,
					$"Cannot declare {DescribeTypeParameter(first, false)}s on '{_entryPointName}'")
				: new(DiagnosticSeverity.Error, GetIdentifier(candidate.Symbol.Syntax).SourceLocation,
					candidate.Signature.ReturnType != NativeSymbols.Void &&
					candidate.Signature.ReturnType != NativeSymbols.Int32
						? $"'{_entryPointName}' must return 'i32' or nothing"
						: $"'{_entryPointName}' must take no parameters, 'ptr[str]' and 'usize', or one parameter " +
						  "that meets 'new(ptr[str], usize)'"));
		}
	}
	
	private void ReportExtSignatureTypes()
	{
		foreach (var (type, node) in GetExtSignatureTypes())
			_extSignatureTypes.Add(type, node);
		
		_extSignatureTypes.Report(Diagnostics);
	}
	
	private IEnumerable<(TypeSymbol Type, ITypeNode Node)> GetExtSignatureTypes()
	{
		foreach (var (global, info) in _builder.Globals)
		{
			if (global.IsExternal)
				yield return (info.Type, global.Syntax.Type);
		}
		
		foreach (var (function, info) in _builder.Functions)
		{
			if (!function.IsExternal)
				continue;
			
			var (parameters, returnType) = function.Syntax switch
			{
				FunctionNode node => (node.Parameters, node.ReturnType),
				ExternalFunctionNode node => (node.Parameters, node.ReturnType),
				_ => ([], null)
			};
			
			for (var i = 0; i < parameters.Length; i++)
			{
				if (!IsWrittenBorrow(parameters[i].Type))
					yield return (info.Signature.GetDeclaredType(i), parameters[i].Type);
			}
			
			if (returnType is not null)
				yield return (info.Signature.ReturnType, returnType);
		}
	}
	
	private static bool IsWrittenBorrow(ITypeNode node) => node is BorrowTypeNode or FunctionTypeNode { IsRef: true };
	
	private static bool IsEntryPoint(FunctionSignature signature) =>
		(signature.ReturnType == NativeSymbols.Void || signature.ReturnType == NativeSymbols.Int32) &&
		!signature.HasVariadicParameter &&
		signature.ParameterModes.All(static mode => mode != ParameterMode.Mut) &&
		signature.ParameterTypes switch
		{
			[] or [_] => true,
			[PointerType { BaseType: var element }, var count] => element == NativeSymbols.Str &&
			                                                      count == NativeSymbols.UIntSize,
			_ => false
		};
	
	private bool BuildsArguments(FunctionSignature signature) => signature.ParameterTypes is not [var type] ||
	                                                             type is InvalidType ||
	                                                             _typePool.FindArgumentsWitness(type) is not null;
}
