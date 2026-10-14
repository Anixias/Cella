using System.Collections.Immutable;
using Cella.Core.Text;

namespace Cella.Core.Syntax.Nodes;

public sealed class IsExpressionNode
(
	Token? mode,
	IExpressionNode value,
	PatternNode pattern,
	SourceLocation sourceLocation
) : IExpressionNode
{
	public Token? Mode { get; } = mode;
	public IExpressionNode Value { get; } = value;
	public PatternNode Pattern { get; } = pattern;
	public SourceLocation SourceLocation { get; } = sourceLocation;
	public bool IsContained => false;
}

public enum PatternKind
{
	Case,
	Type,
	Binding,
	Value,
	Range,
	Relational
}

public sealed class PatternNode
{
	private PatternNode(PatternKind kind, SourceLocation sourceLocation)
	{
		Kind = kind;
		SourceLocation = sourceLocation;
	}
	
	public PatternKind Kind { get; }
	public SourceLocation SourceLocation { get; }
	public ImmutableArray<Token> TypePath { get; private init; } = [];
	public Token CaseName { get; private init; }
	public ImmutableArray<PatternNode> Elements { get; private init; } = [];
	public bool HasParentheses { get; private init; }
	public ITypeNode? Type { get; private init; }
	public Token? TypeBinding { get; private init; }
	public Token? Mode { get; private init; }
	public IExpressionNode? Value { get; private init; }
	public IExpressionNode? Upper { get; private init; }
	public Token? Operator { get; private init; }
	
	public bool IsInclusive => Operator?.Type == TokenType.OpDotDotEqual;
	
	public static PatternNode CreateCase(IEnumerable<Token> typePath, Token caseName, IEnumerable<PatternNode> elements,
		bool hasParentheses, SourceLocation sourceLocation) => new(PatternKind.Case, sourceLocation)
	{
		TypePath = [..typePath],
		CaseName = caseName,
		Elements = [..elements],
		HasParentheses = hasParentheses
	};
	
	public static PatternNode CreateType(IEnumerable<Token> typePath, Token caseName, ITypeNode? type,
		Token? binding, SourceLocation sourceLocation) => new(PatternKind.Type, sourceLocation)
	{
		TypePath = [..typePath],
		CaseName = caseName,
		Type = type,
		TypeBinding = binding
	};
	
	public static PatternNode CreateBinding(Token? mode, Token name)
	{
		var (source, range) = (mode ?? name).SourceLocation;
		return new PatternNode(PatternKind.Binding, new(source, range.Join(name.SourceLocation.Range)))
		{
			Mode = mode,
			CaseName = name
		};
	}
	
	public static PatternNode CreateValue(IExpressionNode value) =>
		new(PatternKind.Value, value.SourceLocation) { Value = value };
	
	public static PatternNode CreateRange(IExpressionNode lower, Token op, IExpressionNode upper)
	{
		var (source, range) = lower.SourceLocation;
		return new PatternNode(PatternKind.Range, new(source, range.Join(upper.SourceLocation.Range)))
		{
			Value = lower,
			Operator = op,
			Upper = upper
		};
	}
	
	public static PatternNode CreateRelational(Token op, IExpressionNode value)
	{
		var (source, range) = op.SourceLocation;
		return new PatternNode(PatternKind.Relational, new(source, range.Join(value.SourceLocation.Range)))
		{
			Operator = op,
			Value = value
		};
	}
	
	public PatternNode WithValue(IExpressionNode value) => new(Kind, SourceLocation)
	{
		TypePath = TypePath,
		CaseName = CaseName,
		Elements = Elements,
		HasParentheses = HasParentheses,
		Value = value
	};
}