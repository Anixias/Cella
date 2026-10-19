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
			var unreported = _entryPoints.Count(entryPoint => !_conflictingDeclarations.Contains(entryPoint.Symbol));
			if (unreported <= 1 && unreported < _entryPoints.Count)
				return;
			
			foreach (var entryPoint in _entryPoints)
				Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(entryPoint.Symbol.Syntax).SourceLocation,
					$"The program has more than one '{_entryPointName}' function"));
			
			return;
		}
		
		var candidates = _builder.Functions.Values
			.Where(f => f.Symbol is { Kind: FunctionKind.Free } && IsEntryPointName(f.Symbol))
			.ToList();
		
		if (candidates.Count == 0)
		{
			if (!dropsEntryPoint)
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
					DescribeEntryPointProblem(candidate))
				{
					Hints = !QualifiesEntryPoint && IsEntryPointResult(candidate.Signature.ReturnType) &&
					        candidate.Signature.ParameterTypes is [var only] &&
					        _typePool.DescribeHiddenRun(only, NativeSymbols.Str) is { } hint
						? [hint]
						: []
				});
		}
	}
	
	private string DescribeEntryPointProblem(FunctionInfo candidate)
	{
		if (!IsEntryPointResult(candidate.Signature.ReturnType))
			return $"'{_entryPointName}' must return 'i32', 'never' or nothing";
		
		if (!QualifiesEntryPoint)
			return $"'{_entryPointName}' must take no parameters, 'ptr[str]' and 'usize', or one parameter " +
			       "that meets 'new(ptr[str], usize)'";
		
		return candidate.Signature.ParameterTypes.IsEmpty
			? $"'{_entryPointName}' must be 'pub'"
			: $"'{_entryPointName}' must take no parameters";
	}
	
	private bool IsEntryPointName(FileNode file, Token name) => _entryPointName is not null &&
	                                                            (QualifiesEntryPoint
		                                                            ? $"{file.ModuleName.Text}.{name.Text}"
		                                                            : name.Text) == _entryPointName;
	
	private bool IsEntryPointName(FunctionSymbol function) => _entryPointName is not null && (QualifiesEntryPoint
		? function.Kind == FunctionKind.Free &&
		  string.Join('.', function.QualifiedName.Select(static part => part.Name)) == _entryPointName
		: function.Name == _entryPointName);
	
	private void ReportExtSignatureTypes()
	{
		foreach (var (type, node, isPassed) in GetExtSignatureTypes())
			_extSignatureTypes.Add(type, node, isPassed);
		
		_extSignatureTypes.Report(Diagnostics);
	}
	
	private IEnumerable<(TypeSymbol Type, ITypeNode Node, bool IsPassed)> GetExtSignatureTypes()
	{
		foreach (var (global, info) in _builder.Globals)
		{
			if (global.IsExternal)
				yield return (info.Type, global.Syntax.Type, false);
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
				if (parameters[i].Ellipsis is null && !IsWrittenBorrow(parameters[i].Type))
					yield return (info.Signature.GetDeclaredType(i), parameters[i].Type,
						ExtSignatureTypes.PassesByValue(info.Signature.GetMode(i)));
			}
			
			if (returnType is not null)
				yield return (info.Signature.ReturnType, returnType, true);
		}
	}
	
	private static bool IsWrittenBorrow(ITypeNode node) => node is BorrowTypeNode or FunctionTypeNode { IsRef: true };
	
	public static bool CanRunAsMain(FunctionInfo function) =>
		function.Symbol is { Kind: FunctionKind.Free, TypeParameters.IsEmpty: true } &&
		IsEntryPoint(function.Signature);
	
	public static bool CanRunAsEntry(FunctionInfo function) =>
		function.Symbol is { Kind: FunctionKind.Free, TypeParameters.IsEmpty: true } &&
		function.Signature.ParameterTypes.IsEmpty && IsEntryPointResult(function.Signature.ReturnType) &&
		ModuleIndex.GetVisibility(function.Symbol) == Visibility.Public;
	
	private static bool IsEntryPointResult(TypeSymbol type) =>
		type == NativeSymbols.Void || type == NativeSymbols.Int32 || type == NativeSymbols.Never;
	
	private static bool IsEntryPoint(FunctionSignature signature) =>
		IsEntryPointResult(signature.ReturnType) &&
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