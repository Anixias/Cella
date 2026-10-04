using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class FunctionTypeNode
(
	SourceLocation sourceLocation,
	bool isExternal,
	IEnumerable<ITypeNode> parameterTypes,
	ITypeNode? returnType
) : ITypeNode
{
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsExternal { get; } = isExternal;
	public ImmutableArray<ITypeNode> ParameterTypes { get; } = parameterTypes.ToImmutableArray();
	public ITypeNode? ReturnType { get; } = returnType;
}