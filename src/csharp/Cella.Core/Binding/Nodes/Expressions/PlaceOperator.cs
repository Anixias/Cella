namespace Cella.Core.Binding.Nodes;

public enum PlaceOperator
{
	Dereference,
	Indexer
}

public static class PlaceOperatorExtensions
{
	public static string GetName(this PlaceOperator op) => op == PlaceOperator.Dereference ? "*" : "[]";
}