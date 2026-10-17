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
		foreach (var importExpression in node.Imports)
		{
			var collected = CollectImport(importExpression, context, hidden);
			imports.AddRange(collected);
			if (context.ResolveMembers(Modules.Root, importExpression.ModuleName.Parts, 0) is ModulePathSymbol imported)
				activated.AddRange(Modules.GetModules(imported));
			
			activated.AddRange(collected.OfType<ModulePathSymbol>().SelectMany(Modules.GetModules));
			if (importExpression.Import is TokenImport or ListImport)
				explicitImports.AddRange(collected.Select(symbol =>
					(symbol, FindImportName(importExpression, symbol), importExpression.ModuleName.Text)));
		}
		
		_explicitImports.Add((explicitImports, context));
		ReportRepeatedImports(node.Imports);
		return new(imports, explicitImports.Select(static entry => entry.Symbol), hidden, activated);
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
	
	private static Token FindImportName(ImportExpression import, Symbol symbol) => import.Import switch
	{
		TokenImport single => single.Token,
		ListImport list => list.Tokens.First(token => token.Text == symbol.Name),
		_ => throw new InvalidOperationException()
	};
	
	private void ReportImportConflicts(List<(Symbol Symbol, Token Name, string Module)> explicitImports,
		ResolutionContext context)
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
			{
				var location = GetIdentifier(_declarations[member].Node).SourceLocation;
				if (_reportedImportConflicts.Add(location))
					Diagnostics.Add(new(DiagnosticSeverity.Error, location, message)
					{
						Hints = [$"Also imported from '{module}'"]
					});
			}
		}
	}
	
	private List<Symbol> CollectImport(ImportExpression import, ResolutionContext context, List<Symbol> hidden)
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
	
	private List<Symbol> CollectImport(ModulePathSymbol module, IImport import, ResolutionContext context) =>
		import switch
		{
			FullImport => [..module.Members.Values.SelectMany(static m => m).Where(context.IsVisible)],
			TokenImport i => ImportMember(module, i.Token, context),
			ListImport i => [..i.Tokens.SelectMany(t => ImportMember(module, t, context))],
			_ => []
		};
	
	private List<Symbol> ImportMember(ModulePathSymbol module, Token name, ResolutionContext context)
	{
		if (module.Members.TryGetValue(name.Text, out var members) &&
		    members.Where(context.IsVisible).ToList() is { Count: > 0 } visible)
			return visible;
		
		if (module.Children.TryGetValue(name.Text, out var child))
			return [child];
		
		Diagnostics.Add(context.ReportUndefinedMember(name.SourceLocation, module, name.Text));
		return [];
	}
}