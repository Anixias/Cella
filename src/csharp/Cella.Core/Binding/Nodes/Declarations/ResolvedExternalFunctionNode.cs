using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedExternalFunctionNode(FunctionInfo functionInfo, IDeclarationNode syntax)
	: IResolvedDeclarationNode
{
	public FunctionInfo FunctionInfo { get; } = functionInfo;
	public IDeclarationNode Syntax { get; } = syntax;
}