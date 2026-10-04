using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedIsExpressionNode
(
	IResolvedExpressionNode value,
	ResolvedPattern pattern,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type => NativeSymbols.Bool;
	public IResolvedExpressionNode Value { get; } = value;
	public ResolvedPattern Pattern { get; } = pattern;
	public IExpressionNode Syntax { get; } = syntax;
}

public sealed class ResolvedPattern(EnumCaseSymbol enumCase, IEnumerable<LocalVariableSymbol?> bindings)
{
	public EnumCaseSymbol Case { get; } = enumCase;
	public ImmutableArray<LocalVariableSymbol?> Bindings { get; } = bindings.ToImmutableArray();
	public bool HasBindings => Bindings.Any(static binding => binding is not null);
}