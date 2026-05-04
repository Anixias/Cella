using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedFileNode(FileSymbol symbol, IEnumerable<IResolvedDeclarationNode> declarations,
	IEnumerable<FunctionInfo> importedFunctions,
	IDeclarationNode syntax
) : IResolvedDeclarationNode
{
	public FileSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Declarations { get; } = declarations.ToImmutableArray();
	public ImmutableArray<FunctionInfo> ImportedFunctions { get; } = importedFunctions.ToImmutableArray();
	public IDeclarationNode Syntax { get; } = syntax;
}