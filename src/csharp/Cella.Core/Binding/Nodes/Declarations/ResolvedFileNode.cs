using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Declarations;

public sealed class ResolvedFileNode(FileSymbol symbol, IEnumerable<IResolvedDeclarationNode> declarations,
	IEnumerable<FunctionInfo> importedFunctions)
	: IResolvedDeclarationNode
{
	public FileSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Declarations { get; } = declarations.ToImmutableArray();
	public ImmutableArray<FunctionInfo> ImportedFunctions { get; } = importedFunctions.ToImmutableArray();
}