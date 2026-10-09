using System.Collections.Immutable;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed class FileParser
(
	ImmutableArray<Token> tokens,
	string fileName,
	string fullPath,
	IReadOnlyDictionary<string, bool> flags
) : BaseParser<FileNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _topLevelContextualKeywords =
		BuildContextualKeywords(TokenType.KeywordMod, TokenType.KeywordUse, TokenType.KeywordPub, TokenType.KeywordPvt,
			TokenType.KeywordExt, TokenType.KeywordRec, TokenType.KeywordEnum, TokenType.KeywordRef,
			TokenType.KeywordTrait, TokenType.KeywordImpl);
	
	private static readonly Dictionary<string, TokenType> _memberContextualKeywords =
		BuildContextualKeywords(TokenType.KeywordPub, TokenType.KeywordPvt, TokenType.KeywordMod, TokenType.KeywordSet,
			TokenType.KeywordNew, TokenType.KeywordDrop, TokenType.KeywordOp, TokenType.KeywordReq,
			TokenType.KeywordGet, TokenType.KeywordProp);
	
	private static readonly HashSet<TokenType> _constraintTypes =
	[
		TokenType.KeywordNoref, TokenType.KeywordCopy, TokenType.KeywordDrop, TokenType.KeywordTrait
	];
	
	private static readonly Dictionary<string, TokenType> _constraintKeywords =
		BuildContextualKeywords(_constraintTypes.Append(TokenType.KeywordNew));
	
	private static readonly HashSet<TokenType> _operatorNames =
	[
		TokenType.OpEqualEqual, TokenType.OpBangEqual, TokenType.OpLess, TokenType.OpLessEqual, TokenType.OpGreater,
		TokenType.OpGreaterEqual, TokenType.OpPlus, TokenType.OpMinus, TokenType.OpStar, TokenType.OpSlash,
		TokenType.OpPercent, TokenType.OpLessLess, TokenType.OpGreaterGreater, TokenType.OpLessLessLess,
		TokenType.OpGreaterGreaterGreater, TokenType.OpAmpersand, TokenType.OpBar, TokenType.OpHat, TokenType.OpTilde,
		TokenType.OpPlusEqual, TokenType.OpMinusEqual, TokenType.OpStarEqual, TokenType.OpSlashEqual,
		TokenType.OpPercentEqual, TokenType.OpLessLessEqual, TokenType.OpGreaterGreaterEqual,
		TokenType.OpLessLessLessEqual, TokenType.OpGreaterGreaterGreaterEqual, TokenType.OpAmpersandEqual,
		TokenType.OpBarEqual, TokenType.OpHatEqual
	];
	
	private static readonly HashSet<TokenType> _undeclarableOperators =
		[TokenType.OpBang, TokenType.OpAmpersandAmpersand, TokenType.OpBarBar, TokenType.OpDot, TokenType.OpEqual];
	
	private static readonly HashSet<TokenType> _topLevelSyncTypes = [TokenType.OpSemicolon, TokenType.EndOfFile];
	
	private static readonly HashSet<TokenType> _topLevelKeywords =
		[TokenType.KeywordMod, TokenType.KeywordUse, TokenType.KeywordExt, TokenType.KeywordPvt];
	
	private static readonly HashSet<TokenType> _visibilityKeywords =
		[TokenType.KeywordPub, TokenType.KeywordPvt, TokenType.KeywordMod];
	
	private static readonly HashSet<TokenType> _blockKeywords = [TokenType.KeywordPvt, TokenType.KeywordMod];
	
	private static readonly HashSet<TokenType> _bindingKeywords = [TokenType.KeywordVal, TokenType.KeywordVar];
	
	private static readonly HashSet<TokenType> _accessorKeywords = [TokenType.KeywordGet, TokenType.KeywordSet];
	
	private static readonly HashSet<TokenType> _parameterModes = [TokenType.KeywordMut, TokenType.KeywordOwn];
	private static readonly HashSet<TokenType> _lifecycleKeywords = [TokenType.KeywordNew, TokenType.KeywordDrop];
	
	private static Dictionary<string, TokenType> BuildContextualKeywords(params IEnumerable<TokenType> tokenTypes) =>
		tokenTypes.ToDictionary(static t => t.Representation);
	
	public DiagnosticList Diagnostics { get; } = new();
	
	private bool _allowsMissingBodies;
	
	private void Report(SourceLocation location, string message,
		DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
		Diagnostics.Add(new(severity, location, message));
	
	private void Report(Token token, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
		Diagnostics.Add(new(severity, token.SourceLocation, message));
	
	private void ReportUnexpected(Token token) => Report(token, token.Error ?? "Unexpected token");
	
	private void ReportExpected(int index, string expected, Token? opener = null)
	{
		var token = Tokens[Math.Min(index, Tokens.Length - 1)];
		Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation, token.Error ?? $"Expected {expected}")
		{
			Hints = token.Error is null && opener is { } o ? [$"To match the '{o.Text}' on line {o.Line}"] : []
		});
	}
	
	private void Report(string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
		Diagnostics.Add(new(severity, new(Tokens[0].SourceLocation.Source, TextRange.Empty), message));
	
	public override FileNode? Parse(ref int index)
	{
		if (Tokens.Length == 0)
		{
			Report("Cannot parse empty file");
			return null;
		}
		
		// Setup
		var (source, range) = Tokens[0].SourceLocation;
		
		ModuleName moduleName = default;
		var declarations = new List<IDeclarationNode>();
		var imports = new List<ImportExpression>();
		
		var moduleNameAllowed = true;
		var importsAllowed = false;
		var conditionAllowed = false;
		var isIncluded = true;
		
		while (!AtEnd(index))
		{
			// Parse module name
			if (!IsVisibilityBlock(index, _topLevelContextualKeywords) &&
			    Match(ref index, _topLevelContextualKeywords, TokenType.KeywordMod))
			{
				try
				{
					moduleName = ParseModuleName(ref index);
				}
				catch (Exception e)
				{
					Report(moduleName.SourceLocation, $"Failed to parse module name: {e.Message}");
				}
				
				if (moduleNameAllowed)
				{
					moduleNameAllowed = false;
					importsAllowed = true;
					conditionAllowed = true;
				}
				else
				{
					Report(moduleName.SourceLocation, "Module name must precede all other declarations or imports");
					continue;
				}
			}
			
			// Parse imports
			if (Match(ref index, out var useToken, _topLevelContextualKeywords, TokenType.KeywordUse))
			{
				if (Match(ref index, TokenType.KeywordWhen))
				{
					if (!conditionAllowed)
						Report(useToken, "'use when' must directly follow the module name");
					
					if (ParseCondition(ref index) is { } condition)
						isIncluded &= condition;
					else
						ResyncTopLevel(ref index);
					
					conditionAllowed = false;
					continue;
				}
				
				conditionAllowed = false;
				if (ParseImportExpression(ref index) is { } import)
					imports.Add(import);
				else
					ResyncTopLevel(ref index);
				
				if (!importsAllowed)
					Report(useToken, "Imports must precede all other declarations");
				
				continue;
			}
			
			if (ParseTopLevelDeclaration(ref index, declarations, null))
			{
				importsAllowed = false;
				conditionAllowed = false;
				continue;
			}
			
			// Unexpected token, cannot resync
			ReportUnexpected(Tokens[index]);
			return null;
		}
		
		if (moduleName == default)
		{
			Report("All files must begin with a module name (mod a.b.c)");
			return null;
		}
		
		// Combine range to include penultimate token (because last must be EOF)
		if (index > 1)
			range.Join(Tokens[index - 1].SourceLocation.Range);
		
		// Must read EOF at end
		if (!Match(ref index, TokenType.EndOfFile))
		{
			Report(Tokens[index], "Expected end of file");
			return null;
		}
		
		if (Diagnostics.ErrorCount > 0)
			return null;
		
		return new FileNode(new(source, range))
		{
			FileName = fileName,
			FullPath = fullPath,
			ModuleName = moduleName,
			Imports = imports.ToImmutableArray(),
			Declarations = declarations.ToImmutableArray(),
			IsExcluded = !isIncluded
		};
	}
	
	private bool ParseTopLevelDeclaration(ref int index, List<IDeclarationNode> declarations, Token? block)
	{
		if (MatchVisibilityBlock(ref index, _topLevelContextualKeywords, out var keyword, out var openBrace))
		{
			if (block is { } outer)
				Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
			
			ParseTopLevelBlock(ref index, openBrace, block ?? keyword, declarations);
			return true;
		}
		
		if (Match(ref index, TokenType.KeywordWhen))
		{
			if (!ParseWhen(ref index, (ref int i, Token open, bool isActive) =>
				    ParseTopLevelBlock(ref i, open, block, isActive ? declarations : [])))
				ResyncTopLevel(ref index, block is not null);
			
			return true;
		}
		
		var insideBlock = block is not null;
		
		// Parse external declarations
		var externalStart = index;
		if (Match(ref index, out var extToken, _topLevelContextualKeywords, TokenType.KeywordExt))
		{
			if (ParseDeclaration(ref index, extToken, "external",
				    (ref i) => ParseExternalDeclarations(ref i, block)) is { } externals)
				declarations.AddRange(externals);
			else
				SkipDeclaration(ref index, externalStart, insideBlock);
			
			return true;
		}
		
		// Parse top-level declarations
		var declarationStart = index;
		if (!Match(ref index, out var identifier, _topLevelContextualKeywords, TokenType.Identifier))
			return false;
		
		var target = ParseImplTarget(ref index, identifier);
		if (ParseTypeParameters(ref index) is not { } typeParameters)
		{
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		var colonIndex = index;
		if (!Consume(ref index, _topLevelSyncTypes, TokenType.OpColon))
		{
			Report(Tokens[colonIndex], "Expected ':' after identifier");
			return true;
		}
		
		var modifiers = ParseDeclarationModifiers(ref index, block);
		if (Match(ref index, out var implKeyword, _topLevelContextualKeywords, TokenType.KeywordImpl))
		{
			if (modifiers.Tokens is [var modifier, ..])
				Report(modifier, $"Cannot use '{modifier.Text}' on impl blocks");
			
			var impl = ParseDeclaration(ref index, identifier, "impl",
				(ref i) => ParseImpl(ref i, target, implKeyword, typeParameters));
			
			if (impl is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(impl);
			
			return true;
		}
		
		if (target is QualifiedTypeNode)
		{
			ReportExpected(index, "'impl'");
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordTrait))
		{
			var trait = ParseDeclaration(ref index, identifier, "trait",
				(ref i) => ParseTrait(ref i, identifier, modifiers, typeParameters));
			
			if (trait is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(trait);
			
			return true;
		}
		
		var isRef = Match(ref index, _topLevelContextualKeywords, TokenType.KeywordRef);
		
		// Record
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordRec))
		{
			var record = ParseDeclaration(ref index, identifier, "record",
				(ref i) => ParseRecord(ref i, identifier, modifiers, isRef, typeParameters));
			
			if (record is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(record);
			
			return true;
		}
		
		var kindIndex = index;
		var isExternal = !isRef && Match(ref index, _topLevelContextualKeywords, TokenType.KeywordExt);
		if (typeParameters is [var first, ..] && DescribeTypeParameterError(index, isExternal) is { } error)
			Report(first.SourceLocation, error);
		
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordEnum))
		{
			var enumTypeParameters = isExternal ? [] : typeParameters;
			var enumNode = ParseDeclaration(ref index, identifier, "enum",
				(ref i) => ParseEnum(ref i, identifier, modifiers, isExternal, isRef, enumTypeParameters));
			
			if (enumNode is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(enumNode);
			
			return true;
		}
		
		if (isRef)
		{
			Report(Tokens[index], "Expected 'rec' or 'enum' after 'ref'");
			if (!AtEnd(index))
				index++;
			
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		// Global
		if (!isExternal && Match(ref index, out var bindingKeyword, _bindingKeywords))
		{
			var global = ParseDeclaration(ref index, identifier, "global",
				(ref i) => ParseGlobal(ref i, identifier, modifiers, bindingKeyword));
			
			if (global is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(global);
			
			return true;
		}
		
		// Function
		if (Match(ref index, TokenType.KeywordFun))
		{
			var function = ParseDeclaration(ref index, identifier, "function",
				(ref i) => ParseFunction(ref i, identifier, modifiers, isExternal, isExternal ? [] : typeParameters));
			
			if (function is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(function);
			
			return true;
		}
		
		// Unknown declaration
		index = kindIndex;
		Report(Tokens[index], "Unknown declaration type");
		if (!AtEnd(index))
			index++;
		
		ResyncTopLevel(ref index, insideBlock);
		return true;
	}
	
	private List<TypeParameterNode>? ParseTypeParameters(ref int index)
	{
		var parameters = new List<TypeParameterNode>();
		if (!IsOnSameLine(index) || !Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			return parameters;
		
		do
		{
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				ReportExpected(index, "a type parameter name");
				return null;
			}
			
			var keywords = new List<Token>();
			var traits = new List<ITypeNode>();
			var constructors = new List<ConstructorConstraintNode>();
			if (Match(ref index, TokenType.OpColon))
			{
				do
				{
					if (Match(ref index, out var newKeyword, _constraintKeywords, TokenType.KeywordNew))
					{
						if (ParseConstructorConstraint(ref index, newKeyword) is not { } constructor)
							return null;
						
						constructors.Add(constructor);
					}
					else if (Match(ref index, out var keyword, _constraintKeywords, _constraintTypes) ||
					         Match(ref index, out keyword, TokenType.KeywordNull) ||
					         Match(ref index, out keyword, TokenType.KeywordAtomic))
						keywords.Add(keyword);
					else if (!Peek(index, TokenType.Identifier))
					{
						ReportExpected(index, "a constraint");
						return null;
					}
					else if (ParseConstraintType(ref index) is { } trait)
						traits.Add(trait);
					else
						return null;
				} while (Match(ref index, TokenType.OpPlus));
			}
			
			parameters.Add(new(name, keywords, traits, constructors));
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, TokenType.OpCloseBracket))
			return parameters;
		
		ReportExpected(index, "',' or ']'", openBracket);
		return null;
	}
	
	private ConstructorConstraintNode? ParseConstructorConstraint(ref int index, Token keyword)
	{
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			ReportExpected(index, "'('");
			return null;
		}
		
		var modes = new List<Token?>();
		var types = new List<ITypeNode>();
		if (Match(ref index, out var closeParen, TokenType.OpCloseParen))
			return new(keyword, modes, types, closeParen);
		
		do
		{
			var isBorrowType = Peek(index, TokenType.KeywordMut) && Peek(index + 1, TokenType.OpOpenBracket);
			modes.Add(!isBorrowType && Match(ref index, out var mode, _parameterModes) ? mode : null);
			if (!StartsType(index))
			{
				ReportExpected(index, "a type");
				return null;
			}
			
			if (ParseConstraintType(ref index) is not { } type)
				return null;
			
			types.Add(type);
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, out closeParen, TokenType.OpCloseParen))
			return new(keyword, modes, types, closeParen);
		
		ReportExpected(index, "',' or ')'", openParen);
		return null;
	}
	
	private bool StartsType(int index) => Peek(index, TokenType.Identifier) || Peek(index, TokenType.KeywordImm) ||
	                                      Peek(index, TokenType.KeywordMut) || Peek(index, TokenType.KeywordDyn) ||
	                                      Peek(index, TokenType.KeywordFun);
	
	private ITypeNode? ParseConstraintType(ref int index)
	{
		try
		{
			return ParseType(ref index);
		}
		catch (ParseException e)
		{
			Diagnostics.Add(e.Diagnostic);
		}
		catch (InvalidOperationException)
		{
			ReportUnexpected(Tokens[Math.Min(index, Tokens.Length - 1)]);
		}
		
		return null;
	}
	
	private string? DescribeTypeParameterError(int index, bool isExternal)
	{
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordEnum))
			return isExternal ? "Cannot declare type parameters on ext enums" : null;
		
		if (Match(ref index, TokenType.KeywordFun))
			return isExternal ? "Cannot declare type parameters on ext functions" : null;
		
		return Match(ref index, _bindingKeywords) ? "Cannot declare type parameters on globals" : null;
	}
	
	private bool RejectTypeParameters(ref int index, string kind)
	{
		if (ParseTypeParameters(ref index) is not { } typeParameters)
			return false;
		
		if (typeParameters is [var first, ..])
			Report(first.SourceLocation, $"Cannot declare type parameters on {kind}");
		
		return true;
	}
	
	private bool ParseTopLevelBlock(ref int index, Token openBrace, Token? block, List<IDeclarationNode> declarations)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				Report(openBrace, "Expected '}' to close this block");
				return false;
			}
			
			if (ParseTopLevelDeclaration(ref index, declarations, block))
				continue;
			
			ReportUnexpected(Tokens[index]);
			while (!AtEnd(index))
				index++;
			
			return false;
		}
		
		return true;
	}
	
	private delegate bool BranchParser(ref int index, Token openBrace, bool isActive);
	
	private bool ParseWhen(ref int index, BranchParser parseBranch)
	{
		var taken = false;
		while (true)
		{
			if (ParseCondition(ref index) is not { } condition ||
			    !ParseBranch(ref index, parseBranch, condition && !taken))
				return false;
			
			taken |= condition;
			if (!Match(ref index, TokenType.KeywordElse))
				return true;
			
			if (!Match(ref index, TokenType.KeywordWhen))
				return ParseBranch(ref index, parseBranch, !taken);
		}
	}
	
	private bool ParseBranch(ref int index, BranchParser parseBranch, bool isActive)
	{
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return parseBranch(ref index, openBrace, isActive);
		
		ReportExpected(index, "'{'");
		return false;
	}
	
	private bool? ParseCondition(ref int index)
	{
		if (ParseConjunction(ref index) is not { } value)
			return null;
		
		while (Match(ref index, TokenType.OpBarBar))
		{
			if (ParseConjunction(ref index) is not { } other)
				return null;
			
			value |= other;
		}
		
		return value;
	}
	
	private bool? ParseConjunction(ref int index)
	{
		if (ParseFlag(ref index) is not { } value)
			return null;
		
		while (Match(ref index, TokenType.OpAmpersandAmpersand))
		{
			if (ParseFlag(ref index) is not { } other)
				return null;
			
			value &= other;
		}
		
		return value;
	}
	
	private bool? ParseFlag(ref int index)
	{
		if (Match(ref index, TokenType.OpBang))
			return !ParseFlag(ref index);
		
		if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			var value = ParseCondition(ref index);
			if (value is null || Match(ref index, TokenType.OpCloseParen))
				return value;
			
			ReportExpected(index, "')'", openParen);
			return null;
		}
		
		if (!Match(ref index, out var name, TokenType.Identifier))
		{
			ReportExpected(index, "a flag");
			return null;
		}
		
		if (flags.TryGetValue(name.Text, out var flag))
			return flag;
		
		Report(name, $"Flag '{name.Text}' not found");
		return false;
	}
	
	private bool IsVisibilityBlock(int index, IReadOnlyDictionary<string, TokenType> keywords) =>
		MatchVisibilityBlock(ref index, keywords, out _, out _);
	
	private bool MatchVisibilityBlock(ref int index, IReadOnlyDictionary<string, TokenType> keywords, out Token keyword,
		out Token openBrace)
	{
		var start = index;
		if (Match(ref index, out keyword, keywords, _blockKeywords) &&
		    Match(ref index, out openBrace, TokenType.OpOpenBrace))
			return true;
		
		index = start;
		openBrace = default;
		return false;
	}
	
	private readonly record struct DeclarationModifiers
	(
		List<Token> Tokens,
		Token? Visibility,
		Token? WriteVisibility = null,
		SourceLocation WriteLocation = default
	);
	
	private DeclarationModifiers ParseDeclarationModifiers(ref int index, Token? block)
	{
		var keywords = new List<Token>();
		while (Match(ref index, out var keyword, _topLevelContextualKeywords, _visibilityKeywords))
			keywords.Add(keyword);
		
		return new(keywords, CheckVisibility(keywords, block, false));
	}
	
	private DeclarationModifiers ParseMemberModifiers(ref int index, Token? block)
	{
		var tokens = new List<Token>();
		var keywords = new List<Token>();
		var restrictions = new List<(Token Scope, Token Set)>();
		while (StartsMemberType(index + 1) &&
		       Match(ref index, out var keyword, _memberContextualKeywords, _visibilityKeywords))
		{
			tokens.Add(keyword);
			if (keyword.Type != TokenType.KeywordPub && StartsMemberType(index + 1) &&
			    Match(ref index, out var set, _memberContextualKeywords, TokenType.KeywordSet))
			{
				tokens.Add(set);
				restrictions.Add((keyword, set));
				continue;
			}
			
			if (restrictions.Count > 0)
				Report(keyword, $"Cannot use '{keyword.Text}' after '{restrictions[^1].Scope.Text} set'");
			
			keywords.Add(keyword);
		}
		
		var visibility = CheckVisibility(keywords, block, true);
		if (restrictions.Count == 0)
			return new(tokens, visibility);
		
		var (scope, setKeyword) = restrictions[0];
		var location = Span(scope, setKeyword);
		if (restrictions.Count > 1)
		{
			List<string> names = [..restrictions.Select(static r => $"{r.Scope.Text} set")];
			Report(Span(scope, restrictions[^1].Set), $"Cannot combine {DiagnosticReporter.JoinNames(names)}");
		}
		else if (keywords is [{ Type: not TokenType.KeywordPub } read] && !IsNarrower(scope, read))
			Report(Span(read, setKeyword), $"Cannot combine '{read.Text}' and '{scope.Text} set'");
		else if (keywords.Count == 0 && block is { } outer && !IsNarrower(scope, outer))
			Report(location, $"Cannot use '{scope.Text} set' in '{outer.Text}' blocks");
		
		return new(tokens, visibility, scope, location);
	}
	
	private bool StartsMemberType(int index) =>
		!AtEnd(index) && Tokens[index].Line == Tokens[index - 1].Line && Tokens[index].Type is TokenType.Identifier
			or TokenType.KeywordFun or TokenType.KeywordImm or TokenType.KeywordMut or TokenType.KeywordVal
			or TokenType.KeywordVar;
	
	private static bool IsNarrower(Token write, Token read) =>
		write.Type == TokenType.KeywordPvt && read.Type == TokenType.KeywordMod;
	
	private Token? CheckVisibility(List<Token> keywords, Token? block, bool isMember)
	{
		if (keywords.Count > 1)
			Report(Span(keywords[0], keywords[^1]),
				$"Cannot combine {DiagnosticReporter.JoinNames([..keywords.Select(static k => k.Text)])}");
		else if (isMember && keywords is [{ Type: TokenType.KeywordPub } pub])
			Report(pub, "Cannot declare 'pub' members");
		else if (keywords is [var keyword] && block is { } outer)
			Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
		
		return keywords.Count > 0 ? keywords[0] : block;
	}
	
	private void RejectWriteRestriction(DeclarationModifiers modifiers, string kinds)
	{
		if (modifiers.WriteVisibility is { } scope)
			Report(modifiers.WriteLocation, $"Cannot use '{scope.Text} set' on {kinds}");
	}
	
	private static SourceLocation Span(Token first, Token last)
	{
		var (source, range) = first.SourceLocation;
		return new(source, range.Join(last.SourceLocation.Range));
	}
	
	private delegate T? DeclarationParser<T>(ref int index) where T : class;
	
	private T? ParseDeclaration<T>(ref int index, Token start, string kind, DeclarationParser<T> parse)
		where T : class
	{
		var errorCount = Diagnostics.ErrorCount;
		T? declaration;
		
		try
		{
			declaration = parse(ref index);
		}
		catch (ParseException e)
		{
			Diagnostics.Add(e.Diagnostic);
			declaration = null;
		}
		catch (InvalidOperationException)
		{
			ReportUnexpected(Tokens[index]);
			declaration = null;
		}
		
		if (declaration is null && Diagnostics.ErrorCount == errorCount)
			Report(start, $"Invalid {kind} declaration");
		
		return declaration;
	}
	
	private List<IDeclarationNode>? ParseExternalDeclarations(ref int index, Token? block)
	{
		// 'ext' token already consumed by caller
		
		// TODO Diagnostics
		
		// Optional library origin
		string? origin;
		if (Match(ref index, TokenType.OpOpenParen))
		{
			if (!Match(ref index, out var str, TokenType.StringLiteral))
				return null;
			
			origin = str.Text;
			
			if (!Match(ref index, TokenType.OpCloseParen))
				return null;
		}
		else
			origin = null;
		
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return ParseExternalDeclaration(ref index, origin, block) is { } single ? [single] : null;
		
		var nodes = new List<IDeclarationNode>();
		return ParseExternalBody(ref index, openBrace, origin, block, nodes) ? nodes : null;
	}
	
	private bool ParseExternalBody(ref int index, Token openBrace, string? origin, Token? block,
		List<IDeclarationNode> nodes)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				Report(openBrace, "Expected '}' to close this block");
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref int i, Token open, bool isActive) =>
					    ParseExternalBody(ref i, open, origin, block, isActive ? nodes : [])))
					return false;
				
				continue;
			}
			
			if (ParseExternalDeclaration(ref index, origin, block) is not { } ext)
			{
				// TODO Diagnostics
				SkipUntil(ref index, TokenType.OpCloseBrace);
				SkipIf(ref index, TokenType.OpCloseBrace);
				return false;
			}
			
			nodes.Add(ext);
		}
		
		return true;
	}
	
	private ModuleName ParseModuleName(ref int index)
	{
		var parts = new List<Token>();
		
		if (!Consume(ref index, out var firstPart, _topLevelSyncTypes, TokenType.Identifier))
			throw new Exception(); // TODO
		
		parts.Add(firstPart);
		
		while (Match(ref index, TokenType.OpDot))
		{
			if (Match(ref index, out var part, TokenType.Identifier))
				parts.Add(part);
		}
		
		return new(parts.ToImmutableArray());
	}
	
	private ImportExpression? ParseImportExpression(ref int index)
	{
		if (!IsOnSameLine(index) || !Match(ref index, out var firstPart, TokenType.Identifier))
		{
			ReportExpected(IsOnSameLine(index) ? index : index - 1, "a module name");
			return null;
		}
		
		var parts = new List<Token> { firstPart };
		IImport? import = null;
		while (import is null && Match(ref index, TokenType.OpDot))
		{
			if (!IsOnSameLine(index))
			{
				ReportExpected(index - 1, "a name, '[' or '*' after '.'");
				return null;
			}
			
			if (Match(ref index, out var part, TokenType.Identifier))
			{
				parts.Add(part);
			}
			else if (Match(ref index, out var openBracket, TokenType.OpOpenBracket))
			{
				if (ParseImportList(ref index, openBracket) is not { } list)
					return null;
				
				import = list;
			}
			else if (Match(ref index, TokenType.OpStar))
			{
				import = FullImport.Instance;
			}
			else
			{
				ReportExpected(index, "a name, '[' or '*'");
				return null;
			}
		}
		
		if (import is null)
		{
			import = new TokenImport(parts[^1]);
			parts.RemoveAt(parts.Count - 1);
		}
		
		return new(new(parts.ToImmutableArray()), import);
	}
	
	private bool IsOnSameLine(int index) => !AtEnd(index) && Tokens[index].Line == Tokens[index - 1].Line;
	
	private ListImport? ParseImportList(ref int index, Token openBracket)
	{
		var names = new List<Token>();
		while (!Match(ref index, TokenType.OpCloseBracket))
		{
			if (names.Count > 0 && !Match(ref index, TokenType.OpComma))
			{
				ReportExpected(index, "',' or ']'", openBracket);
				return null;
			}
			
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				ReportExpected(index, "a name", openBracket);
				return null;
			}
			
			names.Add(name);
		}
		
		return new ListImport(names.ToImmutableArray());
	}
	
	private FunctionNode? ParseFunction(ref int index, Token identifier, DeclarationModifiers modifiers,
		bool isExternal, List<TypeParameterNode> typeParameters)
	{
		// When this is called, the identifier and fun keyword are already consumed
		// Caller is expected to resync in case of errors
		
		// @TODO Diagnostics
		
		if (ParseFunctionSignature(ref index, false, true) is not { } signature)
			return null;
		
		var (receiver, parameters, returnType, _) = signature;
		WhenClause? when = null;
		if (IsOnSameLine(index) && Match(ref index, out var whenKeyword, TokenType.KeywordWhen))
		{
			if (ParseCondition(ref index) is not { } condition)
				return null;
			
			when = new(whenKeyword, condition);
		}
		
		if (Match(ref index, TokenType.OpEqual))
		{
			if (ParseExpressionBody(ref index, returnType is null) is not { } statement)
				return null;
			
			return new(identifier, modifiers.Tokens, receiver, parameters, returnType, statement, isExternal)
			{
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				When = when
			};
		}
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return _allowsMissingBodies
				? new(identifier, modifiers.Tokens, receiver, parameters, returnType, null, isExternal)
				{
					Visibility = modifiers.Visibility,
					TypeParameters = [..typeParameters],
					When = when
				}
				: null;
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		return new(identifier, modifiers.Tokens, receiver, parameters, returnType, body, isExternal)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			When = when
		};
	}
	
	private IStatementNode? ParseExpressionBody(ref int index, bool discardsValue)
	{
		if (discardsValue && Match(ref index, out var matchToken, TokenType.KeywordMatch))
			return ParseMatchStatement(ref index, matchToken);
		
		var expression = ParseExpression(ref index);
		return new ExpressionStatementNode(discardsValue
			? expression
			: new ReturnExpressionNode(expression.SourceLocation, expression));
	}
	
	private GlobalNode? ParseGlobal(ref int index, Token identifier, DeclarationModifiers modifiers, Token keyword)
	{
		var type = ParseType(ref index);
		if (!Match(ref index, TokenType.OpEqual))
		{
			Report(type.SourceLocation, "Expected '=' and an initial value");
			return null;
		}
		
		var initializer = ParseExpression(ref index);
		return new(identifier, modifiers.Tokens, keyword, type, initializer)
		{
			Visibility = modifiers.Visibility,
			WriteVisibility = modifiers.WriteVisibility
		};
	}
	
	private GlobalNode? ParseStaticField(ref int index, Token identifier, DeclarationModifiers modifiers, Token keyword)
	{
		if (keyword.Type == TokenType.KeywordVal)
			RejectWriteRestriction(modifiers, "'val' fields");
		
		return ParseDeclaration(ref index, identifier, "field",
			(ref i) => ParseGlobal(ref i, identifier, modifiers, keyword));
	}
	
	private ExternalFunctionNode? ParseExternalDeclaration(ref int index, string? origin, Token? block)
	{
		// TODO Diagnostics
		
		if (!Match(ref index, out var identifier, _topLevelContextualKeywords, TokenType.Identifier))
			return null;
		
		if (!RejectTypeParameters(ref index, "ext functions") || !Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseDeclarationModifiers(ref index, block);
		
		if (!Match(ref index, TokenType.KeywordFun))
			return null;
		
		if (ParseFunctionSignature(ref index, true, false) is not { } signature)
			return null;
		
		var (_, parameters, returnType, isVariadic) = signature;
		return new(identifier, modifiers.Tokens, parameters, returnType, isVariadic, origin)
		{
			Visibility = modifiers.Visibility
		};
	}
	
	private List<ParameterNode>? ParseParameters(ref int index, bool optionalParentheses) =>
		ParseParameters(ref index, optionalParentheses, false, false, out _, out _);
	
	private List<ParameterNode>? ParseParameters(ref int index, bool optionalParentheses, bool allowVariadic,
		bool allowReceiver, out bool isVariadic, out ReceiverNode? receiver)
	{
		isVariadic = false;
		receiver = null;
		var parameters = new List<ParameterNode>();
		
		// Parentheses are optional for function declarations
		if (Match(ref index, TokenType.OpOpenParen))
		{
			receiver = allowReceiver ? ParseReceiver(ref index) : null;
			if (Match(ref index, TokenType.OpCloseParen))
				return parameters;
			
			if (receiver is not null && !Match(ref index, TokenType.OpComma))
				return null;
			
			if (ParseParameterList(ref index, allowVariadic, out isVariadic) is not { } parameterList)
				return null;
			
			parameters.AddRange(parameterList);
			
			// TODO Diagnostics
			if (!Match(ref index, TokenType.OpCloseParen))
				return null;
		}
		else if (!optionalParentheses)
			return null; // TODO Diagnostics
		
		return parameters;
	}
	
	private (ReceiverNode? Receiver, List<ParameterNode> Parameters, ITypeNode? ReturnType, bool IsVariadic)?
		ParseFunctionSignature(ref int index, bool allowVariadic, bool allowReceiver)
	{
		if (ParseParameters(ref index, true, allowVariadic, allowReceiver, out var isVariadic, out var receiver) is not
		    { } parameters)
			return null;
		
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		return (receiver, parameters, returnType, isVariadic);
	}
	
	private ReceiverNode? ParseReceiver(ref int index)
	{
		var start = index;
		Token? mode = Match(ref index, out var keyword, _parameterModes) ? keyword : null;
		if (Match(ref index, out var self, TokenType.KeywordSelf))
			return new(mode, self);
		
		index = start;
		return null;
	}
	
	private List<ParameterNode>? ParseParameterList(ref int index, bool allowVariadic, out bool isVariadic)
	{
		isVariadic = false;
		var result = new List<ParameterNode>();
		
		if (ParseParameter(ref index) is not { } firstParameter)
			return null;
		
		result.Add(firstParameter);
		
		while (Match(ref index, TokenType.OpComma))
		{
			if (allowVariadic && Match(ref index, TokenType.OpDotDotDot))
			{
				isVariadic = true;
				break;
			}
			
			if (ParseParameter(ref index) is not { } parameter)
				return null;
			
			result.Add(parameter);
		}
		
		return result;
	}
	
	private ParameterNode? ParseParameter(ref int index)
	{
		// TODO Diagnostics
		
		Token? mode = Match(ref index, out var keyword, _parameterModes) ? keyword : null;
		
		// TODO Attempt resync 
		if (!Match(ref index, out var identifier, TokenType.Identifier))
			return null;
		
		// TODO Potentially make type optional, inferred from default or make auto-generic?
		if (!Match(ref index, TokenType.OpColon))
			return null;
		
		// TODO Type should be more complex than a simple identifier token
		var type = ParseType(ref index);
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(mode, identifier, type, null);
		
		var defaultValue = ParseExpression(ref index);
		return new(mode, identifier, type, defaultValue);
	}
	
	private RecordNode? ParseRecord(ref int index, Token identifier, DeclarationModifiers modifiers, bool isRef,
		List<TypeParameterNode> typeParameters)
	{
		// When this is called, the identifier and rec keyword are already consumed
		// Caller is expected to resync in case of errors
		
		if (ParseImplementedTraits(ref index) is not { } traits)
			return null;
		
		var members = new List<IDeclarationNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace) &&
		    !ParseMembers(ref index, openBrace, null, members))
			return null;
		
		return new(identifier, modifiers.Tokens, isRef, members)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			Traits = [..traits]
		};
	}
	
	private ITypeNode ParseImplTarget(ref int index, Token identifier)
	{
		if (!Peek(index, TokenType.OpDot))
			return new IdentifierTypeNode(identifier);
		
		var parts = new List<Token> { identifier };
		while (IsOnSameLine(index) && Peek(index, TokenType.OpDot) && Peek(index + 1, TokenType.Identifier))
		{
			index++;
			parts.Add(Tokens[index++]);
		}
		
		var (source, range) = identifier.SourceLocation;
		return new QualifiedTypeNode(new(source, range.Join(parts[^1].SourceLocation.Range)), parts);
	}
	
	private List<ITypeNode>? ParseImplementedTraits(ref int index)
	{
		var traits = new List<ITypeNode>();
		if (!IsOnSameLine(index) || !Match(ref index, _topLevelContextualKeywords, TokenType.KeywordImpl))
			return traits;
		
		return ParseTraitNames(ref index, traits) ? traits : null;
	}
	
	private bool ParseTraitNames(ref int index, List<ITypeNode> traits)
	{
		do
		{
			if (!Peek(index, TokenType.Identifier))
			{
				ReportExpected(index, "a trait");
				return false;
			}
			
			traits.Add(ParseType(ref index));
		} while (Match(ref index, TokenType.OpPlus));
		
		return true;
	}
	
	private TraitNode? ParseTrait(ref int index, Token identifier, DeclarationModifiers modifiers,
		List<TypeParameterNode> typeParameters)
	{
		var members = new List<IDeclarationNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			var outer = _allowsMissingBodies;
			_allowsMissingBodies = true;
			var parsed = ParseMembers(ref index, openBrace, null, members);
			_allowsMissingBodies = outer;
			if (!parsed)
				return null;
		}
		
		return new(identifier, modifiers.Tokens, members)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters]
		};
	}
	
	private ImplNode? ParseImpl(ref int index, ITypeNode target, Token keyword, List<TypeParameterNode> typeParameters)
	{
		var traits = new List<ITypeNode>();
		if (!Peek(index, TokenType.OpOpenBrace))
		{
			if (!Peek(index, TokenType.Identifier))
			{
				ReportExpected(index, "a trait or '{'");
				return null;
			}
			
			if (!ParseTraitNames(ref index, traits))
				return null;
		}
		
		var members = new List<IDeclarationNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace) &&
		    !ParseMembers(ref index, openBrace, null, members))
			return null;
		
		return new(target, keyword, traits, members) { TypeParameters = [..typeParameters] };
	}
	
	private bool ParseMembers(ref int index, Token openBrace, Token? block, List<IDeclarationNode> members)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				Report(openBrace, "Expected '}' to close this block");
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref int i, Token open, bool isActive) =>
					    ParseMembers(ref i, open, block, isActive ? members : [])))
					return false;
				
				continue;
			}
			
			if (MatchVisibilityBlock(ref index, _memberContextualKeywords, out var keyword, out var blockBrace))
			{
				if (block is { } outer)
					Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
				
				if (!ParseMembers(ref index, blockBrace, block ?? keyword, members))
					return false;
				
				continue;
			}
			
			// TODO Diagnostics
			if (ParseMember(ref index, block) is not { } member)
			{
				ResyncSimple(ref index);
				return false;
			}
			
			members.Add(member);
		}
		
		return true;
	}
	
	private EnumNode? ParseEnum(ref int index, Token identifier, DeclarationModifiers modifiers, bool isExternal,
		bool isRef, List<TypeParameterNode> typeParameters)
	{
		ITypeNode? tagType = null;
		ITypeNode? matchedType = null;
		if (Match(ref index, out var matchKeyword, TokenType.KeywordMatch))
		{
			if (isExternal)
				Report(matchKeyword, "Cannot use 'match' on ext enums");
			
			matchedType = ParseType(ref index);
		}
		else if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			tagType = ParseType(ref index);
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				ReportExpected(index, "')'", openParen);
				return null;
			}
		}
		
		if (ParseImplementedTraits(ref index) is not { } traits)
			return null;
		
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return new(identifier, modifiers.Tokens, isExternal, isRef, tagType, [], [])
			{
				Visibility = modifiers.Visibility,
				TypeParameters = [..typeParameters],
				MatchedType = matchedType,
				Traits = [..traits]
			};
		
		var cases = new List<EnumCaseNode>();
		var members = new List<IDeclarationNode>();
		if (!ParseEnumBody(ref index, openBrace, null, matchedType, cases, members))
			return null;
		
		return new(identifier, modifiers.Tokens, isExternal, isRef, tagType, cases, members)
		{
			Visibility = modifiers.Visibility,
			TypeParameters = [..typeParameters],
			MatchedType = matchedType,
			Traits = [..traits]
		};
	}
	
	private bool ParseEnumBody(ref int index, Token openBrace, Token? block, ITypeNode? matchedType,
		List<EnumCaseNode> cases, List<IDeclarationNode> members)
	{
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				Report(openBrace, "Expected '}' to close this block");
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref int i, Token open, bool isActive) => isActive
					    ? ParseEnumBody(ref i, open, block, matchedType, cases, members)
					    : ParseEnumBody(ref i, open, block, matchedType, [], [])))
					return false;
				
				continue;
			}
			
			if (MatchVisibilityBlock(ref index, _memberContextualKeywords, out var keyword, out var blockBrace))
			{
				if (block is { } outer)
					Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
				
				if (!ParseEnumBody(ref index, blockBrace, block ?? keyword, matchedType, cases, members))
					return false;
				
				continue;
			}
			
			if (MatchOperatorName(ref index, out var symbol))
			{
				if (ParseOperatorMember(ref index, symbol, block) is not { } operatorMember)
					return false;
				
				members.Add(operatorMember);
				continue;
			}
			
			if (StartsEnumMember(index) &&
			    Match(ref index, out var lifecycleKeyword, _memberContextualKeywords, _lifecycleKeywords))
			{
				Report(lifecycleKeyword, $"Cannot declare {DescribeLifecycle(lifecycleKeyword)} in enums");
				if (ParseLifecycleMember(ref index, lifecycleKeyword, block) is null)
					return false;
				
				continue;
			}
			
			if (StartsEnumMember(index))
			{
				if (ParseEnumMember(ref index, block) is not { } member)
					return false;
				
				members.Add(member);
				continue;
			}
			
			if (ParseEnumCase(ref index, matchedType) is not { } enumCase)
				return false;
			
			if (block is { } caseBlock)
				Report(enumCase.Identifier, $"Cannot declare '{caseBlock.Text}' cases");
			
			cases.Add(enumCase);
		}
		
		return true;
	}
	
	private bool StartsEnumMember(int index) => Peek(index, TokenType.Identifier) &&
	                                            (Peek(index + 1, TokenType.OpColon) ||
	                                             Peek(index + 1, TokenType.OpOpenBracket) && IsOnSameLine(index + 1));
	
	private IDeclarationNode? ParseEnumMember(ref int index, Token? block)
	{
		var identifier = Tokens[index++];
		if (ParseTypeParameters(ref index) is not { } typeParameters || !Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseMemberModifiers(ref index, block);
		if (!IsFunctionMember(index))
			RejectMemberTypeParameters(typeParameters, index);
		
		if (Match(ref index, out var bindingKeyword, _bindingKeywords))
			return ParseStaticField(ref index, identifier, modifiers, bindingKeyword);
		
		if (StartsProperty(index))
			return ParseDeclaration(ref index, identifier, "property",
				(ref i) => ParseProperty(ref i, identifier, modifiers));
		
		if (!IsFunctionMember(index))
		{
			Report(identifier, "Cannot declare fields in enums");
			return null;
		}
		
		RejectWriteRestriction(modifiers, "functions");
		Match(ref index, TokenType.KeywordFun);
		return ParseDeclaration(ref index, identifier, "function",
			(ref i) => ParseFunction(ref i, identifier, modifiers, false, typeParameters));
	}
	
	private EnumCaseNode? ParseEnumCase(ref int index, ITypeNode? matchedType)
	{
		if (!Match(ref index, out var name, TokenType.Identifier))
		{
			ReportExpected(index, "a case name");
			return null;
		}
		
		var payload = new List<FieldNode>();
		if (Tokens[index].Line == name.Line && Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			var parsed = matchedType is null
				? ParsePayload(ref index, openParen, payload)
				: ParseMatchedPayload(ref index, openParen, matchedType, payload);
			
			if (!parsed)
				return null;
		}
		
		var values = new List<IExpressionNode>();
		Token? elseKeyword = null;
		if (Match(ref index, TokenType.OpEqual))
		{
			if (Match(ref index, out var keyword, TokenType.KeywordElse))
				elseKeyword = keyword;
			else
				values.AddRange(ParseCaseValues(ref index));
		}
		
		return new(name, payload, values, elseKeyword);
	}
	
	private List<IExpressionNode> ParseCaseValues(ref int index)
	{
		var values = new List<IExpressionNode> { ParseExpression(ref index) };
		while (Match(ref index, TokenType.OpComma))
			values.Add(ParseExpression(ref index));
		
		return values;
	}
	
	private bool ParsePayload(ref int index, Token openParen, List<FieldNode> payload)
	{
		do
		{
			if (!Match(ref index, out var fieldName, TokenType.Identifier))
			{
				ReportExpected(index, "a payload name");
				return false;
			}
			
			if (!Match(ref index, TokenType.OpColon))
			{
				ReportExpected(index, "':' and a type");
				return false;
			}
			
			payload.Add(new FieldNode(fieldName, ParseType(ref index), []));
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, TokenType.OpCloseParen))
			return true;
		
		ReportExpected(index, "',' or ')'", openParen);
		return false;
	}
	
	private bool ParseMatchedPayload(ref int index, Token openParen, ITypeNode matchedType, List<FieldNode> payload)
	{
		if (!Match(ref index, out var fieldName, TokenType.Identifier))
		{
			ReportExpected(index, "a payload name");
			return false;
		}
		
		payload.Add(new FieldNode(fieldName, matchedType, []));
		if (Match(ref index, TokenType.OpCloseParen))
			return true;
		
		ReportExpected(index, "')'", openParen);
		return false;
	}
	
	private IDeclarationNode? ParseMember(ref int index, Token? block)
	{
		// TODO Diagnostics
		
		if (Match(ref index, out var lifecycleKeyword, _memberContextualKeywords, _lifecycleKeywords))
			return ParseLifecycleMember(ref index, lifecycleKeyword, block);
		
		if (MatchOperatorName(ref index, out var symbol))
			return ParseOperatorMember(ref index, symbol, block);
		
		if (!Match(ref index, out var identifier, TokenType.Identifier) ||
		    ParseTypeParameters(ref index) is not { } typeParameters || !Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseMemberModifiers(ref index, block);
		if (IsFunctionMember(index))
		{
			RejectWriteRestriction(modifiers, "functions");
			Match(ref index, TokenType.KeywordFun);
			return ParseDeclaration(ref index, identifier, "function",
				(ref i) => ParseFunction(ref i, identifier, modifiers, false, typeParameters));
		}
		
		RejectMemberTypeParameters(typeParameters, index);
		if (Match(ref index, out var bindingKeyword, _bindingKeywords))
			return ParseStaticField(ref index, identifier, modifiers, bindingKeyword);
		
		if (StartsProperty(index))
			return ParseDeclaration(ref index, identifier, "property",
				(ref i) => ParseProperty(ref i, identifier, modifiers));
		
		// TODO Casts, operator overloads
		
		// Fields
		return ParseField(ref index, identifier, modifiers);
	}
	
	private void RejectMemberTypeParameters(List<TypeParameterNode> typeParameters, int index)
	{
		if (typeParameters is not [var first, ..])
			return;
		
		var kind = StartsProperty(index) ? "properties" : "fields";
		Report(first.SourceLocation, $"Cannot declare type parameters on {kind}");
	}
	
	private bool MatchOperatorName(ref int index, out Token symbol)
	{
		if (Peek(index + 1, TokenType.OpColon) && Match(ref index, out symbol, _undeclarableOperators))
		{
			Report(symbol, $"Cannot declare '{symbol.Text}' operators");
			return true;
		}
		
		if (Peek(index, TokenType.OpOpenBracket) && Peek(index + 1, TokenType.OpCloseBracket))
		{
			var (source, range) = Tokens[index].SourceLocation;
			range = range.Join(Tokens[index + 1].SourceLocation.Range);
			symbol = new Token(TokenType.OpOpenBracket, new SourceLocation(source, range), "[]");
			index += 2;
			return true;
		}
		
		return Match(ref index, out symbol, _operatorNames);
	}
	
	private FunctionNode? ParseOperatorMember(ref int index, Token symbol, Token? block) =>
		RejectTypeParameters(ref index, "operators")
			? ParseDeclaration(ref index, symbol, "operator", (ref i) => ParseOperator(ref i, symbol, block))
			: null;
	
	private FunctionNode? ParseOperator(ref int index, Token symbol, Token? block)
	{
		if (!Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseMemberModifiers(ref index, block);
		RejectWriteRestriction(modifiers, "operators");
		return Match(ref index, _memberContextualKeywords, TokenType.KeywordOp)
			? ParseFunction(ref index, symbol, modifiers, false, [])
			: null;
	}
	
	private bool IsFunctionMember(int index) =>
		Match(ref index, TokenType.KeywordFun) && !Peek(index, TokenType.OpOpenBracket);
	
	private bool StartsProperty(int index)
	{
		if (Match(ref index, _memberContextualKeywords, TokenType.KeywordProp))
			return IsOnSameLine(index) && Tokens[index].Type is TokenType.Identifier or TokenType.KeywordFun
				or TokenType.KeywordImm or TokenType.KeywordMut;
		
		return Match(ref index, _memberContextualKeywords, _accessorKeywords) && IsOnSameLine(index) &&
		       Tokens[index].Type is TokenType.OpOpenParen or TokenType.OpArrow or TokenType.OpEqual
			       or TokenType.OpOpenBrace;
	}
	
	private PropertyNode? ParseProperty(ref int index, Token identifier, DeclarationModifiers modifiers)
	{
		RejectWriteRestriction(modifiers, "properties");
		if (!Match(ref index, out var keyword, _memberContextualKeywords, TokenType.KeywordProp))
			return ParseAccessor(ref index, null, [], null) is { } accessor
				? new(identifier, modifiers.Tokens, null, null, [accessor]) { Visibility = modifiers.Visibility }
				: null;
		
		var type = ParseType(ref index);
		var accessors = new List<FunctionNode>();
		if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			while (!Match(ref index, TokenType.OpCloseBrace))
			{
				if (AtEnd(index))
				{
					Report(openBrace, "Expected '}' to close this block");
					return null;
				}
				
				if (ParsePropertyAccessor(ref index, type, modifiers.Visibility) is not { } accessor)
					return null;
				
				accessors.Add(accessor);
			}
		}
		
		var restricted = accessors.Where(static accessor => accessor.Visibility is not null).ToList();
		if (restricted.Count > 1)
		{
			foreach (var accessor in restricted)
				Report(accessor.Visibility!.Value, "Cannot restrict both accessors");
		}
		
		return new(identifier, modifiers.Tokens, keyword, type, accessors) { Visibility = modifiers.Visibility };
	}
	
	private FunctionNode? ParsePropertyAccessor(ref int index, ITypeNode type, Token? propertyVisibility)
	{
		var keywords = new List<Token>();
		while (Match(ref index, out var keyword, _memberContextualKeywords, _visibilityKeywords))
			keywords.Add(keyword);
		
		var visibility = CheckVisibility(keywords, null, true);
		if (keywords is [{ Type: not TokenType.KeywordPub } narrowed] && propertyVisibility is { } outer &&
		    !IsNarrower(narrowed, outer))
			Report(narrowed, $"Cannot use '{narrowed.Text}' in '{outer.Text}' properties");
		
		return ParseAccessor(ref index, type, keywords, visibility);
	}
	
	private FunctionNode? ParseAccessor(ref int index, ITypeNode? propertyType, List<Token> modifiers,
		Token? visibility)
	{
		if (!Match(ref index, out var keyword, _memberContextualKeywords, _accessorKeywords))
		{
			ReportExpected(index, "'get' or 'set'");
			return null;
		}
		
		if (ParseAccessorParameters(ref index, propertyType) is not var (receiver, parameters))
			return null;
		
		var isGetter = keyword.Type == TokenType.KeywordGet;
		var returnType = Match(ref index, TokenType.OpArrow) ? ParseType(ref index) : null;
		if (isGetter && parameters.Count > 0)
			Report(parameters[0].SourceLocation, "Getters cannot take parameters");
		else if (!isGetter && parameters.Count != 1)
			Report(parameters.Count > 1 ? parameters[1].SourceLocation : keyword.SourceLocation,
				"Setters must take one parameter");
		
		if (!isGetter && returnType is not null)
			Report(returnType.SourceLocation, "Setters cannot return values");
		else if (isGetter && returnType is null && propertyType is null)
			ReportExpected(index, "'->' and a type");
		
		var resultType = isGetter ? returnType ?? propertyType : null;
		IStatementNode? body;
		if (Match(ref index, TokenType.OpEqual))
			body = ParseExpressionBody(ref index, resultType is null);
		else if (Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			body = ParseBlockStatement(ref index, openBrace);
		else if (_allowsMissingBodies)
			return new(keyword, modifiers, receiver, parameters, resultType, null, false) { Visibility = visibility };
		else
		{
			ReportExpected(index, "'=' or '{'");
			return null;
		}
		
		return body is null
			? null
			: new(keyword, modifiers, receiver, parameters, resultType, body, false) { Visibility = visibility };
	}
	
	private (ReceiverNode? Receiver, List<ParameterNode> Parameters)? ParseAccessorParameters(ref int index,
		ITypeNode? propertyType)
	{
		var parameters = new List<ParameterNode>();
		if (!Match(ref index, out var openParen, TokenType.OpOpenParen))
			return (null, parameters);
		
		var receiver = ParseReceiver(ref index);
		if (Match(ref index, TokenType.OpCloseParen))
			return (receiver, parameters);
		
		if (receiver is not null && !Match(ref index, TokenType.OpComma))
		{
			ReportExpected(index, "',' or ')'", openParen);
			return null;
		}
		
		do
		{
			Token? mode = Match(ref index, out var keyword, _parameterModes) ? keyword : null;
			if (!Match(ref index, out var name, TokenType.Identifier))
			{
				ReportExpected(index, "a parameter name");
				return null;
			}
			
			if (Match(ref index, TokenType.OpColon))
				parameters.Add(new(mode, name, ParseType(ref index), null));
			else if (propertyType is not null)
				parameters.Add(new(mode, name, propertyType, null));
			else
			{
				ReportExpected(index, "':' and a type");
				return null;
			}
		} while (Match(ref index, TokenType.OpComma));
		
		if (Match(ref index, TokenType.OpCloseParen))
			return (receiver, parameters);
		
		ReportExpected(index, "',' or ')'", openParen);
		return null;
	}
	
	private List<Token> ParseFieldModifiers(ref int index)
	{
		var modifiers = new List<Token>();
		if ((Peek(index + 1, TokenType.Identifier) || Peek(index + 1, TokenType.KeywordFun)) &&
		    Tokens[index + 1].Line == Tokens[index].Line &&
		    Match(ref index, out var reqToken, _memberContextualKeywords, TokenType.KeywordReq))
			modifiers.Add(reqToken);
		
		return modifiers;
	}
	
	private IDeclarationNode? ParseLifecycleMember(ref int index, Token keyword, Token? block)
	{
		if (!RejectTypeParameters(ref index, DescribeLifecycle(keyword)))
			return null;
		
		return keyword.Type == TokenType.KeywordNew
			? ParseConstructor(ref index, keyword, block)
			: ParseDestructor(ref index, keyword, block);
	}
	
	private static string DescribeLifecycle(Token keyword) =>
		keyword.Type == TokenType.KeywordNew ? "constructors" : "destructors";
	
	private ConstructorNode? ParseConstructor(ref int index, Token newKeyword, Token? block)
	{
		// When this is called, the identifier and fun keyword are already consumed
		// Caller is expected to resync in case of errors
		
		// @TODO Diagnostics
		
		if (!Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseMemberModifiers(ref index, block);
		RejectWriteRestriction(modifiers, "constructors");
		if (!Match(ref index, _memberContextualKeywords, TokenType.KeywordOp))
			return null;
		
		if (ParseParameters(ref index, true) is not { } parameters)
			return null;
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return _allowsMissingBodies
				? new(newKeyword, modifiers.Tokens, parameters, null, newKeyword.SourceLocation)
				{
					Visibility = modifiers.Visibility
				}
				: null;
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		var (source, range) = newKeyword.SourceLocation;
		range = range.Join(body.SourceLocation.Range);
		
		return new(newKeyword, modifiers.Tokens, parameters, body, new(source, range))
		{
			Visibility = modifiers.Visibility
		};
	}
	
	private DestructorNode? ParseDestructor(ref int index, Token dropKeyword, Token? block)
	{
		if (!Match(ref index, TokenType.OpColon))
			return null;
		
		var modifiers = ParseMemberModifiers(ref index, block);
		RejectWriteRestriction(modifiers, "destructors");
		if (modifiers.Visibility is { Type: not TokenType.KeywordPub } visibility)
			Report(modifiers.Tokens.Contains(visibility) ? visibility : dropKeyword,
				$"Cannot declare '{visibility.Text}' destructors");
		
		if (!Match(ref index, _memberContextualKeywords, TokenType.KeywordOp))
			return null;
		
		if (ParseParameters(ref index, true) is not { } parameters)
			return null;
		
		if (parameters.Count > 0)
			Report(parameters[0].SourceLocation, "Destructors cannot take parameters");
		
		if (!Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return null;
		
		if (ParseBlockStatement(ref index, openBraceToken) is not { } body)
			return null;
		
		var (source, range) = dropKeyword.SourceLocation;
		range = range.Join(body.SourceLocation.Range);
		
		return new(dropKeyword, body, new(source, range));
	}
	
	private FieldNode ParseField(ref int index, Token identifier, DeclarationModifiers modifiers)
	{
		var tokens = modifiers.Tokens.Concat(ParseFieldModifiers(ref index));
		var type = ParseType(ref index);
		return new(identifier, type, tokens)
		{
			Visibility = modifiers.Visibility,
			WriteVisibility = modifiers.WriteVisibility
		};
	}
	
	private BlockStatementNode? ParseBlockStatement(ref int index, Token open)
	{
		// @TODO Diagnostics
		
		// @TODO Replace with statement node type
		var statements = new List<IStatementNode>();
		if (!ParseStatements(ref index, open, statements, out var close))
			return null;
		
		var source = open.SourceLocation.Source;
		var range = open.SourceLocation.Range.Join(close.SourceLocation.Range);
		var sourceLocation = new SourceLocation(source, range);
		return new(sourceLocation, statements.ToImmutableArray());
	}
	
	private bool ParseStatements(ref int index, Token open, List<IStatementNode> statements, out Token close)
	{
		while (!Match(ref index, out close, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				Report(open, "Expected '}' to close this block");
				return false;
			}
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref int i, Token branch, bool isActive) =>
					    ParseStatements(ref i, branch, isActive ? statements : [], out _)))
				{
					ResyncSimple(ref index);
					return false;
				}
				
				continue;
			}
			
			if (ParseStatement(ref index) is not { } statement)
			{
				ResyncSimple(ref index);
				return false;
			}
			
			statements.Add(statement);
		}
		
		return true;
	}
	
	private BlockStatementNode? ParseWhenStatement(ref int index, Token whenToken)
	{
		var statements = new List<IStatementNode>();
		return ParseWhen(ref index, (ref int i, Token open, bool isActive) =>
			ParseStatements(ref i, open, isActive ? statements : [], out _))
			? new(Span(whenToken, Tokens[index - 1]), [..statements])
			: null;
	}
	
	private IStatementNode? ParseStatement(ref int index)
	{
		// @TODO Diagnostics
		
		if (Match(ref index, out var bindingToken, _bindingKeywords))
			return ParseVarStatement(ref index, bindingToken);
		
		if (Match(ref index, out var whenToken, TokenType.KeywordWhen))
			return ParseWhenStatement(ref index, whenToken);
		
		if (Match(ref index, out var ifToken, TokenType.KeywordIf))
			return ParseIfStatement(ref index, ifToken);
		
		if (Match(ref index, out var forToken, TokenType.KeywordFor))
			return ParseForStatement(ref index, forToken);
		
		if (Match(ref index, out var loopToken, TokenType.KeywordLoop))
			return ParseLoopStatement(ref index, loopToken);
		
		if (Match(ref index, out var matchToken, TokenType.KeywordMatch))
			return ParseMatchStatement(ref index, matchToken);
		
		if (Match(ref index, out var openBraceToken, TokenType.OpOpenBrace))
			return ParseBlockStatement(ref index, openBraceToken);
		
		// Named loops
		var expr = ParseExpression(ref index);
		if (expr is not VarExpressionNode { Identifier.Type: TokenType.Identifier } var)
			return new ExpressionStatementNode(expr);
		
		// Attempt to parse as loop, else backtrack
		var backtrackIndex = index;
		
		if (Match(ref index, TokenType.OpColon))
		{
			if (Match(ref index, out var namedForToken, TokenType.KeywordFor))
				return ParseForStatement(ref index, namedForToken, var.Identifier);
			
			if (Match(ref index, out var namedLoopToken, TokenType.KeywordLoop))
				return ParseLoopStatement(ref index, namedLoopToken, var.Identifier);
		}
		
		index = backtrackIndex;
		return new ExpressionStatementNode(expr);
	}
	
	private IStatementNode? ParseForStatement(ref int index, Token forToken, Token? labelToken = null)
	{
		Report(forToken, "'for' loops are not supported yet");
		return null;
	}
	
	private IStatementNode? ParseLoopStatement(ref int index, Token loopToken, Token? labelToken = null)
	{
		// TODO Diagnostics
		
		var (source, range) = loopToken.SourceLocation;
		if (labelToken is { } label)
			range = range.Join(label.SourceLocation.Range);
		
		// While-loop:
		if (Match(ref index, TokenType.KeywordWhile))
		{
			var condition = ParseExpression(ref index);
			
			if (ParseStatement(ref index) is not { } body)
				return null;
			
			range = range.Join(body.SourceLocation.Range);
			return new WhileStatementNode(new(source, range), condition, body, labelToken);
		}
		
		// Repeat loop:
		if (Match(ref index, TokenType.KeywordFor))
		{
			var count = ParseExpression(ref index);
			
			if (ParseStatement(ref index) is not { } body)
				return null;
			
			range = range.Join(body.SourceLocation.Range);
			return new RepeatStatementNode(new(source, range), count, body, labelToken);
		}
		
		// Other loops
		if (ParseStatement(ref index) is not { } statement)
			return null;
		
		// Do-While loop:
		if (Match(ref index, TokenType.KeywordWhile))
		{
			var condition = ParseExpression(ref index);
			
			range = range.Join(condition.SourceLocation.Range);
			return new DoWhileStatementNode(new(source, range), statement, condition, labelToken);
		}
		
		// Infinite loop:
		range = range.Join(statement.SourceLocation.Range);
		return new LoopStatementNode(new(source, range), statement, labelToken);
	}
	
	private IfStatementNode? ParseIfStatement(ref int index, Token ifToken)
	{
		// ifToken already consumed when this function is called
		
		// TODO Diagnostics
		
		var condition = ParseExpression(ref index);
		
		if (ParseStatement(ref index) is not { } then)
			return null;
		
		var (source, range) = ifToken.SourceLocation;
		if (!Match(ref index, TokenType.KeywordElse))
		{
			range = range.Join(then.SourceLocation.Range);
			return new(new(source, range), condition, then, null);
		}
		
		if (ParseStatement(ref index) is not { } @else)
			return null;
		
		range = range.Join(@else.SourceLocation.Range);
		return new(new(source, range), condition, then, @else);
	}
	
	private MatchStatementNode? ParseMatchStatement(ref int index, Token matchToken)
	{
		Token? mode = Match(ref index, out var keyword, TokenType.KeywordMut) ? keyword : null;
		var value = ParseExpression(ref index);
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
		{
			ReportExpected(index, "'{'");
			return null;
		}
		
		var arms = new List<MatchArmNode>();
		Token closeBrace;
		while (!Match(ref index, out closeBrace, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				Report(openBrace, "Expected '}' to close this block");
				return null;
			}
			
			if (ParseMatchArm(ref index) is not { } arm)
				return null;
			
			arms.Add(arm);
		}
		
		var (source, range) = matchToken.SourceLocation;
		range = range.Join(closeBrace.SourceLocation.Range);
		return new(new(source, range), mode, value, arms);
	}
	
	private MatchArmNode? ParseMatchArm(ref int index)
	{
		PatternNode? pattern = null;
		SourceLocation location;
		if (Match(ref index, out var elseToken, TokenType.KeywordElse))
			location = elseToken.SourceLocation;
		else
		{
			pattern = new ExpressionParser(Tokens).ParsePattern(ref index);
			location = pattern.SourceLocation;
		}
		
		if (!Match(ref index, TokenType.OpFatArrow))
		{
			ReportExpected(index, "'=>'");
			return null;
		}
		
		return ParseStatement(ref index) is { } body ? new(pattern, location, body) : null;
	}
	
	private VarStatementNode? ParseVarStatement(ref int index, Token keyword)
	{
		// keyword already consumed when this function is called
		
		// TODO Diagnostics
		
		// TODO Attempt resync 
		if (!Match(ref index, out var identifier, TokenType.Identifier))
		{
			Report(Tokens[index], $"Expected a name after '{keyword.Text}'");
			return null;
		}
		
		var type = Match(ref index, TokenType.OpColon) ? ParseType(ref index) : null;
		var isMutable = keyword.Type == TokenType.KeywordVar;
		
		var (source, range) = keyword.SourceLocation;
		
		if (!Match(ref index, TokenType.OpEqual))
			return new(new(source, range), identifier, type, null, isMutable);
		
		var initializer = ParseExpression(ref index);
		range = range.Join(initializer.SourceLocation.Range);
		return new(new(source, range), identifier, type, initializer, isMutable);
	}
	
	private IExpressionNode ParseExpression(ref int index) => new ExpressionParser(Tokens).Parse(ref index);
	private ITypeNode ParseType(ref int index) => new TypeParser(Tokens).Parse(ref index);
	
	private void ResyncTopLevel(ref int index, bool insideBlock = false)
	{
		// Skip until another declaration starts. If we encounter braces, keep skipping until closed
		var braceCount = 0;
		var loop = true;
		while (loop && !AtEnd(index))
		{
			switch (Tokens[index].Type)
			{
				case TokenType.OpOpenBrace:
					braceCount++;
					index++;
					break;
				
				case TokenType.OpCloseBrace when braceCount == 0 && insideBlock:
					loop = false;
					break;
				
				case TokenType.OpCloseBrace:
					braceCount--;
					index++;
					break;
				
				case TokenType.KeywordWhen when braceCount == 0:
					loop = false;
					break;
				
				case TokenType.Identifier:
					if (braceCount > 0 || !StartsTopLevelDeclaration(index))
						index++;
					else
						loop = false;
					
					break;
				
				default:
					index++;
					break;
			}
		}
	}
	
	private bool StartsTopLevelDeclaration(int index)
	{
		var keywordIndex = index;
		return Peek(index + 1, TokenType.OpColon) ||
		       Peek(index + 1, TokenType.OpOpenBracket) && IsOnSameLine(index + 1) ||
		       Match(ref keywordIndex, _topLevelContextualKeywords, _topLevelKeywords);
	}
	
	private void SkipDeclaration(ref int index, int start, bool insideBlock = false)
	{
		var braceCount = 0;
		var parameterLists = new Stack<bool>();
		for (var i = start; !AtEnd(i); i++)
		{
			switch (Tokens[i].Type)
			{
				case TokenType.OpOpenBrace:
					braceCount++;
					break;
				
				case TokenType.OpCloseBrace:
					braceCount--;
					if (braceCount > 0)
						break;
					
					index = braceCount < 0 && insideBlock ? i : i + 1;
					return;
				
				case TokenType.OpOpenParen:
					parameterLists.Push(FollowsFun(i));
					break;
				
				case TokenType.OpCloseParen:
					parameterLists.TryPop(out _);
					break;
				
				case TokenType.Identifier when i > start && braceCount == 0 && !parameterLists.Contains(true) &&
				                               StartsDeclarationOnLine(i):
					index = i;
					return;
			}
		}
		
		ResyncTopLevel(ref index, insideBlock);
	}
	
	private bool FollowsFun(int index)
	{
		var keywordIndex = index - 1;
		return Match(ref keywordIndex, TokenType.KeywordFun);
	}
	
	private bool StartsDeclarationOnLine(int index) =>
		Tokens[index].Line != Tokens[index - 1].Line && StartsTopLevelDeclaration(index);
	
	private void ResyncSimple(ref int index)
	{
		SkipUntil(ref index, TokenType.OpSemicolon);
		SkipIf(ref index, TokenType.OpSemicolon);
	}
	
	private void SkipIf(ref int index, TokenType type)
	{
		if (!AtEnd(index) && Tokens[index].Type == type)
			index++;
	}
}