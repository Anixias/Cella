using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public interface IResolvedExpressionNode : IResolvedNode
{
	TypeSymbol Type { get; }
	bool IsConstant { get; }
	IExpressionNode Syntax { get; }
}

[TreeVisitor<IResolvedExpressionNode>]
public partial interface IResolvedExpressionNodeVisitor;

[TreeVisitor<IResolvedExpressionNode>]
public partial interface IResolvedExpressionNodeVisitor<out T>;

public sealed class ResolvedInvalidExpressionNode(IExpressionNode syntax, TypeSymbol? type = null)
	: IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type ?? NativeSymbols.Invalid;
	public bool IsConstant => true;
	public IExpressionNode Syntax { get; } = syntax;
}