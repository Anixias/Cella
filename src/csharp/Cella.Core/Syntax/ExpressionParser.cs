using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class ExpressionParser(ImmutableArray<Token> tokens) : BaseParser<IExpressionNode>(tokens)
{
	public delegate BlockStatementNode? BlockParser(ref int index, Token openBrace);
	
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
		TokenType.OpPlusPercent,
		TokenType.OpMinusPercent,
		TokenType.OpStarPercent,
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
		TokenType.OpMinus,
		TokenType.OpPlusPercent,
		TokenType.OpMinusPercent
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
		TokenType.OpStarPercent,
	];
	
	private static readonly HashSet<TokenType> _assignmentOps =
	[
		TokenType.OpPlusEqual,
		TokenType.OpMinusEqual,
		TokenType.OpStarEqual,
		TokenType.OpSlashEqual,
		TokenType.OpPercentEqual,
		TokenType.OpPlusPercentEqual,
		TokenType.OpMinusPercentEqual,
		TokenType.OpStarPercentEqual,
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
	
	private static readonly HashSet<TokenType> _bindingModes =
	[
		TokenType.KeywordVal,
		TokenType.KeywordMut,
		TokenType.KeywordOwn,
		TokenType.KeywordVar
	];
	
	private static readonly HashSet<TokenType> _relationalPatternOps =
	[
		TokenType.OpGreaterEqual,
		TokenType.OpLessEqual,
		TokenType.OpGreater,
		TokenType.OpLess,
		TokenType.OpBangEqual
	];
	
	private static readonly HashSet<TokenType> _rangeOps = [TokenType.OpDotDot, TokenType.OpDotDotEqual];
	
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
	
	public BlockParser? ParseBlock { get; init; }
	
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
		var operands = new List<IExpressionNode> { ParseIs(ref index) };
		var ops = new List<Token>();
		
		while (!IsNextNewline(index) && Match(ref index, out var op, _equalityOps))
		{
			ops.Add(op);
			operands.Add(ParseIs(ref index));
		}
		
		return operands.Count switch
		{
			1 => operands[0],
			2 => new BinaryOpExpressionNode(operands[0], ops[0], operands[1]),
			_ => new ChainedExpressionNode(operands, ops)
		};
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
	
	public delegate bool? ConditionParser(ref int index);
	
	public ConditionParser? ParseWhenCondition { get; init; }
	
	public PatternNode ParsePattern(ref int index)
	{
		var pattern = ParseUnboundPattern(ref index);
		return !IsNextNewline(index) && Match(ref index, out var name, TokenType.Identifier)
			? pattern.WithBinding(PatternNode.CreateBinding(null, name))
			: pattern;
	}
	
	private PatternNode ParseUnboundPattern(ref int index)
	{
		if (StartsBorrowType(index))
		{
			var borrow = ParseType(ref index);
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, borrow.SourceLocation,
				"Cannot test for borrow types"));
		}
		
		if (Match(ref index, out var relation, _relationalPatternOps))
			return PatternNode.CreateRelational(relation, ParseBitwiseOr(ref index));
		
		if (Peek(index, TokenType.KeywordDyn))
		{
			var dynToken = Tokens[index];
			var dyn = ParseType(ref index);
			return PatternNode.CreateType([], dynToken, dyn, dyn.SourceLocation);
		}
		
		var start = index;
		var named = TryParse(start, ParseNamedPattern, out var namedEnd);
		var value = TryParse(start, ParseBitwiseOr, out var valueEnd);
		if (named is not null && (value is null || namedEnd >= valueEnd))
		{
			index = namedEnd;
			if (named.Kind != PatternKind.Case || value is null || namedEnd != valueEnd)
				return named;
			
			if (IsNextNewline(index) || !Match(ref index, out var namedRange, _rangeOps))
				return named.WithValue(value);
			
			return PatternNode.CreateRange(value, namedRange, ParseBitwiseOr(ref index));
		}
		
		if (value is null)
		{
			index = start;
			ParseBitwiseOr(ref index);
			throw Expected(start, "a pattern");
		}
		
		index = valueEnd;
		if (IsNextNewline(index) || !Match(ref index, out var rangeOp, _rangeOps))
			return PatternNode.CreateValue(value);
		
		return PatternNode.CreateRange(value, rangeOp, ParseBitwiseOr(ref index));
	}
	
	private delegate T ParseStep<out T>(ref int index);
	
	private static T? TryParse<T>(int start, ParseStep<T> parse, out int end) where T : class
	{
		end = start;
		try
		{
			var result = parse(ref end);
			return result;
		}
		catch (ParseException)
		{
			return null;
		}
	}
	
	private PatternNode ParseNamedPattern(ref int index)
	{
		var start = index;
		if (!Match(ref index, out var first, TokenType.Identifier))
			throw Expected(index, "a pattern");
		
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
			if (!IsNextNewline(index) && Peek(index, TokenType.OpDot))
				throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, type.SourceLocation,
					"Cannot pass type arguments to cases"));
			
			return PatternNode.CreateType(typePath, caseName, type, type.SourceLocation);
		}
		
		if (IsNextNewline(index) || !Match(ref index, out var openParen, TokenType.OpOpenParen))
			return PatternNode.CreateCase(typePath, caseName, [], false, new(source, range));
		
		var elements = new List<PatternNode>();
		Token closeParen;
		while (!Match(ref index, out closeParen, TokenType.OpCloseParen))
		{
			if (AtEnd(index))
				throw Expected(index, "')'", openParen);
			
			if (elements.Count > 0)
			{
				if (!Match(ref index, out var comma, TokenType.OpComma))
					throw Expected(index, "',' or ')'", openParen);
				
				if (RejectTrailingComma(index, comma, TokenType.OpCloseParen))
					continue;
			}
			
			elements.Add(ParseElement(ref index));
		}
		
		range = range.Join(closeParen.SourceLocation.Range);
		return PatternNode.CreateCase(typePath, caseName, elements, true, new(source, range));
	}
	
	private PatternNode ParseElement(ref int index)
	{
		if (Peek(index, TokenType.Identifier) && Peek(index + 1, TokenType.OpColon) &&
		    Match(ref index, out var field, TokenType.Identifier) && Match(ref index, TokenType.OpColon))
			return ParseElementPattern(ref index).WithField(field);
		
		return ParseElementPattern(ref index);
	}
	
	private PatternNode ParseElementPattern(ref int index)
	{
		var probe = index;
		if (Match(ref probe, _bindingModes) ||
		    Match(ref probe, out var name, TokenType.Identifier) && name.Text == "_" && EndsElement(probe))
			return ParseBinding(ref index);
		
		var pattern = ParseUnboundPattern(ref index);
		return EndsElement(index) ? pattern : pattern.WithBinding(ParseBinding(ref index));
	}
	
	private bool EndsElement(int index) => Peek(index, TokenType.OpComma) || Peek(index, TokenType.OpCloseParen);
	
	private PatternNode ParseBinding(ref int index)
	{
		Token? mode = Match(ref index, out var modeToken, _bindingModes) ? modeToken : null;
		if (!Match(ref index, out var name, TokenType.Identifier))
			throw Expected(index, mode is null ? "',' or ')'" : "a name or '_'");
		
		return PatternNode.CreateBinding(mode, name);
	}
	
	private bool StartsArm(int index) =>
		TryParse(index, (ref i) => ParseArmHead(ref i).Patterns, out var end) is not null &&
		Peek(end, TokenType.OpFatArrow);
	
	public (List<PatternNode> Patterns, IExpressionNode? Guard, SourceLocation Location) ParseArmHead(ref int index)
	{
		var patterns = new List<PatternNode> { ParsePattern(ref index) };
		while (!IsNextNewline(index) && MatchSeparator(ref index, TokenType.OpFatArrow, TokenType.KeywordIf))
			patterns.Add(ParsePattern(ref index));
		
		var (source, range) = patterns[0].SourceLocation;
		range = range.Join(patterns[^1].SourceLocation.Range);
		var guard = !IsNextNewline(index) && Match(ref index, TokenType.KeywordIf) ? ParseExpression(ref index) : null;
		return (patterns, guard, new(source, range));
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
	
	private bool StartsBorrowType(int index) =>
		(Peek(index, TokenType.KeywordImm) || Peek(index, TokenType.KeywordMut)) &&
		Peek(index + 1, TokenType.OpOpenBracket);
	
	private IExpressionNode ParseUnary(ref int index)
	{
		if (Match(ref index, out var ownKeyword, TokenType.KeywordOwn))
		{
			if (Peek(index, TokenType.KeywordFun) && Peek(index + 1, TokenType.OpOpenBracket))
				throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, ownKeyword.SourceLocation,
					"Cannot name 'own fun' types"));
			
			if (Match(ref index, out var ownedBorrow, TokenSets.BorrowKeywords))
				throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, ownedBorrow.SourceLocation,
					$"Cannot combine 'own' and '{ownedBorrow.Text}'"));
			
			return !Peek(index + 1, TokenType.OpOpenBracket) && Match(ref index, out var lambda, TokenType.KeywordFun)
				? ParseLambda(ref index, lambda, ownKeyword)
				: new OwnExpressionNode(ownKeyword, ParseOperand(ref index));
		}
		
		if (Match(ref index, out var atomicKeyword, TokenType.KeywordAtomic))
			return ParseAtomic(ref index, atomicKeyword);
		
		if (!StartsBorrowType(index) && Match(ref index, out var borrowKeyword, TokenSets.BorrowKeywords))
			return new BorrowExpressionNode(borrowKeyword, ParseOperand(ref index));
		
		if (!Match(ref index, out var op, _unaryPrefixOps))
			return ParsePrimary(ref index);
		
		var operand = ParseOperand(ref index);
		return new UnaryOpExpressionNode(op, operand);
	}
	
	private IExpressionNode ParseOperand(ref int index) =>
		AtEnd(index) || IsNextNewline(index) ? throw Expected(index - 1, "an expression") : ParseUnary(ref index);
	
	private AtomicExpressionNode ParseAtomic(ref int index, Token keyword)
	{
		var (ordering, orderingLocation) = ParseOrdering(ref index);
		if (!IsNextNewline(index) && Peek(index, TokenType.OpStar))
			return new AtomicExpressionNode(keyword, ordering, orderingLocation, ParseUnary(ref index), null, null,
				null);
		
		if (!AtEnd(index) && !IsNextNewline(index) && !Peek(index, TokenType.OpCloseBrace))
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
				
				while (MatchSeparator(ref index, TokenType.OpCloseBracket))
					values.Add(ParseExpression(ref index));
				
				if (!Match(ref index, out closeBracket, TokenType.OpCloseBracket))
					throw Expected(index, "',' or ']'", openBracket);
				
				range = range.Join(closeBracket.SourceLocation.Range);
				node = new ArrayExpressionNode(values, new(source, range));
			}
		}
		else if (!Peek(index + 1, TokenType.OpOpenBracket) && Match(ref index, out var lambda, TokenType.KeywordFun))
			return ParseLambda(ref index, lambda);
		else if (StartsFunctionType(index) || StartsBorrowType(index) || Peek(index, TokenType.KeywordDyn))
			node = new TypeExpressionNode(ParseType(ref index));
		else if (Match(ref index, out var identifier, TokenType.Identifier))
			node = new VarExpressionNode(identifier);
		else if (Peek(index + 1, TokenType.OpDot) && Match(ref index, out var root, TokenType.KeywordMod))
			node = new VarExpressionNode(root);
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
	
	private LambdaExpressionNode ParseLambda(ref int index, Token keyword, Token? own = null)
	{
		var parameters = new List<LambdaParameterNode>();
		if (Match(ref index, out var openParen, TokenType.OpOpenParen) && !Match(ref index, TokenType.OpCloseParen))
		{
			do
				parameters.Add(ParseLambdaParameter(ref index));
			while (MatchSeparator(ref index, TokenType.OpCloseParen));
			
			if (!Match(ref index, TokenType.OpCloseParen))
				throw Expected(index, "',' or ')'", openParen);
		}
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		var (source, range) = (own ?? keyword).SourceLocation;
		if (Match(ref index, TokenType.OpEqual))
		{
			var expression = ParseExpression(ref index);
			return new(keyword, parameters, returnType, expression, null,
				new(source, range.Join(expression.SourceLocation.Range))) { Own = own };
		}
		
		if (ParseBlock is null || !Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			throw Expected(index, ParseBlock is null ? "'='" : "'=' or '{'");
		
		var block = ParseBlock(ref index, openBrace) ?? new BlockStatementNode(openBrace.SourceLocation, []);
		return new(keyword, parameters, returnType, null, block, new(source, range.Join(block.SourceLocation.Range)))
		{
			Own = own
		};
	}
	
	private LambdaParameterNode ParseLambdaParameter(ref int index)
	{
		Token? mode = Match(ref index, out var keyword, TokenSets.ParameterModes) ? keyword : null;
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			throw IsReservedWord(index)
				? new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
					DescribeReservedWord(Tokens[index])))
				: Expected(index, "a parameter name");
		
		return new(mode, identifier, Match(ref index, TokenType.OpColon) ? ParseType(ref index) : null);
	}
	
	private bool StartsFunctionType(int index) => Peek(index, TokenType.KeywordFun) ||
	                                              Peek(index, TokenType.KeywordRef) &&
	                                              Peek(index + 1, TokenType.KeywordFun) ||
	                                              Tokens[index] is { Type: TokenType.Identifier, Text: "ext" } &&
	                                              Peek(index + 1, TokenType.KeywordFun) && !IsNextNewline(index + 1);
	
	private MatchExpressionNode ParseMatch(ref int index, Token keyword)
	{
		Token? mode = Match(ref index, out var modeKeyword, TokenType.KeywordMut) ? modeKeyword : null;
		var value = ParseExpression(ref index);
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			throw Expected(index, "'{'");
		
		var arms = new List<MatchExpressionArmNode>();
		var closeBrace = ParseMatchArms(ref index, openBrace, arms);
		
		if (!IsNextNewline(index) && Peek(index, TokenType.OpDot))
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
				"Cannot use '.' on 'match' expressions"));
		
		var (source, range) = keyword.SourceLocation;
		range = range.Join(closeBrace.SourceLocation.Range);
		return new MatchExpressionNode(keyword, mode, value, arms, new(source, range));
	}
	
	private Token ParseMatchArms(ref int index, Token openBrace, List<MatchExpressionArmNode> arms)
	{
		Token closeBrace;
		while (!Match(ref index, out closeBrace, TokenType.OpCloseBrace))
		{
			if (AtEnd(index) || Tokens[index].Type is TokenType.KeywordRet or TokenType.KeywordVar or
				    TokenType.KeywordVal or TokenType.KeywordIf or TokenType.KeywordFor or TokenType.KeywordLoop or
				    TokenType.KeywordWhile or TokenType.KeywordDo)
				throw Expected(index, "'}'", openBrace);
			
			if (Match(ref index, out var when, TokenType.KeywordWhen))
				ParseWhenArms(ref index, when, arms);
			else
				arms.Add(ParseMatchArm(ref index));
		}
		
		return closeBrace;
	}
	
	private void ParseWhenArms(ref int index, Token when, List<MatchExpressionArmNode> arms)
	{
		var taken = false;
		while (true)
		{
			if (ParseWhenCondition is null)
				throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, when.SourceLocation,
					"Cannot use 'when' here"));
			
			if (ParseWhenCondition(ref index) is not { } condition)
				throw Expected(index, "a condition");
			
			ParseWhenBranch(ref index, condition && !taken ? arms : []);
			taken |= condition;
			if (!Peek(index, TokenType.KeywordElse) ||
			    !Peek(index + 1, TokenType.KeywordWhen) && !Peek(index + 1, TokenType.OpOpenBrace))
				return;
			
			Match(ref index, TokenType.KeywordElse);
			if (Match(ref index, TokenType.KeywordWhen))
				continue;
			
			ParseWhenBranch(ref index, taken ? [] : arms);
			return;
		}
	}
	
	private void ParseWhenBranch(ref int index, List<MatchExpressionArmNode> arms)
	{
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			throw Expected(index, "'{'");
		
		ParseMatchArms(ref index, openBrace, arms);
	}
	
	private MatchExpressionArmNode ParseMatchArm(ref int index)
	{
		if (Match(ref index, out var elseToken, TokenType.KeywordElse))
		{
			if (!Match(ref index, TokenType.OpFatArrow))
				throw Expected(index, "'=>'");
			
			return new MatchExpressionArmNode([], null, elseToken.SourceLocation, ParseExpression(ref index));
		}
		
		var (patterns, guard, location) = ParseArmHead(ref index);
		if (!Match(ref index, TokenType.OpFatArrow))
			throw Expected(index, "'=>'");
		
		return new MatchExpressionArmNode(patterns, guard, location, ParseExpression(ref index));
	}
	
	private IExpressionNode ParseJump(ref int index, Token keyword)
	{
		var operandIndex = index;
		var operand = IsNextNewline(index) || !CanStartExpression(index)
			? null
			: TryParse(index, ParseExpression, out operandIndex);
		
		if (operand is null
			    ? !IsNextNewline(index) && CanStartExpression(index) && !StartsArm(index)
			    : !Peek(operandIndex, TokenType.OpFatArrow))
		{
			operandIndex = index;
			operand = ParseExpression(ref operandIndex);
			index = operandIndex;
		}
		else
		{
			operand = null;
		}
		
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
	
	public static bool ContinuesExpression(TokenType type) => !_unaryPrefixOps.Contains(type) &&
	                                                          (_binaryOperatorNames.Contains(type) ||
	                                                           _assignmentOps.Contains(type) ||
	                                                           _rangeOps.Contains(type) ||
	                                                           type is TokenType.OpAmpersandAmpersand
		                                                           or TokenType.OpBarBar or TokenType.KeywordIs);
	
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
			return new UndefExpressionNode(null, token.SourceLocation);
		
		var type = ParseType(ref index);
		
		if (!Match(ref index, out var closeBracket, TokenType.OpCloseBracket))
			throw Expected(index, "']'", openBracket);
		
		var (source, range) = token.SourceLocation;
		range = range.Join(closeBracket.SourceLocation.Range);
		return new UndefExpressionNode(type, new(source, range));
	}
	
	private SizeOfExpressionNode ParseSizeOf(ref int index, Token token)
	{
		var target = ParseLayoutTarget(ref index, token, out var location);
		return new SizeOfExpressionNode(target, location);
	}
	
	private AlignOfExpressionNode ParseAlignOf(ref int index, Token token)
	{
		var target = ParseLayoutTarget(ref index, token, out var location);
		return new AlignOfExpressionNode(target, location);
	}
	
	private IExpressionNode ParseLayoutTarget(ref int index, Token token, out SourceLocation location)
	{
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			throw Expected(index, "'('");
		
		if (Peek(index, TokenType.OpCloseParen))
			throw Expected(index, "a type or a value");
		
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
			new ExpressionParser([..h.Tokens, new Token(TokenType.EndOfFile, h.Close)]).ParseHole(h.Open));
		
		return new InterpolatedStringExpressionNode(interpolation.Segments, values,
			interpolation.Holes.Select(static h => h.Spec), interpolation.Holes.Select(static h => h.SpecLocation),
			token.SourceLocation);
	}
	
	private IExpressionNode ParseHole(Token open)
	{
		var index = 0;
		if (AtEnd(index))
			throw Expected(index, "an expression");
		
		if (Tokens[index].Error is null && !CanStartExpression(index))
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, open.SourceLocation,
				"Expected an expression after '{'"));
		
		var value = ParseExpression(ref index);
		if (!AtEnd(index))
			throw Expected(index, "'}'", open);
		
		return value;
	}
	
	private NameOfExpressionNode ParseNameOf(ref int index, Token token)
	{
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			throw Expected(index, "'('");
		
		if (!Match(ref index, out var first, TokenType.Identifier) &&
		    !Match(ref index, out first, TokenType.KeywordSelf))
			throw Expected(index, "a name");
		
		IExpressionNode name = new VarExpressionNode(first);
		while (Match(ref index, TokenType.OpDot))
		{
			if (!Match(ref index, out var member, TokenType.Identifier))
				throw Expected(index, "a name");
			
			var (nameSource, nameRange) = name.SourceLocation;
			name = new AccessExpressionNode(name, member, new(nameSource, nameRange.Join(member.SourceLocation.Range)));
		}
		
		if (Peek(index, TokenType.OpOpenBracket))
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
				"Cannot use type arguments in 'nameOf'"));
		
		if (!Match(ref index, out var closeParen, TokenType.OpCloseParen))
			throw Expected(index, "')'", openParen);
		
		var (source, range) = token.SourceLocation;
		range = range.Join(closeParen.SourceLocation.Range);
		return new NameOfExpressionNode(name, new(source, range));
	}
	
	private IExpressionNode ParsePostfix(ref int index, IExpressionNode target)
	{
		while (true)
		{
			// Call, Indexer, Access
			// TODO Contextual keywords
			// TODO Should not use identifier but instead a tightly-bound indexer, which is also used for type parameters
			var startIndex = index;
			
			// Access Expression
			if (Match(ref index, TokenType.OpDot))
			{
				if (!Match(ref index, out var member, TokenType.Identifier) &&
				    !MatchOperatorName(ref index, out member))
				{
					if (IsNextNewline(startIndex))
					{
						index = startIndex;
						break;
					}
					
					throw IsReservedWord(index) && !IsNextNewline(index)
						? new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
							DescribeReservedWord(Tokens[index])))
						: Expected(IsNextNewline(index) ? index - 1 : index, "a name after '.'");
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
			
			if (arguments.Count > 0)
			{
				if (!Match(ref index, out var comma, TokenType.OpComma))
					throw Expected(index, "',' or ')'", openParen);
				
				if (RejectTrailingComma(index, comma, TokenType.OpCloseParen))
					continue;
			}
			
			arguments.Add(ParseArgument(ref index));
		}
		
		range = range.Join(closeParen.SourceLocation.Range);
		
		return new(target, arguments, target.SourceLocation with { Range = range });
	}
	
	private IExpressionNode ParseArgument(ref int index)
	{
		if (!StartsBorrowType(index) && Match(ref index, out var keyword, TokenType.KeywordMut))
			return new BorrowExpressionNode(keyword, ParseExpression(ref index));
		
		return Match(ref index, out var ellipsis, TokenType.OpDotDotDot)
			? new SpreadExpressionNode(ellipsis, ParseExpression(ref index))
			: ParseExpression(ref index);
	}
	
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
			
			if (arguments.Count > 0)
			{
				if (!Match(ref index, out var comma, TokenType.OpComma))
					throw Expected(index, "',' or ']'", openBracket);
				
				if (RejectTrailingComma(index, comma, TokenType.OpCloseBracket))
					continue;
			}
			
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
		
		if (Peek(index, TokenType.KeywordDrop))
			throw new ParseException(new Diagnostic(DiagnosticSeverity.Error, Tokens[index].SourceLocation,
				"Cannot use 'drop' as a value"));
		
		throw Expected(index, "an expression");
	}
}