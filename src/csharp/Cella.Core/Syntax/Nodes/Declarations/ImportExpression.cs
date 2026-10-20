using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public readonly record struct ImportExpression(ModuleName ModuleName, IImport Import)
{
	public bool IsPrelude { get; init; }
	public bool HasSyntaxErrors { get; init; }
	public bool HasEntryLocations { get; init; }
}

public interface IImport;

public readonly record struct FullImport : IImport
{
	public static FullImport Instance { get; } = new();
}

public readonly record struct TokenImport(Token Token) : IImport;
public readonly record struct ListImport(ImmutableArray<Token> Tokens) : IImport;