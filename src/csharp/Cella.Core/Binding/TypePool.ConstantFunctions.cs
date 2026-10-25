using Cella.Core.Binding.Constants;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed record ConstantBody(ResolvedFunctionNode Node, bool IsValid)
{
	public Diagnostic? Problem { get; init; }
}

public sealed partial class TypePool
{
	private readonly Dictionary<FunctionSymbol, ConstantBody> _constantBodies = [];
	private readonly Dictionary<FunctionSymbol, Constant?> _foldedFunctions = [];
	
	public ConstantBody? GetConstantBody(FunctionSymbol function) => _constantBodies.GetValueOrDefault(function);
	
	public void RegisterConstantBody(ConstantBody body) => _constantBodies[body.Node.FunctionInfo.Symbol] = body;
	
	public bool TryGetFoldedFunction(FunctionSymbol function, out Constant? value) =>
		_foldedFunctions.TryGetValue(function, out value);
	
	public void RegisterFoldedFunction(FunctionSymbol function, Constant? value) => _foldedFunctions[function] = value;
	
	public Constant? FindFoldedFunction(FunctionSymbol function) => _foldedFunctions.GetValueOrDefault(function);
	
	public static bool FoldsAsConstant(FunctionSymbol function) =>
		function is { IsConstant: true, Kind: FunctionKind.Free, Parameters.IsEmpty: true, Trait: null } &&
		function.TypeParameters.IsEmpty;
}