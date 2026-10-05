using System.Collections.Immutable;
using System.Numerics;
using Cella.Core.Symbols;

namespace Cella.Core.Analysis;

public abstract record Projection;
public sealed record FieldProjection(FieldSymbol Field) : Projection;
public sealed record IndexProjection(BigInteger? Index) : Projection;
public sealed record PayloadProjection(EnumCaseSymbol Case, int Index) : Projection;
public sealed record DerefProjection : Projection;

public sealed record Place(VariableSymbol Root, ImmutableArray<Projection> Path)
{
	public Place Project(Projection projection) => this with { Path = Path.Add(projection) };
}