using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class EntryPointNode(SourceLocation sourceLocation, FunctionNode functionNode) : ISyntaxNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public FunctionNode FunctionNode { get; } = functionNode;
}