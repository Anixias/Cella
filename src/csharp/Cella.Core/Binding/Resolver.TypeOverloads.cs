using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private ResolvedInvalidExpressionNode? RejectOverloadedType(IExpressionNode node)
	{
		var target = node switch
		{
			CallExpressionNode call => call.Target,
			AccessExpressionNode access => access.Target,
			_ => null
		};
		
		var name = target is not null && IsOverloadedType(target) ? target
			: node is CallExpressionNode && target is AccessExpressionNode { Target: var owner } &&
			  IsOverloadedType(owner) ? owner
			: null;
		
		return name is null
			? null
			: Error(node, $"'{name.SourceLocation.GetText()}' needs generic arguments", CurrentTargetType, name);
	}
	
	private bool IsOverloadedType(IExpressionNode name) => name is VarExpressionNode or AccessExpressionNode &&
	                                                       TypeOverloads.FindFamily(
			                                                       CurrentResolutionContext.FindNamed(name)) is
		                                                       [NamedTypeSymbol, ..];
}