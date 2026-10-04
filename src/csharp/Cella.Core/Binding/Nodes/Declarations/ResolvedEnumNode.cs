using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedEnumNode(EnumSymbol symbol, IDeclarationNode syntax) : IResolvedDeclarationNode
{
	public EnumSymbol Symbol { get; } = symbol;
	public IDeclarationNode Syntax { get; } = syntax;
}