using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class FunctionTypeNode
(
	SourceLocation sourceLocation,
	Token keyword,
	bool isExternal,
	bool isRef,
	IEnumerable<Token?> parameterModes,
	IEnumerable<ITypeNode> parameterTypes,
	ITypeNode? returnType
) : ITypeNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public Token Keyword { get; } = keyword;
	public bool IsExternal { get; } = isExternal;
	public bool IsRef { get; } = isRef;
	public ImmutableArray<Token?> ParameterModes { get; } = parameterModes.ToImmutableArray();
	public ImmutableArray<ITypeNode> ParameterTypes { get; } = parameterTypes.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
}