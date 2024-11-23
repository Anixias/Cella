using Cella.Core.Diagnostics;

namespace Cella.Compiler;

public readonly struct CompilationResult
{
	public bool IsSuccess => diagnostics.ErrorCount == 0;
	
	public readonly DiagnosticList diagnostics;
	// Todo: Add custom IR output here
	
	public CompilationResult(DiagnosticList diagnostics)
	{
		this.diagnostics = diagnostics;
	}
}