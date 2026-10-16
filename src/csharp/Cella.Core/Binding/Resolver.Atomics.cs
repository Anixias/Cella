using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Binding;

public sealed partial class Resolver
{
	private sealed record GenericUse
	(
		FunctionSymbol? Caller,
		FunctionSymbol Callee,
		ImmutableArray<TypeSymbol> TypeArguments,
		SourceLocation Location
	);
	
	private readonly List<GenericUse> _genericUses = [];
	
	private void AddGenericUse(GenericUse use)
	{
		_genericUses.Add(use);
		Journal(() => _genericUses.RemoveAt(_genericUses.Count - 1));
	}
	
	private void AddAtomicRequirement(FunctionSymbol owner, AtomicRequirement requirement)
	{
		if (_typePool.AddAtomicRequirement(owner, requirement))
			Journal(() => _typePool.RemoveAtomicRequirement(owner, requirement));
	}
	
	public void ReportAtomicInstances()
	{
		bool changed;
		do
		{
			changed = false;
			foreach (var use in _genericUses)
			{
				foreach (var requirement in _typePool.GetAtomicRequirements(use.Callee).ToList())
				{
					if (FindCallerParameter(use, requirement) is { } parameter)
						changed |= _typePool.AddAtomicRequirement(use.Caller!,
							requirement with { Parameter = parameter });
				}
			}
		} while (changed);
		
		var reported = new HashSet<SourceLocation>();
		foreach (var use in _genericUses)
		{
			foreach (var requirement in _typePool.GetAtomicRequirements(use.Callee))
			{
				if (FindArgument(use, requirement) is not { } argument || argument is InvalidType ||
				    FindCallerParameter(use, requirement) is not null ||
				    TypePool.SupportsAtomic(requirement.Operation, argument) || !reported.Add(use.Location))
					continue;
				
				Diagnostics.Add(new(DiagnosticSeverity.Error, use.Location,
					$"Cannot use '{requirement.Operator}' atomically on '{argument.Name}'")
				{
					Hints = [DescribeUse(use.Location, requirement.Location)]
				});
			}
		}
	}
	
	private static TypeSymbol? FindArgument(GenericUse use, AtomicRequirement requirement)
	{
		var index = use.Callee.TypeParameters.IndexOf(requirement.Parameter);
		return index >= 0 && index < use.TypeArguments.Length ? use.TypeArguments[index] : null;
	}
	
	private static TypeParameterSymbol? FindCallerParameter(GenericUse use, AtomicRequirement requirement) =>
		FindArgument(use, requirement) is TypeParameterSymbol parameter &&
		use.Caller?.TypeParameters.Contains(parameter) == true
			? parameter
			: null;
	
	private static string DescribeUse(SourceLocation call, SourceLocation use) => use.Source == call.Source
		? $"Used on line {use.GetLineColumn().Line}"
		: $"Used in '{Path.GetFileName(use.Source.FilePath)}' on line {use.GetLineColumn().Line}";
}