namespace Cella.Analysis.Semantics.Symbols;

public abstract class DataTypeSymbol : ISymbol
{
	private static uint nextID = 1u;

	public string Name { get; }
	public uint TypeID { get; }
	public abstract string SymbolTypeName { get; }
	public DataType BaseType => new DataType.Base(this);

	protected DataTypeSymbol(string name)
	{
		Name = name;
		TypeID = nextID++;
	}
}

public sealed class TypeSymbol : DataTypeSymbol
{
	public override string SymbolTypeName => "a type";
	public Scope Scope { get; }
	
	public TypeSymbol(string name, Scope scope) : base(name)
	{
		Scope = scope;
	}
}

// Todo: Other DataTypes, such as Enum