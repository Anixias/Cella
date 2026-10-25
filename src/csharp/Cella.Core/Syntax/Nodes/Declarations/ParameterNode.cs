using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class ParameterNode(Token? mode, Token identifier, ITypeNode type, IExpressionNode? defaultValue)
	: IDeclarationNode
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token? Mode { get; } = mode;
	public Token Identifier { get; } = identifier;
	public ITypeNode Type { get; } = type;
	public IExpressionNode? DefaultValue { get; } = defaultValue;
	public Token? Ellipsis { get; init; }
	public bool IsVariadic => Ellipsis is not null;
}

public sealed class ReceiverNode(Token? mode, Token self)
{
	public SourceLocation SourceLocation => Self.SourceLocation;
	public Token? Mode { get; } = mode;
	public Token Self { get; } = self;
}

public sealed class TypeParameterNode
(
	Token identifier,
	IEnumerable<Token> keywords,
	IEnumerable<ITypeNode> traits,
	IEnumerable<ConstructorConstraintNode> constructors
)
{
	public SourceLocation SourceLocation => Identifier.SourceLocation;
	public Token Identifier { get; } = identifier;
	public ImmutableArray<Token> Keywords { get; } = keywords.ToImmutableArray();
	public ImmutableArray<ITypeNode> Traits { get; } = traits.ToImmutableArray();
	public ImmutableArray<ConstructorConstraintNode> Constructors { get; } = constructors.ToImmutableArray();
	public ITypeNode? ValueType { get; init; }
	public ITypeNode? DefaultType { get; init; }
	public IExpressionNode? DefaultValue { get; init; }
	public ImmutableArray<TypeParameterNode> Nested { get; init; } = [];
	public ImmutableArray<TypeParameterNode> Declared { get; init; } = [];
	public ImmutableArray<ITypeNode> ConstantTraits { get; init; } = [];
	public ITypeNode? EnumTag { get; init; }
	
	public static IEnumerable<TypeParameterNode> CollectNestedLeaves(IEnumerable<TypeParameterNode> nodes) => nodes
		.SelectMany(static node => node.Nested.IsEmpty
			? []
			: node.Nested.SelectMany(static child => child.Nested.IsEmpty
				? [child]
				: CollectNestedLeaves([child])));
	
	public static IEnumerable<TypeParameterNode> CollectDeclared(IEnumerable<TypeParameterNode> nodes) => nodes
		.SelectMany(static node => node.Declared
			.Concat(CollectDeclared(node.Declared))
			.Concat(CollectDeclared(node.Nested)));
	
	public static IEnumerable<Token> CollectBareNames(IEnumerable<TypeParameterNode> nodes) => nodes
		.SelectMany(static node => node.Traits.SelectMany(CollectBareNames)
			.Concat(CollectBareNames(node.Declared))
			.Concat(CollectBareNames(node.Nested)));
	
	private static IEnumerable<Token> CollectBareNames(ITypeNode type) => type is GenericTypeNode generic
		? generic.Arguments.SelectMany(static argument => argument switch
		{
			IdentifierArgumentNode identifier => [identifier.Identifier],
			TypeArgumentNode typeArgument => CollectBareNames(typeArgument.Type),
			_ => []
		})
		: [];
}

public sealed class ConstructorConstraintNode
(
	Token keyword,
	IEnumerable<Token?> modes,
	IEnumerable<ITypeNode> types,
	Token closeParen
) : IDeclarationNode
{
	public Token Keyword { get; } = keyword;
	public ImmutableArray<Token?> Modes { get; } = modes.ToImmutableArray();
	public ImmutableArray<ITypeNode> Types { get; } = types.ToImmutableArray();
	
	public SourceLocation SourceLocation { get; } = keyword.SourceLocation with
	{
		Range = keyword.SourceLocation.Range.Join(closeParen.SourceLocation.Range)
	};
}