using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedIsExpressionNode
(
	IResolvedExpressionNode value,
	ResolvedPattern pattern,
	bool isMut,
	bool ownsValue,
	IExpressionNode syntax
) : IResolvedExpressionNode
{
	public TypeSymbol Type => NativeSymbols.Bool;
	public IResolvedExpressionNode Value { get; } = value;
	public ResolvedPattern Pattern { get; } = pattern;
	public bool IsMut { get; } = isMut;
	public bool OwnsValue { get; } = ownsValue;
	public IExpressionNode Syntax { get; } = syntax;
}

public sealed class ResolvedPattern(EnumCaseSymbol? enumCase, IEnumerable<LocalVariableSymbol?> bindings)
{
	public EnumCaseSymbol? Case { get; } = enumCase;
	public TypeSymbol? TestedType { get; init; }
	public ImmutableArray<LocalVariableSymbol?> Bindings { get; } = bindings.ToImmutableArray();
	public bool HasBindings => Bindings.Any(static binding => binding is not null);
	public bool HasMutBindings => Bindings.Any(static binding => binding is { IsMutBinding: true });
}