using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFunctionNode(FunctionInfo functionInfo, IResolvedNode? body, IDeclarationNode syntax)
	: IResolvedDeclarationNode
{
	public FunctionInfo FunctionInfo { get; } = functionInfo;
	public IResolvedNode? Body { get; } = body;
	public IDeclarationNode Syntax { get; } = syntax;
}