using System.Collections.Immutable;
using Cella.Core.Binding.Nodes;
using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public sealed record BuildResult(bool Succeeded, ImmutableArray<ProjectCheck> Projects);

public sealed record ProjectCheck(
	string ProjectPath,
	ImmutableArray<Diagnostic> Diagnostics,
	ImmutableArray<ResolvedFileNode> Files);
