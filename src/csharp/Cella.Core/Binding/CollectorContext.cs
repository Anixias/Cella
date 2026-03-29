using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes.Declarations;

namespace Cella.Core.Binding;

public readonly record struct CollectorContext
(
	Scope GlobalScope,
	Dictionary<IDeclarationNode, Scope> DeclarationScopes,
	Dictionary<IDeclarationNode, Symbol> DeclarationSymbols
)
{
	public CollectorContext() : this(new(), [], [])
	{
	}
}