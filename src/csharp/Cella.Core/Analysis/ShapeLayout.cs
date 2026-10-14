using System.Numerics;
using Cella.Core.Binding;
using Cella.Core.Symbols;

namespace Cella.Core.Analysis;

public sealed class ShapeLayout(TypePool typePool)
{
	private readonly Dictionary<RecordSymbol, List<FieldSymbol>> _fields = [];
	
	public static BigInteger? GetLength(TypeSymbol type) =>
		type is ArrayType { LengthParameter: null } array && array.Length.Sign >= 0 ? array.Length : null;
	
	public List<FieldSymbol>? GetFields(TypeSymbol type)
	{
		if (type is not RecordSymbol { IsUnion: false } record)
			return null;
		
		if (!_fields.TryGetValue(record, out var fields))
			_fields[record] = fields = typePool.GetMembers(record).OfType<FieldSymbol>().ToList();
		
		return fields;
	}
	
	public Shape Get(Shape shape, TypeSymbol type, ReadOnlySpan<Projection> suffix, out int depth)
	{
		depth = 0;
		while (depth < suffix.Length && Step(shape, type, suffix[depth]) is var (part, partType))
		{
			shape = part;
			type = partType;
			depth++;
		}
		
		return shape;
	}
	
	private (Shape Part, TypeSymbol Type)? Step(Shape shape, TypeSymbol type, Projection projection)
	{
		switch (projection)
		{
			case FieldProjection field when FindField(type, field) is var index and >= 0:
				return (FieldsShape.GetPart(shape, index), typePool.GetTypeOfMember(field.Field));
			
			case IndexProjection { Index: { } index } when IsInBounds(type, index):
				return (shape is ElementsShape elements ? elements.Get(index) : shape, ((ArrayType)type).ElementType);
			
			default:
				return null;
		}
	}
	
	public Shape Update(Shape shape, TypeSymbol type, Projection projection, Func<Shape, TypeSymbol, Shape> update)
	{
		switch (projection)
		{
			case FieldProjection field when FindField(type, field) is var index and >= 0:
			{
				var count = GetFields(type)!.Count;
				var parts = Enumerable.Range(0, count).Select(i => FieldsShape.GetPart(shape, i)).ToArray();
				parts[index] = update(parts[index], typePool.GetTypeOfMember(field.Field));
				return FieldsShape.Create([..parts]);
			}
			
			case IndexProjection { Index: { } index } when IsInBounds(type, index):
			{
				var elements = ElementsShape.Expand(shape, GetLength(type)!.Value);
				return elements.Set(index, update(elements.Get(index), ((ArrayType)type).ElementType));
			}
			
			default:
				return shape;
		}
	}
	
	public TypeSymbol? GetPartType(TypeSymbol type, ReadOnlySpan<Projection> path)
	{
		foreach (var projection in path)
		{
			TypeSymbol? part = projection switch
			{
				FieldProjection field when FindField(type, field) >= 0 => typePool.GetTypeOfMember(field.Field),
				IndexProjection { Index: { } index } when IsInBounds(type, index) => ((ArrayType)type).ElementType,
				_ => null
			};
			
			if (part is null)
				return null;
			
			type = part;
		}
		
		return type;
	}
	
	public bool IsInBounds(TypeSymbol type, BigInteger index) =>
		GetLength(type) is { } length && index.Sign >= 0 && index < length;
	
	private int FindField(TypeSymbol type, FieldProjection projection) =>
		GetFields(type) is { } fields ? fields.IndexOf(projection.Field) : -1;
}