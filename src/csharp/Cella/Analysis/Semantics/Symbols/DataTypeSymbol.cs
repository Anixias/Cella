using Cella.Analysis.Text;

namespace Cella.Analysis.Semantics.Symbols;

public abstract class DataTypeSymbol : ISymbol
{
	private static uint nextID = 1u;

	public string Name { get; }
	public List<SourceLocation> DeclarationLocations { get; } = new();
	public List<SourceLocation> UsageLocations { get; } = new();
	public uint TypeID { get; }
	public DataType BaseType => new DataType.Base(this);

	protected DataTypeSymbol(string name)
	{
		Name = name;
		TypeID = nextID++;
	}
}

public sealed class TypeSymbol : DataTypeSymbol
{
	public Scope Scope { get; }
	
	public TypeSymbol(string name, Scope scope) : base(name)
	{
		Scope = scope;
	}
}

// Todo: Other DataTypes, such as Enum