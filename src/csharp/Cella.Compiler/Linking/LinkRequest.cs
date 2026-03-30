using System.Collections.Immutable;

namespace Cella.Compiler.Linking;

public sealed class LinkRequest(IEnumerable<string> inputFiles, string outputFile, string? toolchainsDirectory = null)
{
	public ImmutableArray<string> InputFiles { get; } = inputFiles.ToImmutableArray();
	public string OutputFile { get; } = outputFile;
	public string ToolchainsDirectory { get; } = toolchainsDirectory ?? @"C:\cella\toolchains";
}