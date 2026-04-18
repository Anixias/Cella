namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFunctionNode(FunctionInfo functionInfo, IResolvedNode? body) : IResolvedDeclarationNode
{
	public FunctionInfo FunctionInfo { get; } = functionInfo;
	public IResolvedNode? Body { get; } = body;
}