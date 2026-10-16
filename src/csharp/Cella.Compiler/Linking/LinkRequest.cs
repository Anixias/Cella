using System.Collections.Immutable;
using Cella.Compiler.Projects;
using Cella.Core.CodeGen;

namespace Cella.Compiler.Linking;

public sealed class LinkRequest
(
	ProjectOutputType outputType,
	IEnumerable<string> inputFiles,
	string outputFile,
	params IEnumerable<string> libFiles
)
{
	public ProjectOutputType OutputType { get; } = outputType;
	public ImmutableArray<string> InputFiles { get; } = inputFiles.ToImmutableArray();
	public string OutputFile { get; } = outputFile;
	public ImmutableArray<string> LibFiles { get; } = libFiles.ToImmutableArray();
	public SystemLinkPreference LinkPreference { get; init; } = SystemLinkPreference.Dynamic;
	public bool DebugInfo { get; init; }
	public OptimizeMode OptimizeMode { get; init; }
	public string? ModuleDefinition { get; init; }
	public ImmutableArray<string> KeptSymbols { get; init; } = [];
	public bool WideEntry { get; init; }
	public ImmutableArray<string> ImportedSymbols { get; init; } = [];
}