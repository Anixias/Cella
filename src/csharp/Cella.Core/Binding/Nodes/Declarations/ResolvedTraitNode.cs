using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedTraitNode
(
	TraitSymbol symbol,
	IEnumerable<IResolvedDeclarationNode> members,
	IDeclarationNode syntax
) : IResolvedDeclarationNode
{
	public TraitSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Members { get; } = members.ToImmutableArray();
	public IDeclarationNode Syntax { get; } = syntax;
}

public sealed class ResolvedImplNode
(
	ImplSymbol symbol,
	TypeSymbol target,
	IEnumerable<IResolvedDeclarationNode> members,
	IDeclarationNode syntax
) : IResolvedDeclarationNode
{
	public ImplSymbol Symbol { get; } = symbol;
	public TypeSymbol Target { get; } = target;
	public ImmutableArray<IResolvedDeclarationNode> Members { get; } = members.ToImmutableArray();
	public IDeclarationNode Syntax { get; } = syntax;
}