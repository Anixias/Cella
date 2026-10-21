using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private ImportEnvironment CollectImports(FileNode node, ResolutionContext context)
	{
		var isFirstFile = projectModules is null;
		projectModules ??= ResolveProjectImports(context);
		if (isFirstFile)
			RecordOwnPrelude(context);
		
		var imports = new List<Symbol>();
		var hidden = new List<Symbol>();
		var explicitImports = new List<(Symbol Symbol, Token Name, string Module)>();
		var activated = new List<ModuleSymbol>();
		var prelude = new HashSet<Symbol>();
		var projectImported = new HashSet<Symbol>();
		var projectNames = new HashSet<Token>();
		
		void Add(ImportExpression import, ModulePathSymbol module, Action<Diagnostic> report, bool isProject)
		{
			var named = CollectImport(module, import, ForImport(import, context), hidden, report);
			var collected = named.Select(static entry => entry.Symbol).ToList();
			imports.AddRange(collected);
			activated.AddRange(Modules.GetModules(module));
			activated.AddRange(collected.OfType<ModulePathSymbol>().SelectMany(Modules.GetModules));
			if (import.IsPrelude)
				prelude.UnionWith(collected);
			else if (isProject)
				projectImported.UnionWith(collected);
			
			if (!import.IsPrelude && import.Import is TokenImport or ListImport)
			{
				explicitImports.AddRange(
					named.Select(entry => (entry.Symbol, entry.Name!.Value, import.ModuleName.Text)));
				
				if (isProject)
					projectNames.UnionWith(named.Select(static entry => entry.Name!.Value));
			}
		}
		
		for (var i = 0; i < ProjectImports.Length; i++)
		{
			if (projectModules[i] is { } module)
				Add(ProjectImports[i], module, diagnostic => _projectImportFailures.TryAdd(diagnostic.SourceLocation,
					diagnostic), true);
		}
		
		foreach (var import in node.Imports)
		{
			using var frame = Own(new ErrorContainment.Owner(null, import.HasSyntaxErrors));
			ProbeImport(import, context);
			if (ResolveImportPath(import, context) is { } module)
				Add(import, module, Diagnostics.Add, false);
		}
		
		foreach (var name in explicitImports
			         .Where(entry => prelude.Contains(entry.Symbol) ||
			                         projectImported.Contains(entry.Symbol) && !projectNames.Contains(entry.Name))
			         .Select(static entry => entry.Name)
			         .Distinct()
			         .Where(name => isFirstFile || !projectNames.Contains(name)))
			Diagnostics.Add(new(DiagnosticSeverity.Hint, name.SourceLocation, "Redundant import")
			{
				Fixes = [RemoveImport(node.Imports, name)],
				Lint = Lints.RedundantImport
			});
		
		_explicitImports.Add((explicitImports, context));
		ReportRepeatedImports(ProjectImports.Where(static import => !import.IsPrelude));
		ReportRepeatedImports(node.Imports.Where(static import => !import.HasSyntaxErrors));
		
		return new(imports, explicitImports.Select(static entry => entry.Symbol), hidden, activated);
	}
	
	private List<ModulePathSymbol?> ResolveProjectImports(ResolutionContext context) =>
		[..ProjectImports.Select(import => ResolveImportPath(import, ForImport(import, context)))];
	
	private void RecordOwnPrelude(ResolutionContext context)
	{
		if (context.RecordName is null)
			return;
		
		var quiet = context with { Diagnostics = new DiagnosticList() };
		foreach (var import in OwnPrelude.Where(static import => !import.HasEntryLocations))
		{
			if (quiet.ResolveMembers(Modules.Root, import.ModuleName.Parts, 0) is ModulePathSymbol module)
				CollectImport(module, import, quiet, [], static _ => { });
		}
	}
	
	private static ResolutionContext ForImport(ImportExpression import, ResolutionContext context) =>
		import.HasEntryLocations ? context with { RecordName = null } : context;
	
	private ModulePathSymbol? ResolveImportPath(ImportExpression import, ResolutionContext context)
	{
		var path = import.ModuleName.Parts;
		var symbol = context.ResolveMembers(Modules.Root, path, 0);
		if (symbol is not null and not ModulePathSymbol)
			Diagnostics.Add(ResolutionContext.ReportNotModule(symbol, path));
		
		return symbol as ModulePathSymbol;
	}
	
	private void ProbeImport(ImportExpression import, ResolutionContext context)
	{
		if (Probe is null)
			return;
		
		var parts = import.ModuleName.Parts;
		for (var i = 0; i < parts.Length; i++)
		{
			if (Probe.Covers(parts[i]))
				Probe.Capture(CompletionPlace.Member, FindModule(parts[..i], context) is { } parent
					? i == parts.Length - 1 && import.Import is FullImport
						? GetImportCandidates(parent, context)
						: parent.Children.Values.Select(static child => new CompletionCandidate(child))
					: [], null);
		}
		
		var names = import.Import switch
		{
			TokenImport single => [single.Token],
			ListImport list => list.Tokens,
			_ => ImmutableArray<Token>.Empty
		};
		
		if (names.FirstOrDefault(Probe.Covers) is { Text: not null } probed && FindModule(parts, context) is { } module)
		{
			var listed = names.Where(name => name != probed).Select(static name => name.Text).ToHashSet();
			Probe.Capture(CompletionPlace.Member, GetImportCandidates(module, context)
				.Where(candidate => !listed.Contains(candidate.Symbol.Name)), null);
		}
	}
	
	private static IEnumerable<CompletionCandidate> GetImportCandidates(ModulePathSymbol module,
		ResolutionContext context) =>
		context.GetModuleMembers(module).Select(static member => new CompletionCandidate(member));
	
	private ModulePathSymbol? FindModule(ImmutableArray<Token> parts, ResolutionContext context)
	{
		ModulePathSymbol? module = Modules.Root;
		foreach (var part in parts)
			module = module?.Children.GetValueOrDefault(part.Text);
		
		return module is not null && (parts.IsEmpty || context.IsVisible(module)) ? module : null;
	}
	
	private void ReportRepeatedImports(IEnumerable<ImportExpression> imports)
	{
		var names = imports.SelectMany(static i => i.Import switch
		{
			TokenImport single => [(Module: i.ModuleName.Text, single.Token)],
			ListImport list => list.Tokens.Select(token => (Module: i.ModuleName.Text, Token: token)),
			_ => []
		});
		
		var projectTokens = ProjectImports.SelectMany(static import => import.Import switch
		{
			TokenImport single => [single.Token],
			ListImport list => list.Tokens,
			_ => []
		}).ToHashSet();
		
		foreach (var repeated in names.GroupBy(static e => (e.Module, e.Token.Text)).Where(static g => g.Count() > 1))
		{
			foreach (var (_, token) in repeated)
			{
				if (!projectTokens.Contains(token) || _repeatedProjectImports.Add(token))
					Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation,
						$"'{token.Text}' is imported more than once"));
			}
		}
	}
	
	private static DiagnosticFix RemoveImport(IEnumerable<ImportExpression> imports, Token name)
	{
		const string title = "Remove redundant import";
		var import = imports.First(import => import.Import switch
		{
			TokenImport single => single.Token == name,
			ListImport list => list.Tokens.Contains(name),
			_ => false
		});
		
		switch (import.Import)
		{
			case ListImport { Tokens: [var first, var second] }:
				var (source, range) = first.SourceLocation;
				var open = range.Start;
				while (open > 0 && source[open - 1] != '[')
					open--;
				
				var kept = first == name ? second : first;
				return DiagnosticFix.Replace(title,
					new(source, new(open - 1, import.SourceLocation.Range.End)), kept.Text);
			
			case ListImport { Tokens.Length: > 2 }:
				return DiagnosticFix.RemoveListItem(title, name.SourceLocation, ',');
			
			default:
				return DiagnosticFix.Remove(title, import.SourceLocation);
		}
	}
	
	private static void CollectImportConflicts(List<(Symbol Symbol, Token Name, string Module)> explicitImports,
		ResolutionContext context, Dictionary<Token, (Symbol Symbol, SortedSet<string> Modules)> ambiguous,
		Dictionary<Symbol, HashSet<string>> importers)
	{
		foreach (var (symbol, name, module) in explicitImports)
		{
			var declared = context.File.Module.Files
				.SelectMany(f => f.Symbols.GetValueOrDefault(symbol.Name) ?? [])
				.Where(member => member != symbol && context.IsVisible(member))
				.ToList();
			
			if (declared.Count == 0 ||
			    symbol is FunctionSymbol && declared.All(static member => member is FunctionSymbol))
				continue;
			
			if (!ambiguous.TryGetValue(name, out var entry))
				ambiguous[name] = entry = (symbol, new SortedSet<string>(StringComparer.Ordinal));
			
			entry.Modules.Add(context.File.Module.Name);
			foreach (var member in declared)
				importers.GetOrAdd(member).Add(module);
		}
	}
	
	private void ReportImportConflicts(Dictionary<Token, (Symbol Symbol, SortedSet<string> Modules)> ambiguous)
	{
		foreach (var (name, (symbol, modules)) in ambiguous)
			Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, $"'{symbol.Name}' is ambiguous")
			{
				Hints = [..modules.Select(static module => $"Also declared in '{module}'")]
			});
	}
	
	private void ReportImportedDeclarations(Dictionary<Symbol, HashSet<string>> importers)
	{
		foreach (var (member, modules) in importers)
			Diagnostics.Add(new(DiagnosticSeverity.Error, GetIdentifier(_declarations[member].Node).SourceLocation,
				$"'{member.Name}' is ambiguous")
			{
				Hints =
				[
					..modules.Order(StringComparer.Ordinal).Select(static module => $"Also imported from '{module}'")
				]
			});
	}
	
	private static List<(Symbol Symbol, Token? Name)> CollectImport(ModulePathSymbol module, ImportExpression import,
		ResolutionContext context, List<Symbol> hidden, Action<Diagnostic> report)
	{
		if (import.Import is FullImport)
			hidden.AddRange(module.Members.Values
				.Concat(module.PrivateMembers.Values)
				.SelectMany(static m => m)
				.Where(m => !context.IsVisible(m)));
		
		return import.Import switch
		{
			FullImport =>
			[
				..module.Members.Values.SelectMany(static m => m).Where(context.IsVisible)
					.Select(static symbol => (symbol, (Token?)null))
			],
			TokenImport i => ImportMember(module, i.Token, context, report),
			ListImport i => [..i.Tokens.SelectMany(t => ImportMember(module, t, context, report))],
			_ => []
		};
	}
	
	private static List<(Symbol Symbol, Token? Name)> ImportMember(ModulePathSymbol module, Token name,
		ResolutionContext context, Action<Diagnostic> report)
	{
		if (module.Members.TryGetValue(name.Text, out var members) &&
		    members.Where(context.IsVisible).ToList() is { Count: > 0 } visible)
		{
			foreach (var symbol in visible)
				context.RecordName?.Invoke(name, symbol);
			
			return [..visible.Select(symbol => (symbol, (Token?)name))];
		}
		
		if (module.Children.TryGetValue(name.Text, out var child))
		{
			context.RecordName?.Invoke(name, child);
			return [(child, name)];
		}
		
		report(context.ReportUndefinedMember(name.SourceLocation, module, name.Text));
		return [];
	}
}