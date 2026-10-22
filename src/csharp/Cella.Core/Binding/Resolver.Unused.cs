using System.Text.RegularExpressions;
using Cella.Core.Binding.Nodes;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private readonly HashSet<object> _usedDeclarations = new(ReferenceEqualityComparer.Instance);
	
	private void MarkUsed(Symbol? symbol)
	{
		switch (symbol)
		{
			case FunctionSymbol function:
				_usedDeclarations.Add(function.Syntax);
				break;
			
			case MethodSymbol method:
				_usedDeclarations.Add(method.Function.Syntax);
				break;
			
			case GlobalSymbol global:
				_usedDeclarations.Add(global.Syntax);
				break;
			
			case AmbiguousSymbol ambiguous:
				foreach (var candidate in ambiguous.Candidates)
					MarkUsed(candidate);
				
				break;
		}
	}
	
	public IEnumerable<Diagnostic> FindUnusedDeclarations(IEnumerable<ResolvedFileNode> files,
		ErrorContainment containment)
	{
		foreach (var file in files)
		{
			var syntax = file.Symbol.Syntax;
			var unresolved = syntax.InactiveCode
				.Concat(syntax.BrokenStatements.Select(static statement => statement.SourceLocation))
				.Select(static location => location.GetText().ToString())
				.ToList();
			
			foreach (var (node, name) in FindDeclarations(file.Declarations))
			{
				if (_usedDeclarations.Contains(node.Syntax) || containment.IsBroken(node) ||
				    unresolved.Any(text => Regex.IsMatch(text, $@"\b{Regex.Escape(name.Text)}\b")))
					continue;
				
				yield return new Diagnostic(DiagnosticSeverity.Hint, name.SourceLocation,
					$"'{name.Text}' is never used")
				{
					Lint = Lints.UnusedDeclaration
				};
			}
		}
	}
	
	public IEnumerable<Diagnostic> FindUnusedImports(IEnumerable<ResolvedFileNode> files)
	{
		const string title = "Remove unused import";
		foreach (var file in files)
		{
			var syntax = file.Symbol.Syntax;
			var unresolved = syntax.InactiveCode
				.Concat(syntax.BrokenStatements.Select(static statement => statement.SourceLocation))
				.Select(static location => location.GetText().ToString())
				.ToList();
			
			var errors = _signatures.Diagnostics.Concat(Diagnostics)
				.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error &&
				                     diagnostic.SourceLocation.Source == syntax.SourceLocation.Source)
				.Select(static diagnostic => diagnostic.SourceLocation.Range)
				.ToList();
			
			var firstDeclaration = syntax.Declarations
				.Select(static declaration => declaration.SourceLocation.Range.Start)
				.DefaultIfEmpty(int.MaxValue)
				.Min();
			
			var unused = _signatures.GetImports(file.Symbol).FindUnused()
				.Where(use => use.Import.SourceLocation.Range.Start < firstDeclaration)
				.Where(use => !use.Import.HasSyntaxErrors && !errors.Any(error =>
					error.Start >= use.Import.SourceLocation.Range.Start &&
					error.Start < use.Import.SourceLocation.Range.End))
				.Where(use => use.Name is { } name
					? !_signatures.IsRedundantImport(name) &&
					  !unresolved.Any(text => Regex.IsMatch(text, $@"\b{Regex.Escape(name.Text)}\b"))
					: unresolved.Count == 0);
			
			foreach (var group in unused.GroupBy(static use => use.Import.SourceLocation))
			{
				var import = group.First().Import;
				var names = group.Select(static use => use.Name).OfType<Token>().ToList();
				if (import.Import is ListImport list && names.Count < list.Tokens.Length)
				{
					foreach (var name in names)
						yield return new(DiagnosticSeverity.Hint, name.SourceLocation, "Unused import")
						{
							UnnecessaryLocation = name.SourceLocation,
							Fixes = [SignatureCollector.RemoveImport(syntax.Imports, name, title)],
							Lint = Lints.UnusedImport
						};
					
					continue;
				}
				
				yield return new(DiagnosticSeverity.Hint, import.SourceLocation, "Unused import")
				{
					UnnecessaryLocation = import.SourceLocation,
					Fixes = [DiagnosticFix.Remove(title, import.SourceLocation)],
					Lint = Lints.UnusedImport
				};
			}
		}
	}
	
	private IEnumerable<(IResolvedDeclarationNode Node, Token Name)> FindDeclarations(
		IEnumerable<IResolvedDeclarationNode> declarations)
	{
		foreach (var declaration in declarations)
		{
			switch (declaration)
			{
				case ResolvedFunctionNode { Syntax: FunctionNode { IsExternal: false } syntax } function when
					syntax.Identifier.Type == TokenType.Identifier && syntax.Identifier.Text != "main" &&
					function.FunctionInfo.Symbol is { Visibility: not Visibility.Public, Property: null } symbol &&
					!_signatures.IsWitness(symbol):
					yield return (function, syntax.Identifier);
					break;
				
				case ResolvedGlobalNode { Syntax: GlobalNode { IsExternal: false } syntax } global when
					global.Info.Symbol.Visibility != Visibility.Public && !_signatures.IsWitness(global.Info.Symbol):
					yield return (global, syntax.Identifier);
					break;
				
				case ResolvedRecordNode record:
					foreach (var member in FindDeclarations(record.Members))
						yield return member;
					
					break;
				
				case ResolvedEnumNode enumNode:
					foreach (var member in FindDeclarations(enumNode.Members))
						yield return member;
					
					break;
				
				case ResolvedImplNode { Symbol.Traits.IsEmpty: true } impl:
					foreach (var member in FindDeclarations(impl.Members))
						yield return member;
					
					break;
			}
		}
	}
}