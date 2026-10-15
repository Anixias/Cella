using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedRecordNode
(
	RecordSymbol symbol,
	IEnumerable<IResolvedDeclarationNode> members,
	IDeclarationNode syntax
) : IResolvedDeclarationNode
{
	public RecordSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Members { get; } = members.ToImmutableArray();
	public IDeclarationNode Syntax { get; } = syntax;
}

public sealed class ResolvedFieldNode
(
	IDeclarationNode syntax
) : IResolvedDeclarationNode
{
	public IDeclarationNode Syntax { get; } = syntax;
}