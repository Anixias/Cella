using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public interface IResolvedDeclarationNode : IResolvedNode
{
	IDeclarationNode Syntax { get; }
}

[TreeVisitor<IResolvedDeclarationNode>]
public partial interface IResolvedDeclarationNodeVisitor;

[TreeVisitor<IResolvedDeclarationNode>]
public partial interface IResolvedDeclarationNodeVisitor<out T>;

public sealed class ResolvedInvalidDeclarationNode(IDeclarationNode node) : IResolvedDeclarationNode
{
	public IDeclarationNode Syntax { get; } = node;
}