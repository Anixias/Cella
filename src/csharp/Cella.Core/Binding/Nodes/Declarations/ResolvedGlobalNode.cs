using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedGlobalNode(GlobalInfo info, IDeclarationNode syntax) : IResolvedDeclarationNode
{
	public GlobalInfo Info { get; } = info;
	public IDeclarationNode Syntax { get; } = syntax;
}