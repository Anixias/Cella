using Cella.Diagnostics;

namespace Cella.Compiler.Building;

public interface IBuildOutput
{
	void WriteLine(string text);
	void WriteError(string text);
	void Report(IEnumerable<Diagnostic> diagnostics);
}
