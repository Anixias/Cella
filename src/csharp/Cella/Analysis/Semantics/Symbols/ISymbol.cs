using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics.Symbols;

public interface ISymbol
{
	string Name { get; }
	List<SourceLocation> DeclarationLocations { get; }
	List<SourceLocation> UsageLocations { get; }
	//Type? EvaluatedType { get; }
	//bool IsConstant { get; }
	//bool IsStatic { get; }
}