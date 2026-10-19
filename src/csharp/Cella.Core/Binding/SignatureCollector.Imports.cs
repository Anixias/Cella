using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class SignatureCollector
{
	private void ReportOnce(Action report)
	{
		var captured = new List<Diagnostic>();
		var previous = Diagnostics.Redirect(captured);
		report();
		Diagnostics.Redirect(previous);
		Diagnostics.AddRange(captured.Where(diagnostic =>
			_reportedImportErrors.Add((diagnostic.SourceLocation, diagnostic.Message))));
	}
	
	private ImportEnvironment CollectImports(FileNode node, ResolutionContext context)
	{
		ImportEnvironment? environment = null;
		ReportOnce(() => environment = CollectFileImports(node, context));
		return environment!;
	}
	
	private ImportEnvironment CollectFileImports(FileNode node, ResolutionContext context)
	{
		var imports = new List<Symbol>();
		var hidden = new List<Symbol>();
		var explicitImports = new List<(Symbol Symbol, Token Name, string Module)>();
		var activated = new List<ModuleSymbol>();
		var prelude = new HashSet<Symbol>();
		foreach (var importExpression in node.Imports)
		{
			ProbeImport(importExpression, context);
			var named = CollectImport(importExpression, context, hidden);
			var collected = named.Select(static entry => entry.Symbol).ToList();
			imports.AddRange(collected);
			if (context.ResolveMembers(Modules.Root, importExpression.ModuleName.Parts, 0) is ModulePathSymbol imported)
				activated.AddRange(Modules.GetModules(imported));
			
			activated.AddRange(collected.OfType<ModulePathSymbol>().SelectMany(Modules.GetModules));
			if (importExpression.IsPrelude)
				prelude.UnionWith(collected);
			else if (importExpression.Import is TokenImport or ListImport)
				explicitImports.AddRange(named.Select(entry =>
					(entry.Symbol, entry.Name!.Value, importExpression.ModuleName.Text)));
		}
		
		foreach (var name in explicitImports.Where(entry => prelude.Contains(entry.Symbol))
			         .Select(static entry => entry.Name).Distinct())
			Diagnostics.Add(new(DiagnosticSeverity.Hint, name.SourceLocation, "Redundant import"));
		
		_explicitImports.Add((explicitImports, context));
		ReportRepeatedImports(node.Imports.Where(static import => !import.IsPrelude));
		return new(imports, explicitImports.Select(static entry => entry.Symbol), hidden, activated);
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
		
		foreach (var repeated in names.GroupBy(static e => (e.Module, e.Token.Text)).Where(static g => g.Count() > 1))
		{
			foreach (var (_, token) in repeated)
				Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation,
					$"'{token.Text}' is imported more than once"));
		}
	}
	
	private void ReportImportConflicts(List<(Symbol Symbol, Token Name, string Module)> explicitImports,
		ResolutionContext context, Dictionary<Symbol, HashSet<string>> importers)
	{
		foreach (var (symbol, name, module) in explicitImports)
		{
			var declared = context.File.Module.Files
				.SelectMany(f => f.Symbols.GetValueOrDefault(symbol.Name) ?? [])
				.Where(context.IsVisible)
				.ToList();
			
			if (declared.Count == 0 ||
			    symbol is FunctionSymbol && declared.All(static member => member is FunctionSymbol))
				continue;
			
			var message = $"'{symbol.Name}' is ambiguous";
			Diagnostics.Add(new(DiagnosticSeverity.Error, name.SourceLocation, message)
			{
				Hints = [$"Also declared in '{context.File.Module.Name}'"]
			});
			
			foreach (var member in declared)
				importers.GetOrAdd(member).Add(module);
		}
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
	
	private List<(Symbol Symbol, Token? Name)> CollectImport(ImportExpression import, ResolutionContext context,
		List<Symbol> hidden)
	{
		var path = import.ModuleName.Parts;
		var symbol = context.ResolveMembers(Modules.Root, path, 0);
		if (symbol is ModulePathSymbol module)
		{
			if (import.Import is FullImport)
				hidden.AddRange(module.Members.Values
					.Concat(module.PrivateMembers.Values)
					.SelectMany(static m => m)
					.Where(m => !context.IsVisible(m)));
			
			return CollectImport(module, import.Import, context);
		}
		
		if (symbol is not null)
			Diagnostics.Add(ResolutionContext.ReportNotModule(symbol, path));
		
		return [];
	}
	
	private List<(Symbol Symbol, Token? Name)> CollectImport(ModulePathSymbol module, IImport import,
		ResolutionContext context) => import switch
	{
		FullImport =>
		[
			..module.Members.Values.SelectMany(static m => m).Where(context.IsVisible)
				.Select(static symbol => (symbol, (Token?)null))
		],
		TokenImport i => ImportMember(module, i.Token, context),
		ListImport i => [..i.Tokens.SelectMany(t => ImportMember(module, t, context))],
		_ => []
	};
	
	private List<(Symbol Symbol, Token? Name)> ImportMember(ModulePathSymbol module, Token name,
		ResolutionContext context)
	{
		if (module.Members.TryGetValue(name.Text, out var members) &&
		    members.Where(context.IsVisible).ToList() is { Count: > 0 } visible)
			return [..visible.Select(symbol => (symbol, (Token?)name))];
		
		if (module.Children.TryGetValue(name.Text, out var child))
			return [(child, name)];
		
		Diagnostics.Add(context.ReportUndefinedMember(name.SourceLocation, module, name.Text));
		return [];
	}
}