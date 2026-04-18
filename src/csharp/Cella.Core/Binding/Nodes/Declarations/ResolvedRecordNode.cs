using System.Collections.Immutable;
using Cella.Core.Symbols;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedRecordNode(RecordSymbol symbol, IEnumerable<IResolvedDeclarationNode> members)
	: IResolvedDeclarationNode
{
	public RecordSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Members { get; } = members.ToImmutableArray();
}

// TODO Properties

public sealed class ResolvedFieldNode(FieldSymbol symbol, TypeSymbol type, IResolvedExpressionNode? initializer)
	: IResolvedDeclarationNode
{
	public FieldSymbol Symbol { get; } = symbol;
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode? Initializer { get; } = initializer;
}

public sealed class ResolvedMethodNode(MethodSymbol symbol, ResolvedFunctionNode functionNode)
	: IResolvedDeclarationNode
{
	public MethodSymbol Symbol { get; } = symbol;
	public ResolvedFunctionNode FunctionNode { get; } = functionNode;
}