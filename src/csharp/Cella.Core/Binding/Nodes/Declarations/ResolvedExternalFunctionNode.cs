namespace Cella.Core.Binding.Nodes.Declarations;

public sealed class ResolvedExternalFunctionNode(FunctionInfo functionInfo) : IResolvedDeclarationNode
{
	public FunctionInfo FunctionInfo { get; } = functionInfo;
}