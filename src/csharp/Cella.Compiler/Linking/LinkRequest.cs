using System.Collections.Immutable;
using Cella.Compiler.Projects;

namespace Cella.Compiler.Linking;

public sealed class LinkRequest(ProjectOutputType outputType, IEnumerable<string> inputFiles, string outputFile,
	string toolchainsDirectory, params IEnumerable<string> libFiles)
{
	public ProjectOutputType OutputType { get; } = outputType;
	public ImmutableArray<string> InputFiles { get; } = inputFiles.ToImmutableArray();
	public string OutputFile { get; } = outputFile;
	public string ToolchainsDirectory { get; } = toolchainsDirectory;
	public ImmutableArray<string> LibFiles { get; } = libFiles.ToImmutableArray();
	public SystemLinkPreference LinkPreference { get; init; } = SystemLinkPreference.Dynamic;
}