using Cella.Core.Binding.Nodes;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed class ErrorContainment
{
	private const int FirstWholeBodyPhase = 3;
	
	private sealed class Group
	{
		public bool IsBroken { get; set; }
	}
	
	private readonly Dictionary<string, List<TextRange>> _declarations = [];
	private readonly Dictionary<string, List<TextRange>> _brokenRegions = [];
	private readonly Dictionary<string, List<TextRange>> _brokenStatements = [];
	private readonly Dictionary<string, List<TextRange>> _syntaxDeclarations = [];
	private readonly Dictionary<IResolvedDeclarationNode, Group> _groups = [];
	private readonly Dictionary<string, List<(TextRange Range, Group Group)>> _groupExtents = [];
	
	public ErrorContainment(IEnumerable<FileNode> files)
	{
		foreach (var file in files)
		{
			foreach (var (source, range) in file.DeclarationExtents)
				GetList(_declarations, source.FilePath).Add(range);
			
			foreach (var (source, range) in file.BrokenStatements)
				GetList(_brokenStatements, source.FilePath).Add(range);
		}
	}
	
	public void MarkSyntaxErrors(IEnumerable<Diagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
		{
			if (diagnostic.Severity != DiagnosticSeverity.Error ||
			    FindInnermost(diagnostic.SourceLocation) is not { } declaration)
				continue;
			
			var path = diagnostic.SourceLocation.Source.FilePath;
			GetList(_syntaxDeclarations, path).Add(declaration);
			GetList(_brokenRegions, path).Add(FindBrokenStatement(diagnostic.SourceLocation) ?? declaration);
		}
	}
	
	public IEnumerable<Diagnostic> Filter(IReadOnlyCollection<Diagnostic> diagnostics, Func<Diagnostic, int> phaseOf)
	{
		var firstErrors = new Dictionary<(string Path, TextRange Range), int>();
		foreach (var diagnostic in diagnostics)
		{
			if (diagnostic.Severity != DiagnosticSeverity.Error || phaseOf(diagnostic) == 0 ||
			    FindInnermost(diagnostic.SourceLocation) is not { } declaration)
				continue;
			
			var key = (diagnostic.SourceLocation.Source.FilePath, declaration);
			var phase = phaseOf(diagnostic);
			firstErrors[key] = firstErrors.TryGetValue(key, out var first) ? Math.Min(first, phase) : phase;
		}
		
		return diagnostics.Where(diagnostic =>
		{
			var phase = phaseOf(diagnostic);
			if (phase == 0)
				return true;
			
			if (IsInBrokenRegion(diagnostic.SourceLocation))
				return false;
			
			if (phase >= FirstWholeBodyPhase && HasSyntaxError(diagnostic.SourceLocation))
				return false;
			
			return FindInnermost(diagnostic.SourceLocation) is not { } declaration ||
			       !firstErrors.TryGetValue((diagnostic.SourceLocation.Source.FilePath, declaration), out var first) ||
			       first >= phase;
		});
	}
	
	private TextRange? FindInnermost(SourceLocation location) => FindInnermost(_declarations, location);
	
	private static TextRange? FindInnermost(Dictionary<string, List<TextRange>> extents, SourceLocation location)
	{
		if (!extents.TryGetValue(location.Source.FilePath, out var ranges))
			return null;
		
		TextRange? innermost = null;
		foreach (var range in ranges)
		{
			if (Contains(range, location.Range.Start) &&
			    (innermost is not { } current || range.Length < current.Length))
				innermost = range;
		}
		
		return innermost;
	}
	
	private TextRange? FindBrokenStatement(SourceLocation location)
	{
		if (FindInnermost(_brokenStatements, location) is { } containing)
			return containing;
		
		if (!_brokenStatements.TryGetValue(location.Source.FilePath, out var statements))
			return null;
		
		var start = location.Range.Start;
		return statements
			.Where(statement => statement.End <= start &&
			                    location.Source.GetText(new TextRange(statement.End, start)).IsWhiteSpace())
			.Select(static statement => (TextRange?)statement)
			.FirstOrDefault();
	}
	
	private bool HasSyntaxError(SourceLocation location) =>
		FindInnermost(location) is { } declaration &&
		_syntaxDeclarations.TryGetValue(location.Source.FilePath, out var declarations) &&
		declarations.Contains(declaration);
	
	private bool ContainsSyntaxError(SourceLocation extent) =>
		_syntaxDeclarations.TryGetValue(extent.Source.FilePath, out var declarations) &&
		declarations.Any(declaration =>
			declaration.Start >= extent.Range.Start && declaration.End <= extent.Range.End);
	
	public bool IsInBrokenRegion(SourceLocation location) =>
		_brokenRegions.TryGetValue(location.Source.FilePath, out var regions) &&
		regions.Any(region => Contains(region, location.Range.Start));
	
	public void AddDeclarations(IEnumerable<ResolvedFileNode> files)
	{
		foreach (var file in files)
		{
			var declarations = Collect(file.Declarations)
				.Select(static node => (Node: node, Extent: GetExtent(node)))
				.OrderBy(static entry => entry.Extent.Range.Start)
				.ThenByDescending(static entry => entry.Extent.Range.End);
			
			(TextRange Range, Group Group)? outer = null;
			foreach (var (node, extent) in declarations)
			{
				if (outer is not { } current || extent.Range.Start < current.Range.Start ||
				    extent.Range.End > current.Range.End)
				{
					outer = (extent.Range,
						new Group { IsBroken = IsInBrokenRegion(extent) || ContainsSyntaxError(extent) });
					
					GetList(_groupExtents, extent.Source.FilePath).Add(outer.Value);
				}
				
				_groups[node] = outer.Value.Group;
			}
		}
	}
	
	public void MarkErrors(IEnumerable<Diagnostic> diagnostics)
	{
		foreach (var diagnostic in diagnostics)
		{
			if (diagnostic.Severity != DiagnosticSeverity.Error)
				continue;
			
			var (source, range) = diagnostic.SourceLocation;
			if (!_groupExtents.TryGetValue(source.FilePath, out var extents))
				continue;
			
			foreach (var (extent, group) in extents)
			{
				if (Contains(extent, range.Start))
					group.IsBroken = true;
			}
		}
	}
	
	public void Mark(IResolvedDeclarationNode node)
	{
		if (_groups.TryGetValue(node, out var group))
			group.IsBroken = true;
	}
	
	public bool IsBroken(IResolvedDeclarationNode node) => _groups.TryGetValue(node, out var group) && group.IsBroken;
	
	private static bool Contains(TextRange range, int position) => position >= range.Start && position <= range.End;
	
	private static List<T> GetList<T>(Dictionary<string, List<T>> lists, string path)
	{
		if (!lists.TryGetValue(path, out var list))
			lists[path] = list = [];
		
		return list;
	}
	
	private static IEnumerable<IResolvedDeclarationNode> Collect(IEnumerable<IResolvedDeclarationNode> declarations)
	{
		foreach (var declaration in declarations)
		{
			switch (declaration)
			{
				case ResolvedFunctionNode or ResolvedGlobalNode:
					yield return declaration;
					break;
				
				case ResolvedRecordNode record:
					foreach (var member in Collect(record.Members))
						yield return member;
					
					break;
				
				case ResolvedEnumNode @enum:
					foreach (var member in Collect(@enum.Members))
						yield return member;
					
					break;
				
				case ResolvedTraitNode trait:
					foreach (var member in Collect(trait.Members))
						yield return member;
					
					break;
				
				case ResolvedImplNode impl:
					foreach (var member in Collect(impl.Members))
						yield return member;
					
					break;
			}
		}
	}
	
	private static SourceLocation GetExtent(IResolvedDeclarationNode node) => node switch
	{
		ResolvedFunctionNode { Syntax: FunctionNode function } => function.Extent,
		ResolvedFunctionNode function => function.Syntax.SourceLocation,
		ResolvedGlobalNode { Syntax: GlobalNode global } => global.Extent,
		ResolvedGlobalNode global => global.Syntax.SourceLocation,
		_ => throw new ArgumentOutOfRangeException(nameof(node))
	};
}