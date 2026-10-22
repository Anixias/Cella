using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedLiteralExpressionNode(TypeSymbol type, object? value, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeSymbol Type { get; } = type;
	public object? Value { get; } = value;
	public IExpressionNode Syntax { get; } = syntax;
	public UntypedType? UntypedSource { get; init; }
	
	[SuppressMessage("ReSharper", "UnusedAutoPropertyAccessor.Global",
		Justification = "Read by the semantic model's walk over resolved nodes")]
	public ImmutableArray<IResolvedExpressionNode> Parts { get; init; } = [];
	
	public BigInteger? IntegerValue => Value switch
	{
		sbyte integer => integer,
		short integer => integer,
		int integer => integer,
		long integer => integer,
		Int128 integer => integer,
		byte integer => integer,
		ushort integer => integer,
		uint integer => integer,
		ulong integer => integer,
		UInt128 integer => integer,
		BigInteger integer => integer,
		_ => null
	};
}