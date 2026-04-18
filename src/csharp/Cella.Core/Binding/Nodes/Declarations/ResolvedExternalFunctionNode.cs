namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedExternalFunctionNode(FunctionInfo functionInfo) : IResolvedDeclarationNode
{
	public FunctionInfo FunctionInfo { get; } = functionInfo;
}