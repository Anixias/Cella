using Cella.Core.Symbols;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public enum CompletionPlace
{
	Name,
	Member,
	Type
}

public sealed record CompletionCandidate
(
	Symbol Symbol,
	FunctionInfo? Function = null,
	TypeSymbol? Type = null,
	GlobalInfo? Global = null
);

public sealed class CompletionProbe(string path, int offset)
{
	private static readonly StringComparer _pathComparer =
		OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
	
	public string Path { get; } = path;
	public int Offset { get; } = offset;
	public CompletionPlace? Place { get; private set; }
	public TypeSymbol? ExpectedType { get; private set; }
	public IReadOnlyList<CompletionCandidate> Candidates { get; private set; } = [];
	
	internal bool Covers(Token token)
	{
		var (source, range) = token.SourceLocation;
		return Place is null && range.IsValid && range.Start <= Offset && Offset <= range.End &&
		       _pathComparer.Equals(source.FilePath, Path);
	}
	
	internal void Capture(CompletionPlace place, IEnumerable<CompletionCandidate> candidates, TypeSymbol? expectedType)
	{
		if (Place is not null)
			return;
		
		Place = place;
		ExpectedType = expectedType;
		Candidates = [..candidates];
	}
}