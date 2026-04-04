using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes.Declarations;

public readonly record struct ImportExpression(ModuleName ModuleName, IImport Import);

public interface IImport;

public readonly record struct FullImport : IImport
{
	public static FullImport Instance { get; } = new();
}

public readonly record struct TokenImport(Token Token) : IImport;
public readonly record struct ListImport(ImmutableArray<Token> Tokens) : IImport;