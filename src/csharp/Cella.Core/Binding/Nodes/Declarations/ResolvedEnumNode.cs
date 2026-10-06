using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedEnumNode
(
	EnumSymbol symbol,
	IEnumerable<IResolvedDeclarationNode> members,
	IDeclarationNode syntax
) : IResolvedDeclarationNode
{
	public EnumSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Members { get; } = members.ToImmutableArray();
	public IDeclarationNode Syntax { get; } = syntax;
}