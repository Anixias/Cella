using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Declarations;

public sealed class ResolvedFileNode(ModuleSymbol moduleSymbol, IEnumerable<IResolvedDeclarationNode> declarations)
	: IResolvedDeclarationNode
{
	public ModuleSymbol ModuleSymbol { get; } = moduleSymbol;
	public ImmutableArray<IResolvedDeclarationNode> Declarations { get; } = declarations.ToImmutableArray();
}