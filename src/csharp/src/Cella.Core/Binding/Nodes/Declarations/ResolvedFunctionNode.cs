using Cella.Core.Binding.Nodes.Statements;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes.Declarations;

public sealed class ResolvedFunctionNode(FunctionSymbol functionSymbol, IResolvedNode body)
	: IResolvedDeclarationNode
{
	public FunctionSymbol FunctionSymbol { get; } = functionSymbol;
	public IResolvedNode Body { get; } = body;
}