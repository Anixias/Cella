using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class LambdaExpressionNode
(
	Token keyword,
	IEnumerable<LambdaParameterNode> parameters,
	ITypeNode? returnType,
	IExpressionNode? expressionBody,
	BlockStatementNode? blockBody,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public Token Keyword { get; } = keyword;
	public ImmutableArray<LambdaParameterNode> Parameters { get; } = parameters.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
	public IExpressionNode? ExpressionBody { get; } = expressionBody;
	public BlockStatementNode? BlockBody { get; } = blockBody;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => false;
}

public sealed class LambdaParameterNode(Token? mode, Token identifier, ITypeNode? type)
{
	public Token? Mode { get; } = mode;
	public Token Identifier { get; } = identifier;
	public ITypeNode? Type { get; } = type;
}

public sealed class LambdaDeclarationNode(LambdaExpressionNode lambda) : IDeclarationNode
{
	public LambdaExpressionNode Lambda { get; } = lambda;
	public SourceLocation SourceLocation => Lambda.Keyword.SourceLocation;
}