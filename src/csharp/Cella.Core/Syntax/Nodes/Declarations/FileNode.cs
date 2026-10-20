using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class FileNode(SourceLocation sourceLocation) : IDeclarationNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required ModuleName ModuleName { get; init; }
	public required ImmutableArray<IDeclarationNode> Declarations { get; init; }
	public required ImmutableArray<ImportExpression> Imports { get; init; }
	public SourceLocation? ImportCondition { get; init; }
	public IReadOnlySet<IStatementNode> BrokenStatements { get; init; } = ImmutableHashSet<IStatementNode>.Empty;
	public IReadOnlySet<IDeclarationNode> BrokenDeclarations { get; init; } = ImmutableHashSet<IDeclarationNode>.Empty;
	
	public IReadOnlySet<IDeclarationNode> DeclarationsWithSyntaxErrors { get; init; } =
		ImmutableHashSet<IDeclarationNode>.Empty;
	
	public ImmutableArray<Token> DroppedNames { get; init; } = [];
	public ImmutableArray<Token> ContextualKeywords { get; init; } = [];
	public ImmutableArray<SourceLocation> InactiveCode { get; init; } = [];
	public bool IsExcluded { get; init; }
}