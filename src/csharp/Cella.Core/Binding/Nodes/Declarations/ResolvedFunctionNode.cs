namespace Cella.Core.Binding.Nodes.Declarations;

public sealed class ResolvedFunctionNode(FunctionInfo functionInfo, IResolvedNode? body) : IResolvedDeclarationNode
{
	public FunctionInfo FunctionInfo { get; } = functionInfo;
	public IResolvedNode? Body { get; } = body;
}