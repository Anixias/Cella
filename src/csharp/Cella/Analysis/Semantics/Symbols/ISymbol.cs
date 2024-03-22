namespace Cella.Analysis.Semantics.Symbols;

public interface ISymbol
{
	string Name { get; }
	string SymbolTypeName { get; }
	//Type? EvaluatedType { get; }
	//bool IsConstant { get; }
	//bool IsStatic { get; }
}