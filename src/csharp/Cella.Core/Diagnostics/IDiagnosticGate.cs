namespace Cella.Diagnostics;

public interface IDiagnosticGate
{
	bool Admit(Diagnostic diagnostic);
	void Record(Diagnostic diagnostic);
}