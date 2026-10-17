using Cella.Compiler.Projects;

namespace Cella.Compiler.Building;

internal readonly record struct ProjectInfo
(
	string FilePath,
	string Directory,
	string Name,
	CellaProject Project
);