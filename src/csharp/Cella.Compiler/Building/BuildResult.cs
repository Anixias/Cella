using System.Collections.Immutable;
using Cella.Compiler.Projects;
using Cella.Core.Binding;
using Cella.Core.Binding.Nodes;
using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public sealed record BuildResult
(
	bool Succeeded,
	ImmutableArray<ProjectCheck> Projects,
	ImmutableArray<ProjectDiagnostic> ProjectDiagnostics
);

public sealed record ProjectCheck
(
	string ProjectPath,
	ImmutableArray<Diagnostic> Diagnostics,
	ImmutableArray<ResolvedFileNode> Files
)
{
	public Lazy<SemanticModel>? Model { get; init; }
}