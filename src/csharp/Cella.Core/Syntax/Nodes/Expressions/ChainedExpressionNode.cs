using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ChainedExpressionNode : IExpressionNode
{
	public ChainedExpressionNode(IEnumerable<IExpressionNode> operands, IEnumerable<Token> ops,
		IEnumerable<SourceLocation> operandLocations)
	{
		Operands = operands.ToImmutableArray();
		Ops = ops.ToImmutableArray();
		OperandLocations = operandLocations.ToImmutableArray();
		
		var (source, range) = OperandLocations[0];
		
		if (OperandLocations.Length > 1)
			range = range.Join(OperandLocations[^1].Range);
		
		SourceLocation = new(source, range);
	}
	
	public ImmutableArray<IExpressionNode> Operands { get; }
	public ImmutableArray<SourceLocation> OperandLocations { get; }
	public ImmutableArray<Token> Ops { get; }
	public SourceLocation SourceLocation { get; }
	public bool IsContained => false;
}