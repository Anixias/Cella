using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedRecordExpressionNode
(
	RecordSymbol type,
	IEnumerable<(FieldSymbol Field, IResolvedExpressionNode Value)> fields,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	
	public ImmutableArray<(FieldSymbol Field, IResolvedExpressionNode Value)> Fields { get; } =
		fields.ToImmutableArray();
	
	public IExpressionNode Syntax { get; } = syntax;
	public bool IsConstant => false;
}