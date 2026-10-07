using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedCaseNameExpressionNode(Token name, Symbol? symbol, bool isNear, IExpressionNode syntax)
	: IResolvedExpressionNode
{
	public TypeSymbol Type => CaseNameType.Instance;
	public Token Name { get; } = name;
	public Symbol? Symbol { get; } = symbol;
	public bool IsNear { get; } = isNear;
	public IExpressionNode Syntax { get; } = syntax;
}