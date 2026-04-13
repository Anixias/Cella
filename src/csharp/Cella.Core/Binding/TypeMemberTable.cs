using Cella.Core.Symbols;

namespace Cella.Core.Binding;

public sealed class TypeMemberTable
{
	private readonly Dictionary<TypeSymbol, OrderedDictionary<string, MemberSymbol>> _members = [];
	
	public void Register(TypeSymbol type, MemberSymbol member) =>
		_members.GetOrAdd(type)[member.Name] = member;
	
	public MemberSymbol? Resolve(TypeSymbol type, string name) =>
		_members.GetValueOrDefault(type)?.GetValueOrDefault(name);
	
	public int GetFieldIndex(TypeSymbol type, MemberSymbol member) =>
		_members[type].IndexOf(member.Name);
	
	public IReadOnlyList<MemberSymbol> GetMembers(TypeSymbol type) => _members[type].Values;
	
	public void CreateNativeMembers()
	{
		// TODO constructors, str.toCstr(), str.getCharLength(), etc.
		//Register(NativeSymbols.Str, new IntrinsicMemberSymbol("byteLength", NativeSymbols.UIntSize));
	}
	
	public void CreateArrayMembers(ArrayType type)
	{
		var length = new PropertySymbol("length", NativeSymbols.UIntSize)
		{
			Getter = new NativeAccessor(NativeMemberIntrinsic.ArrayLength)
		};
		
		Register(type, length);
	}
	
	public void CreateBufferMembers(BufferType type)
	{
		var length = new FieldSymbol("length", NativeSymbols.UIntSize, false);
		Register(type, length);
		var data = new FieldSymbol("data", new PointerType(type.ElementType, PointerKind.Owning), false);
		Register(type, data);
	}
	
	public void CreateSpanMembers(SpanType type)
	{
		var length = new FieldSymbol("length", NativeSymbols.UIntSize, false);
		Register(type, length);
		var data = new FieldSymbol("data", new PointerType(type.ElementType, PointerKind.Mutable), false);
		Register(type, data);
	}
	
	public void CreateViewMembers(ViewType type)
	{
		var length = new FieldSymbol("length", NativeSymbols.UIntSize, false);
		Register(type, length);
		var data = new FieldSymbol("data", new PointerType(type.ElementType, PointerKind.Immutable), false);
		Register(type, data);
	}
}