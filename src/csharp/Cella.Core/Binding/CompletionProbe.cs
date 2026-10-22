using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;

namespace Cella.Core.Binding;

public enum CompletionPlace
{
	Name,
	Member,
	Type,
	Trait,
	Label
}

public enum CompletionSite
{
	Statement,
	Declaration,
	TopLevel
}

public sealed record CompletionCall(string Callee, IReadOnlyList<CompletionCandidate> Candidates, int Argument);
public sealed record CompletionImport(string Name, string Module, bool InProject);

public sealed record CompletionCandidate
(
	Symbol Symbol,
	FunctionInfo? Function = null,
	TypeSymbol? Type = null,
	GlobalInfo? Global = null,
	CompletionImport? Import = null
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
	public IReadOnlyList<CompletionCandidate> Importable { get; private set; } = [];
	public FileNode? File { get; private set; }
	public CompletionSite? Site { get; private set; }
	public CompletionCall? Call { get; private set; }
	
	private (SourceLocation Callee, TextRange Arguments, int Argument)? enclosingCall;
	
	internal bool Covers(Token token) => Place is null && IsAt(token.SourceLocation);
	
	private bool IsAt(SourceLocation location) =>
		IsIn(location) && location.Range.Start <= Offset && Offset <= location.Range.End;
	
	private bool IsIn(SourceLocation location) =>
		location.Range.IsValid && _pathComparer.Equals(location.Source.FilePath, Path);
	
	internal void RecordSite(Token token, CompletionSite site)
	{
		if (Site is null && IsAt(token.SourceLocation))
			Site = site;
	}
	
	internal void EnterCall(Token callee, SourceLocation arguments, IEnumerable<SourceLocation> argumentList)
	{
		var range = arguments.Range;
		if (!IsIn(arguments) || Offset < range.Start || Offset > range.End ||
		    enclosingCall is { Arguments: var outer } && (range.Start < outer.Start || range.End > outer.End))
			return;
		
		enclosingCall = (callee.SourceLocation, range,
			argumentList.Count(argument => argument.Range.End < Offset));
	}
	
	internal void CaptureCall(Token callee, IEnumerable<CompletionCandidate> candidates)
	{
		if (enclosingCall is { } call && call.Callee == callee.SourceLocation)
			Call = new(callee.Text, [..candidates], call.Argument);
	}
	
	internal void Capture(CompletionPlace place, IEnumerable<CompletionCandidate> candidates, TypeSymbol? expectedType,
		FileNode? file = null, IEnumerable<CompletionCandidate>? importable = null)
	{
		if (Place is not null)
			return;
		
		Place = place;
		ExpectedType = expectedType;
		Candidates = [..candidates];
		File = file;
		Importable = importable is null ? [] : [..importable];
	}
}