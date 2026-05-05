using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ChainedExpressionNode : IExpressionNode
{
	public ChainedExpressionNode(IEnumerable<IExpressionNode> operands, IEnumerable<Token> ops)
	{
		Operands = operands.ToImmutableArray();
		Ops = ops.ToImmutableArray();
		
		var (source, range) = Operands[0].SourceLocation;
		
		if (Operands.Length > 1)
			range = range.Join(Operands[^1].SourceLocation.Range);
		
		SourceLocation = new(source, range);
	}
	
	public ImmutableArray<IExpressionNode> Operands { get; }
	public ImmutableArray<Token> Ops { get; }
	public SourceLocation SourceLocation { get; }
	public bool IsContained => false;
}