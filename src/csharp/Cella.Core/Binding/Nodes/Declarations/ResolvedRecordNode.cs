using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;

namespace Cella.Core.Binding.Nodes;

public sealed class ResolvedRecordNode(RecordSymbol symbol, IEnumerable<IResolvedDeclarationNode> members,
	IDeclarationNode syntax) : IResolvedDeclarationNode
{
	public RecordSymbol Symbol { get; } = symbol;
	public ImmutableArray<IResolvedDeclarationNode> Members { get; } = members.ToImmutableArray();
	public IDeclarationNode Syntax { get; } = syntax;
}

// TODO Properties

public sealed class ResolvedFieldNode(FieldSymbol symbol, TypeSymbol type, IResolvedExpressionNode? initializer,
	IDeclarationNode syntax) : IResolvedDeclarationNode
{
	public FieldSymbol Symbol { get; } = symbol;
	public TypeSymbol Type { get; } = type;
	public IResolvedExpressionNode? Initializer { get; } = initializer;
	public IDeclarationNode Syntax { get; } = syntax;
}

public sealed class ResolvedMethodNode(MethodSymbol symbol, ResolvedFunctionNode functionNode, IDeclarationNode syntax)
	: IResolvedDeclarationNode
{
	public MethodSymbol Symbol { get; } = symbol;
	public ResolvedFunctionNode FunctionNode { get; } = functionNode;
	public IDeclarationNode Syntax { get; } = syntax;
}