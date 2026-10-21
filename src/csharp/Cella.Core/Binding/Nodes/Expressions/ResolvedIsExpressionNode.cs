using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Binding.Constants;
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
	public EnumSymbol? EnumType { get; init; }
	public PatternNode? Syntax { get; init; }
	public TypeSymbol? TestedType { get; init; }
	public LocalVariableSymbol? TypeBinding { get; init; }
	public TypeSymbol? Record { get; init; }
	public ImmutableArray<FieldSymbol> Fields { get; init; } = [];
	public ImmutableArray<LocalVariableSymbol?> Bindings { get; } = bindings.ToImmutableArray();
	public ImmutableArray<ResolvedPattern?> Elements { get; init; } = [];
	public LocalVariableSymbol? Subject { get; init; }
	public IResolvedExpressionNode? Test { get; init; }
	public Constant? Constant { get; init; }
	public ImmutableArray<(BigInteger Low, BigInteger High)> Spans { get; init; } = [];
	
	public bool IsConditional =>
		Test is not null || Elements.Any(static element => element is { IsIrrefutable: false });
	
	public bool IsIrrefutable => Record is not null && TestedType is null && !IsConditional;
	
	public IEnumerable<LocalVariableSymbol> AllBindings => Bindings.OfType<LocalVariableSymbol>()
		.Concat(TypeBinding is null ? [] : [TypeBinding])
		.Concat(Elements.OfType<ResolvedPattern>().SelectMany(static element => element.AllBindings));
	
	public bool HasBindings => AllBindings.Any();
	public bool HasMutBindings => AllBindings.Any(static binding => binding.IsMutBinding);
}