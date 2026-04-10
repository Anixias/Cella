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
		Register(NativeSymbols.Str, new IntrinsicMemberSymbol("byteLength", NativeSymbols.UIntSize));
	}
	
	public void CreateArrayMembers(ArrayType type)
	{
		Register(type, new IntrinsicMemberSymbol("length", NativeSymbols.UIntSize));
	}
	
	public void CreateSpanMembers(SpanType type)
	{
		Register(type, new IntrinsicMemberSymbol("length", NativeSymbols.UIntSize));
	}
	
	public void CreateViewMembers(ViewType type)
	{
		Register(type, new IntrinsicMemberSymbol("length", NativeSymbols.UIntSize));
	}
}