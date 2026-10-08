using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class ExpressionParser(ImmutableArray<Token> tokens) : BaseParser<IExpressionNode>(tokens)
{
	private static readonly HashSet<TokenType> _literalTypes =
	[
		TokenType.KeywordTrue,
		TokenType.KeywordFalse,
		TokenType.KeywordNull,
		TokenType.IntegerLiteral,
		TokenType.FloatLiteral,
		TokenType.StringLiteral,
		TokenType.CharLiteral,
		TokenType.InvalidCharLiteral
	];
	
	private static readonly HashSet<TokenType> _jumpKeywords =
	[
		TokenType.KeywordRet,
		TokenType.KeywordBreak,
		TokenType.KeywordCont
	];
	
	private static readonly HashSet<TokenType> _expressionStarts =
	[
		TokenType.Identifier,
		TokenType.InterpolatedStringLiteral,
		TokenType.OpOpenParen,
		TokenType.OpOpenBracket,
		TokenType.KeywordUndef,
		TokenType.KeywordSizeOf,
		TokenType.KeywordAlignOf,
		TokenType.KeywordNameOf,
		TokenType.KeywordMatch,
		TokenType.KeywordMut,
		TokenType.KeywordOwn,
		TokenType.KeywordImm,
		TokenType.KeywordSelf,
		TokenType.KeywordFun,
		TokenType.KeywordDyn,
		TokenType.KeywordAtomic,
		TokenType.KeywordRet,
		TokenType.KeywordBreak,
		TokenType.KeywordCont
	];
	
	private static readonly HashSet<TokenType> _binaryOperatorNames =
	[
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual,
		TokenType.OpLess,
		TokenType.OpLessEqual,
		TokenType.OpGreater,
		TokenType.OpGreaterEqual,
		TokenType.OpPlus,
		TokenType.OpMinus,
		TokenType.OpStar,
		TokenType.OpSlash,
		TokenType.OpPercent,
		TokenType.OpLessLess,
		TokenType.OpGreaterGreater,
		TokenType.OpLessLessLess,
		TokenType.OpGreaterGreaterGreater,
		TokenType.OpAmpersand,
		TokenType.OpBar,
		TokenType.OpHat
	];
	
	private static readonly HashSet<TokenType> _shiftOps =
	[
		TokenType.OpLessLess,
		TokenType.OpGreaterGreater,
		TokenType.OpLessLessLess,
		TokenType.OpGreaterGreaterGreater
	];
	
	private static readonly HashSet<TokenType> _additiveOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus
	];
	
	private static readonly HashSet<TokenType> _unaryPrefixOps =
	[
		TokenType.OpPlus,
		TokenType.OpMinus,
		TokenType.OpBang,
		TokenType.OpTilde,
		TokenType.OpAt,
		TokenType.OpStar
	];
	
	private static readonly HashSet<TokenType> _multiplicativeOps =
	[
		TokenType.OpStar,
		TokenType.OpSlash,
		TokenType.OpPercent,
	];
	
	private static readonly HashSet<TokenType> _assignmentOps =
	[
		TokenType.OpPlusEqual,
		TokenType.OpMinusEqual,
		TokenType.OpStarEqual,
		TokenType.OpSlashEqual,
		TokenType.OpPercentEqual,
		TokenType.OpAmpersandEqual,
		TokenType.OpBarEqual,
		TokenType.OpHatEqual,
		TokenType.OpLessLessEqual,
		TokenType.OpGreaterGreaterEqual,
		TokenType.OpLessLessLessEqual,
		TokenType.OpGreaterGreaterGreaterEqual,
		TokenType.OpEqual
	];
	
	private static readonly HashSet<TokenType> _equalityOps =
	[
		TokenType.OpEqualEqual,
		TokenType.OpBangEqual
	];
	
	private static readonly HashSet<TokenType> _borrowKeywords =
	[
		TokenType.KeywordImm,
		TokenType.KeywordMut
	];
	
	private static readonly HashSet<TokenType> _bindingModes =
	[
		TokenType.KeywordMut,
		TokenType.KeywordOwn,
		TokenType.KeywordVar
	];
	
	private static readonly HashSet<TokenType> _comparisonOps =
	[
		TokenType.OpGreaterEqual,
		TokenType.OpLessEqual,
		TokenType.OpGreater,
		TokenType.OpLess
	];
	
	private bool IsNextNewline(int next)
	{
		if (AtEnd(next - 1) || AtEnd(next))
			return false;
		
		return Tokens[next - 1].Line != Tokens[next].Line;
	}
	
	public override IExpressionNode Parse(ref int index) => ParseExpression(ref index);
	private IExpressionNode ParseExpression(ref int index) => ParseAssignment(ref index);
	private ITypeNode ParseType(ref int index) => new TypeParser(Tokens).Parse(ref index);
	
	private IExpressionNode ParseAssignment(ref int index)
	{
		var left = ParseLogicalOr(ref index);
		if (IsNextNewline(index) || !Match(ref index, out var op, _assignmentOps))
			return left;
		
		var right = ParseExpression(ref index);
		return left switch
		{
			AtomicExpressionNode { Place: not null, Op: null } atomic => atomic.Write(op, right),
			BinaryOpExpressionNode
				{
					Op.Type: TokenType.OpEqualEqual,
					Left: AtomicExpressionNode { Place: not null, Op: null } atomic
				} comparison when op.Type == TokenType.OpEqual =>
				atomic.CompareSwap(comparison.Op, comparison.Right, right),
			_ => new BinaryOpExpressionNode(left, op, right)
		};
	}
	
	private IExpressionNode ParseLogicalOr(ref int index)
	{
		var node = ParseLogicalAnd(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, TokenType.OpBarBar))
		{
			var right = ParseLogicalAnd(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseLogicalAnd(ref int index)
	{
		var node = ParseEquality(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, TokenType.OpAmpersandAmpersand))
		{
			var right = ParseEquality(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseEquality(ref int index)
	{
		var node = ParseIs(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, _equalityOps))
		{
			var right = ParseIs(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseIs(ref int index)
	{
		var start = index;
		if (Match(ref index, out var mode, TokenType.KeywordMut))
		{
			var value = ParseComparison(ref index);
			if (!IsNextNewline(index) && Match(ref index, TokenType.KeywordIs))
				return ParsePatternTest(ref index, mode, value);
			
			index = start;
		}
		
		var node = ParseComparison(ref index);
		return IsNextNewline(index) || !Match(ref index, TokenType.KeywordIs)
			? node
			: ParsePatternTest(ref index, null, node);
	}
	
	private IsExpressionNode ParsePatternTest(ref int index, Token? mode, IExpressionNode value)
	{
		var pattern = ParsePattern(ref index);
		var (source, range) = mode?.SourceLocation ?? value.SourceLocation;
		range = range.Join(pattern.SourceLocation.Range);
		return new IsExpressionNode(mode, value, pattern, new(source, range));
	}
	
	public PatternNode ParsePattern(ref int index)
	{
		var start = index;
		if (!Match(ref index, out var first, TokenType.Identifier))
			throw Expected(index, "a case name");
		
		if (!IsNextNewline(index) && Match(ref index, TokenType.OpColon))
		{
			var type = ParseType(ref index);
			var (bindingSource, bindingRange) = first.SourceLocation;
			var location = new SourceLocation(bindingSource, bindingRange.Join(type.SourceLocation.Range));
			return new PatternNode([], first, [], [], false, location)
			{
				Type = type,
				TypeBinding = first
			};
		}
		
		var names = new List<Token> { first };
		while (!IsNextNewline(index) && Match(ref index, TokenType.OpDot))
		{
			if (!Match(ref index, out var name, TokenType.Identifier))
				throw Expected(index, "a case name");
			
			names.Add(name);
		}
		
		var caseName = names[^1];
		var typePath = names[..^1];
		var (source, range) = first.SourceLocation;
		range = range.Join(caseName.SourceLocation.Range);
		if (!IsNextNewline(index) && Peek(index, TokenType.OpOpenBracket))
		{
			index = start;
			var type = ParseType(ref index);
			return new PatternNode(typePath, caseName, [], [], false, type.SourceLocation) { Type = type };
		}
		
		if (IsNextNewline(index) || !Match(ref index, out var openParen, TokenType.OpOpenParen))
			return new PatternNode(typePath, caseName, [], [], false, new(source, range));
		
		var bindings = new List<Token>();
		var bindingModes = new List<Token?>();
		Token closeParen;
		while (!Match(ref index, out closeParen, TokenType.OpCloseParen))
		{
			if (AtEnd(index))
				throw Expected(index, "')'", openParen);
			
			if (bindings.Count > 0 && !Match(ref index, TokenType.OpComma))
				throw Expected(index, "',' or ')'", openParen);
			
			Token? mode = Match(ref index, out var keyword, _bindingModes) ? keyword : null;
			if (!Match(ref index, out var binding, TokenType.Identifier))
				throw Expected(index, "a name or '_'");
			
			bindings.Add(binding);
			bindingModes.Add(mode);
		}
		
		range = range.Join(closeParen.SourceLocation.Range);
		return new PatternNode(typePath, caseName, bindings, bindingModes, true, new(source, range));
	}
	
	private IExpressionNode ParseComparison(ref int index)
	{
		var operands = new List<IExpressionNode> { ParseBitwiseOr(ref index) };
		var ops = new List<Token>();
		
		while (!IsNextNewline(index) && Match(ref index, out var op, _comparisonOps))
		{
			ops.Add(op);
			operands.Add(ParseBitwiseOr(ref index));
		}
		
		return operands.Count switch
		{
			1 => operands[0],
			2 => new BinaryOpExpressionNode(operands[0], ops[0], operands[1]),
			_ => new ChainedExpressionNode(operands, ops)
		};
	}
	
	private IExpressionNode ParseBitwiseOr(ref int index)
	{
		var node = ParseBitwiseXor(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, TokenType.OpBar))
		{
			var right = ParseBitwiseXor(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseBitwiseXor(ref int index)
	{
		var node = ParseBitwiseAnd(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, TokenType.OpHat))
		{
			var right = ParseBitwiseAnd(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseBitwiseAnd(ref int index)
	{
		var node = ParseShift(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, TokenType.OpAmpersand))
		{
			var right = ParseShift(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseShift(ref int index)
	{
		var node = ParseAdditive(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, _shiftOps))
		{
			var right = ParseAdditive(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseAdditive(ref int index)
	{
		var node = ParseMultiplicative(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, _additiveOps))
		{
			var right = ParseMultiplicative(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseMultiplicative(ref int index)
	{
		var node = ParseUnary(ref index);
		while (!IsNextNewline(index) && Match(ref index, out var op, _multiplicativeOps))
		{
			var right = ParseUnary(ref index);
			node = new BinaryOpExpressionNode(node, op, right);
		}
		
		return node;
	}
	
	private IExpressionNode ParseUnary(ref int index)
	{
		if (Match(ref index, out var ownKeyword, TokenType.KeywordOwn))
			return new OwnExpressionNode(ownKeyword, ParseUnary(ref index));
		
		if (Match(ref index, out var atomicKeyword, TokenType.KeywordAtomic))
			return ParseAtomic(ref index, atomicKeyword);
		
		if (Match(ref index, out var borrowKeyword, _borrowKeywords))
			return new BorrowExpressionNode(borrowKeyword, ParseUnary(ref index));
		
		if (!Match(ref index, out var op, _unaryPrefixOps))
			return ParsePrimary(ref index);
		
		// TODO Should disallow newlines here
		var operand = ParseUnary(ref index);
		return new UnaryOpExpressionNode(op, operand);
	}
	
	private AtomicExpressionNode ParseAtomic(ref int index, Token keyword)
	{
		var (ordering, orderingLocation) = ParseOrdering(ref index);
		if (!IsNextNewline(index) && Peek(index, TokenType.OpStar))
			return new AtomicExpressionNode(keyword, ordering, orderingLocation, ParseUnary(ref index), null, null,
				null);
		
		if (!AtEnd(index) && !IsNextNewline(index))
			throw Expected(index, "'*'");
		
		return new AtomicExpressionNode(keyword, ordering, orderingLocation, null, null, null, null);
	}
	
	private (AtomicOrdering Ordering, SourceLocation? Location) ParseOrdering(ref int index)
	{
		if (AtEnd(index) || IsNextNewline(index) || Tokens[index] is not { Type: TokenType.Identifier } first)
			return (AtomicOrdering.SequentiallyConsistent, null);
		
		switch (first.Text)
		{
			case "relaxed":
				index++;
				return (AtomicOrdering.Relaxed, first.SourceLocation);
			
			case "release":
				index++;
				return (AtomicOrdering.Release, first.SourceLocation);
			
			case "acquire":
				index++;
				if (AtEnd(index) || IsNextNewline(index) ||
				    Tokens[index] is not { Type: TokenType.Identifier, Text: "release" } second)
					return (AtomicOrdering.Acquire, first.SourceLocation);
				
				index++;
				return (AtomicOrdering.AcquireRelease, first.SourceLocation with
				{
					Range = first.SourceLocation.Range.Join(second.SourceLocation.Range)
				});
			
			default:
				return (AtomicOrdering.SequentiallyConsistent, null);
		}
	}
	
	private IExpressionNode ParsePrimary(ref int index)
	{
		IExpressionNode node;
		
		if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			// Parenthesized Expression
			node = ParseExpression(ref index);
			
			if (!Match(ref index, TokenType.OpCloseParen))
				throw Expected(index, "')'", openParen);
		}
		else if (Match(ref index, out var openBracket, TokenType.OpOpenBracket))
		{
			// Array Expression
			var (source, range) = openBracket.SourceLocation;
			
			if (Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			{
				range = range.Join(closeBracket.SourceLocation.Range);
				node = new ArrayExpressionNode([], new(source, range));
			}
			else
			{
				var values = new List<IExpressionNode> { ParseExpression(ref index) };
				
				// TODO Allow trailing comma
				while (Match(ref index, TokenType.OpComma))
					values.Add(ParseExpression(ref index));
				
				if (!Match(ref index, out closeBracket, TokenType.OpCloseBracket))
					throw Expected(index, "',' or ']'", openBracket);
				
				range = range.Join(closeBracket.SourceLocation.Range);
				node = new ArrayExpressionNode(values, new(source, range));
			}
		}
		else if (StartsFunctionType(index) || Peek(index, TokenType.KeywordDyn))
			node = new TypeExpressionNode(ParseType(ref index));
		else if (Match(ref index, out var identifier, TokenType.Identifier))
			node = new VarExpressionNode(identifier);
		else if (Match(ref index, out var self, TokenType.KeywordSelf))
			node = new VarExpressionNode(self);
		else if (Match(ref index, out var undef, TokenType.KeywordUndef))
			node = ParseUndef(ref index, undef);
		else if (Match(ref index, out var sizeOf, TokenType.KeywordSizeOf))
			node = ParseSizeOf(ref index, sizeOf);
		else if (Match(ref index, out var alignOf, TokenType.KeywordAlignOf))
			node = ParseAlignOf(ref index, alignOf);
		else if (Match(ref index, out var nameOf, TokenType.KeywordNameOf))
			node = ParseNameOf(ref index, nameOf);
		else if (Match(ref index, out var match, TokenType.KeywordMatch))
			return ParseMatch(ref index, match);
		else if (Match(ref index, out var jump, _jumpKeywords))
			return ParseJump(ref index, jump);
		else if (Match(ref index, out var interpolated, TokenType.InterpolatedStringLiteral))
			node = ParseInterpolatedString(interpolated);
		else
			node = ParseLiteral(ref index);
		
		return ParsePostfix(ref index, node);
	}
	
	private bool StartsFunctionType(int index) => Peek(index, TokenType.KeywordFun) ||
	                                              Tokens[index] is { Type: TokenType.Identifier, Text: "ext" } &&
	                                              Peek(index + 1, TokenType.KeywordFun) && !IsNextNewline(index + 1);
	
	private MatchExpressionNode ParseMatch(ref int index, Token keyword)
	{
		Token? mode = Match(ref index, out var modeKeyword, TokenType.KeywordMut) ? modeKeyword : null;
		var value = ParseExpression(ref index);
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			throw Expected(index, "'{'");
		
		var arms = new List<MatchExpressionArmNode>();
		Token closeBrace;
		while (!Match(ref index, out closeBrace, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
				throw Expected(index, "'}'", openBrace);
			
			arms.Add(ParseMatchArm(ref index));
		}
		
		var (source, range) = keyword.SourceLocation;
		range = range.Join(closeBrace.SourceLocation.Range);
		return new MatchExpressionNode(keyword, mode, value, arms, new(source, range));
	}
	
	private MatchExpressionArmNode ParseMatchArm(ref int index)
	{
		PatternNode? pattern = null;
		SourceLocation location;
		if (Match(ref index, out var elseToken, TokenType.KeywordElse))
			location = elseToken.SourceLocation;
		else
		{
			pattern = ParsePattern(ref index);
			location = pattern.SourceLocation;
		}
		
		if (!Match(ref index, TokenType.OpFatArrow))
			throw Expected(index, "'=>'");
		
		return new MatchExpressionArmNode(pattern, location, ParseExpression(ref index));
	}
	
	private IExpressionNode ParseJump(ref int index, Token keyword)
	{
		var operandIndex = index;
		var operand = IsNextNewline(index) || !CanStartExpression(index) ? null : ParseExpression(ref operandIndex);
		if (operand is not null && Peek(operandIndex, TokenType.OpFatArrow))
			operand = null;
		else
			index = operandIndex;
		
		var (source, range) = keyword.SourceLocation;
		if (operand is not null)
			range = range.Join(operand.SourceLocation.Range);
		
		var location = new SourceLocation(source, range);
		return keyword.Type switch
		{
			TokenType.KeywordRet => new ReturnExpressionNode(location, operand),
			TokenType.KeywordBreak => new BreakExpressionNode(location, operand),
			_ => new ContinueExpressionNode(location, operand)
		};
	}
	
	private bool CanStartExpression(int index)
	{
		if (AtEnd(index))
			return false;
		
		var type = Tokens[index].Type;
		return _expressionStarts.Contains(type) || _literalTypes.Contains(type) ||
		       _unaryPrefixOps.Contains(type);
	}
	
	private UndefExpressionNode ParseUndef(ref int index, Token token)
	{
		if (!Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return new UndefExpressionNode(token, null, token.SourceLocation);
		
		var type = ParseType(ref index);
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw Expected(index, "']'", openBracket);
		
		var (source, range) = token.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		return new UndefExpressionNode(token, type, new(source, range));
	}
	
	private SizeOfExpressionNode ParseSizeOf(ref int index, Token token)
	{
		var target = ParseLayoutTarget(ref index, token, out var location);
		return new SizeOfExpressionNode(token, target, location);
	}
	
	private AlignOfExpressionNode ParseAlignOf(ref int index, Token token)
	{
		var target = ParseLayoutTarget(ref index, token, out var location);
		return new AlignOfExpressionNode(token, target, location);
	}
	
	private IExpressionNode ParseLayoutTarget(ref int index, Token token, out SourceLocation location)
	{
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			throw Expected(index, "'('");
		
		var target = ParseExpression(ref index);
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseParen))
			throw Expected(index, "')'", openParen);
		
		var (source, range) = token.SourceLocation;
		location = new(source, range.Join(closeBracket.SourceLocation.Range));
		return target;
	}
	
	private static InterpolatedStringExpressionNode ParseInterpolatedString(Token token)
	{
		var interpolation = token.Interpolation!;
		var values = interpolation.Holes.Select(static h =>
			new ExpressionParser([..h.Tokens, new Token(TokenType.EndOfFile, h.Close)]).ParseHole());
		
		return new InterpolatedStringExpressionNode(interpolation.Segments, values,
			interpolation.Holes.Select(static h => h.Spec), interpolation.Holes.Select(static h => h.SpecLocation),
			token.SourceLocation);
	}
	
	private IExpressionNode ParseHole()
	{
		var index = 0;
		if (AtEnd(index))
			throw Expected(index, "an expression");
		
		IExpressionNode value;
		try
		{
			value = ParseExpression(ref index);
		}
		catch (InvalidOperationException e) when (e is not ParseException)
		{
			var token = Tokens[Math.Min(index, Tokens.Length - 1)];
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, token.SourceLocation,
				token.Error ?? "Unexpected token"));
		}
		
		if (!AtEnd(index))
			throw Expected(index, "'}'");
		
		return value;
	}
	
	private NameOfExpressionNode ParseNameOf(ref int index, Token token)
	{
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			throw Expected(index, "'('");
		
		if (!Match(ref index, out var first, TokenType.Identifier))
			throw Expected(index, "a name");
		
		IExpressionNode name = new VarExpressionNode(first);
		while (Match(ref index, TokenType.OpDot))
		{
			if (!Match(ref index, out var member, TokenType.Identifier))
				throw Expected(index, "a name");
			
			var (nameSource, nameRange) = name.SourceLocation;
			name = new AccessExpressionNode(name, member, new(nameSource, nameRange.Join(member.SourceLocation.Range)));
		}
		
		if (!Match(ref index, out var closeParen, TokenType.OpCloseParen))
			throw Expected(index, "')'", openParen);
		
		var (source, range) = token.SourceLocation;
		range = range.Join(closeParen.SourceLocation.Range);
		return new NameOfExpressionNode(token, name, new(source, range));
	}
	
	private IExpressionNode ParsePostfix(ref int index, IExpressionNode target)
	{
		while (true)
		{
			// Call, Indexer, Access
			// TODO Contextual keywords
			// TODO Should not use identifier but instead a tightly-bound indexer, which is also used for type parameters
			// TODO An indexer or call expression could be chained with one another; this won't parse those
			var startIndex = index;
			
			// Access Expression
			if (Match(ref index, TokenType.OpDot))
			{
				if (!Match(ref index, out var member, TokenType.Identifier) &&
				    !MatchOperatorName(ref index, out member))
				{
					// TODO Diagnostic
					index = startIndex;
					break;
				}
				
				var (source, range) = target.SourceLocation;
				range = range.Join(member.SourceLocation.Range);
				target = new AccessExpressionNode(target, member, new(source, range));
				continue;
			}
			
			// Call Expression
			if (!IsNextNewline(index) && Match(ref index, out var openParen, TokenType.OpOpenParen))
			{
				target = ParseCallExpression(ref index, target, openParen);
				continue;
			}
			
			// Indexer Expression
			if (!IsNextNewline(index) && Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			{
				target = ParseIndexerExpression(ref index, target, openBracket);
				continue;
			}
			
			break;
		}
		
		return target;
	}
	
	private bool MatchOperatorName(ref int index, out Token name)
	{
		name = default;
		return Peek(index + 1, TokenType.OpOpenParen) && !IsNextNewline(index + 1) &&
		       Match(ref index, out name, _binaryOperatorNames);
	}
	
	private CallExpressionNode ParseCallExpression(ref int index, IExpressionNode target, Token openParen)
	{
		// Caller already consumed open parenthesis
		var range = target.SourceLocation.Range;
		var arguments = new List<IExpressionNode>();
		
		Token closeParen;
		while (!Match(ref index, out closeParen, TokenType.OpCloseParen))
		{
			if (AtEnd(index))
				throw Expected(index, "')'", openParen);
			
			if (arguments.Count > 0 && !Match(ref index, TokenType.OpComma))
				throw Expected(index, "',' or ')'", openParen);
			
			arguments.Add(ParseArgument(ref index));
		}
		
		range = range.Join(closeParen.SourceLocation.Range);
		
		return new(target, arguments, target.SourceLocation with { Range = range });
	}
	
	private IExpressionNode ParseArgument(ref int index) => Match(ref index, out var keyword, TokenType.KeywordMut)
		? new BorrowExpressionNode(keyword, ParseExpression(ref index))
		: ParseExpression(ref index);
	
	private IndexerExpressionNode ParseIndexerExpression(ref int index, IExpressionNode target, Token openBracket)
	{
		// Caller already consumed open bracket
		var range = target.SourceLocation.Range;
		var arguments = new List<IExpressionNode>();
		
		Token closeBracket;
		while (!Match(ref index, out closeBracket, TokenType.OpCloseBracket))
		{
			if (AtEnd(index))
				throw Expected(index, "']'", openBracket);
			
			if (arguments.Count > 0 && !Match(ref index, TokenType.OpComma))
				throw Expected(index, "',' or ']'", openBracket);
			
			arguments.Add(ParseExpression(ref index));
		}
		
		range = range.Join(closeBracket.SourceLocation.Range);
		
		return new(target, arguments, target.SourceLocation with { Range = range });
	}
	
	private ParseException Expected(int index, string expected, Token? opener = null)
	{
		var token = Tokens[Math.Min(index, Tokens.Length - 1)];
		if (token.Error is { } error)
			return new(new Diagnostic(DiagnosticSeverity.Error, token.SourceLocation, error));
		
		return new(new Diagnostic(DiagnosticSeverity.Error, token.SourceLocation, $"Expected {expected}")
		{
			Hints = opener is { } o ? [$"To match the '{o.Text}' on line {o.Line}"] : []
		});
	}
	
	private LiteralExpressionNode ParseLiteral(ref int index)
	{
		if (Match(ref index, out var literal, _literalTypes))
			return new(literal);
		
		// TODO Diagnostics
		// TEMP Should emit an erroneous node instead of throwing
		throw new InvalidOperationException();
	}
}