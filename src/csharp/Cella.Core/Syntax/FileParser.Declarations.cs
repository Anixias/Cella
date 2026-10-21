using Cella.Core.Binding;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
{
	private bool ParseBlanketImpl(ref int index, List<IDeclarationNode> declarations, Token? block, bool insideBlock)
	{
		var declarationStart = index;
		var openBracket = Tokens[index];
		if (ParseTypeParameters(ref index, true) is not { } typeParameters)
		{
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		if (!Match(ref index, TokenType.OpColon))
		{
			ReportExpected(index, "':'");
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		var modifiers = ParseDeclarationModifiers(ref index, block);
		if (!Match(ref index, out var implKeyword, _topLevelContextualKeywords, TokenType.KeywordImpl))
		{
			ReportExpected(index, "'impl'");
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		var target = new IdentifierTypeNode(typeParameters[0].Identifier);
		var impl = ParseDeclaration(ref index, openBracket, "impl",
			(ref i) => ParseImpl(ref i, target, implKeyword, typeParameters, modifiers));
		
		if (impl is null)
			SkipDeclaration(ref index, declarationStart, insideBlock);
		else
			declarations.Add(new ImplNode(impl.Target, impl.Keyword, impl.Traits, impl.Members)
			{
				TypeParameters = impl.TypeParameters,
				IsBlanket = true,
				Body = impl.Body,
				LastHeaderToken = impl.LastHeaderToken
			});
		
		return true;
	}
	
	private bool ParseNamedDeclaration(ref int index, List<IDeclarationNode> declarations, Token? block,
		bool insideBlock)
	{
		var declarationStart = index;
		Token identifier;
		if (StartsContextualName(index))
			identifier = Tokens[index++];
		else if (!Match(ref index, out identifier, _topLevelContextualKeywords, TokenType.Identifier))
			return false;
		
		var target = ParseImplTarget(ref index, identifier);
		if (ParseTypeParameters(ref index) is not { } typeParameters)
		{
			_droppedNames.Add(identifier);
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		if (!Match(ref index, TokenType.OpColon))
		{
			Report(IsOnSameLine(index) ? Tokens[index] : identifier, $"Expected ':' after '{identifier.Text}'");
			_droppedNames.Add(identifier);
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		var modifiers = ParseDeclarationModifiers(ref index, block);
		if (Match(ref index, out var implKeyword, _topLevelContextualKeywords, TokenType.KeywordImpl))
		{
			var impl = ParseDeclaration(ref index, identifier, "impl",
				(ref i) => ParseImpl(ref i, target, implKeyword, typeParameters, modifiers));
			
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
		
		RejectReservedName(identifier);
		RejectReservedNames(typeParameters);
		
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
		
		var isOwn = Match(ref index, out var ownKeyword, _topLevelContextualKeywords, TokenType.KeywordOwn);
		var isRef = Match(ref index, out var refKeyword, _topLevelContextualKeywords, TokenType.KeywordRef);
		
		// Record
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordRec))
		{
			var record = ParseDeclaration(ref index, identifier, "record",
				(ref i) => ParseRecord(ref i, identifier, modifiers, isRef, isOwn, false, typeParameters));
			
			if (record is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(record);
			
			return true;
		}
		
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordUnion))
		{
			if (isOwn)
				Report(ownKeyword, "Cannot declare 'own' unions");
			
			if (isRef)
				Report(refKeyword, "Cannot declare 'ref' unions");
			
			if (typeParameters is [var parameter, ..])
				Report(parameter.SourceLocation, "Cannot declare type parameters on unions");
			
			var union = ParseDeclaration(ref index, identifier, "union",
				(ref i) => ParseRecord(ref i, identifier, modifiers, false, false, true, []));
			
			if (union is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(union);
			
			return true;
		}
		
		var kindIndex = index;
		var isExternal = !isRef && Match(ref index, _topLevelContextualKeywords, TokenType.KeywordExt);
		var externalName = isExternal && Tokens[index].Type == TokenType.OpOpenParen
			? ParseExternalName(ref index)
			: null;
		
		if (typeParameters is [var first, ..] && DescribeTypeParameterError(index, isExternal) is { } error)
			Report(first.SourceLocation, error);
		
		if (Match(ref index, _topLevelContextualKeywords, TokenType.KeywordEnum))
		{
			if (externalName is { } enumName)
				Report(enumName, "Cannot name 'ext' enums");
			
			if (isOwn && isExternal)
				Report(ownKeyword, "Cannot combine 'own' and 'ext'");
			
			var enumTypeParameters = isExternal ? [] : typeParameters;
			var enumNode = ParseDeclaration(ref index, identifier, "enum",
				(ref i) => ParseEnum(ref i, identifier, modifiers, isExternal, isRef, isOwn && !isExternal,
					enumTypeParameters));
			
			if (enumNode is null)
				SkipDeclaration(ref index, declarationStart, insideBlock);
			else
				declarations.Add(enumNode);
			
			return true;
		}
		
		if (isExternal && Tokens[index].Text is "rec" or "ref")
		{
			Report(Tokens[index], Tokens[index].Text == "rec"
				? "Cannot declare 'ext' records"
				: "Cannot combine 'ext' and 'ref'");
			
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		if (isRef || isOwn)
		{
			var keyword = isRef ? "ref" : "own";
			Report(Tokens[index], Tokens[index].Text == "ext"
				? $"Cannot combine '{keyword}' and 'ext'"
				: $"Expected 'rec' or 'enum' after '{keyword}'");
			
			if (!AtEnd(index))
				index++;
			
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
		// Global
		if (Match(ref index, out var bindingKeyword, _bindingKeywords))
		{
			var global = ParseDeclaration(ref index, identifier, "global",
				(ref i) => ParseGlobal(ref i, identifier, modifiers, bindingKeyword, isExternal, externalName));
			
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
				(ref i) => ParseFunction(ref i, identifier, modifiers, isExternal, isExternal ? [] : typeParameters,
					externalName));
			
			if (function is null)
			{
				_droppedNames.Add(identifier);
				SkipDeclaration(ref index, declarationStart, insideBlock);
			}
			else
			{
				declarations.Add(function);
			}
			
			return true;
		}
		
		// Unknown declaration
		index = kindIndex;
		_droppedNames.Add(identifier);
		if (!AtEnd(index) && !IsOnSameLine(index))
		{
			ReportExpected(index - 1, "a declaration");
			return true;
		}
		
		Report(Tokens[index], "Expected a declaration");
		if (!AtEnd(index))
			index++;
		
		ResyncTopLevel(ref index, insideBlock);
		return true;
	}
	
	private bool StartsExternalFunctionType(int index) =>
		Match(ref index, _constraintKeywords, TokenType.KeywordExt) && Peek(index, TokenType.KeywordFun);
	
	private readonly record struct DeclarationModifiers
	(
		List<Token> Tokens,
		Token? Visibility,
		Token? WriteVisibility = null,
		SourceLocation WriteLocation = default
	);
	
	private DeclarationModifiers ParseDeclarationModifiers(ref int index, Token? block)
	{
		var start = index;
		var modifiers = ParseDeclarationKeywords(ref index, block);
		RecordSites(start, index, CompletionSite.Declaration);
		return modifiers;
	}
	
	private DeclarationModifiers ParseDeclarationKeywords(ref int index, Token? block)
	{
		var keywords = new List<Token>();
		while (Match(ref index, out var keyword, _topLevelContextualKeywords, _visibilityKeywords))
		{
			keywords.Add(keyword);
			if (keyword.Type != TokenType.KeywordPub && StartsMemberType(index + 1) &&
			    Match(ref index, out var set, _memberContextualKeywords, TokenType.KeywordSet))
				Report(Span(keyword, set), $"Cannot use '{keyword.Text} set' outside types");
		}
		
		return new(keywords, CheckVisibility(keywords, block, false));
	}
	
	private DeclarationModifiers ParseMemberModifiers(ref int index, Token? block)
	{
		var start = index;
		var modifiers = ParseMemberKeywords(ref index, block);
		RecordSites(start, index, CompletionSite.Declaration);
		return modifiers;
	}
	
	private DeclarationModifiers ParseMemberKeywords(ref int index, Token? block)
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
			or TokenType.KeywordFun or TokenType.KeywordRef or TokenType.KeywordImm or TokenType.KeywordMut
			or TokenType.KeywordVal or TokenType.KeywordVar or TokenType.KeywordConst;
	
	private static bool IsNarrower(Token write, Token read) =>
		write.Type == TokenType.KeywordPvt && read.Type == TokenType.KeywordMod;
	
	private Token? CheckVisibility(List<Token> keywords, Token? block, bool isMember)
	{
		if (keywords.Count > 1)
			Report(Span(keywords[0], keywords[^1]),
				$"Cannot combine {DiagnosticReporter.JoinNames([..keywords.Select(static k => k.Text)])}");
		else if (isMember && !allowsPublicMembers && keywords is [{ Type: TokenType.KeywordPub } pub])
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
	
	private readonly record struct ErrorWindow(int Errors, int Attributed, int FirstNested, int Dropped, int Start);
	
	private ErrorWindow OpenErrorWindow(int index) =>
		new(Diagnostics.ErrorCount, attributedErrors, _parsedDeclarations.Count, droppedMembers, index);
	
	private T? ParseDeclaration<T>(ref int index, Token start, string kind, DeclarationParser<T> parse)
		where T : class
	{
		var errorCount = Diagnostics.ErrorCount;
		var window = OpenErrorWindow(index);
		var outerNesting = reportedNesting;
		reportedNesting = false;
		T? declaration;
		
		try
		{
			declaration = parse(ref index);
		}
		catch (ParseException e)
		{
			AddParseError(e);
			declaration = null;
		}
		
		reportedNesting = outerNesting;
		
		if (declaration is null && Diagnostics.ErrorCount == errorCount)
			Report(start, $"Invalid {kind} declaration");
		
		RecordSyntaxErrors(declaration, window, index);
		return declaration;
	}
	
	private bool RecordSyntaxErrors(bool parsed, List<IDeclarationNode> declarations, int first, ErrorWindow window,
		int end)
	{
		RecordSyntaxErrors(declarations[first..], window, end);
		return parsed;
	}
	
	private void RecordSyntaxErrors(object? declaration, ErrorWindow window, int end)
	{
		ReportLexicalErrors(window.Start, end);
		var errors = Diagnostics.ErrorCount - window.Errors;
		var ownErrors = errors - (attributedErrors - window.Attributed);
		attributedErrors = window.Attributed + errors;
		
		IDeclarationNode[] nodes = declaration switch
		{
			IDeclarationNode node => [node],
			IEnumerable<IDeclarationNode> list => [..list],
			_ => []
		};
		
		if (ownErrors > 0)
		{
			_brokenDeclarations.UnionWith(_parsedDeclarations.Skip(window.FirstNested));
			_brokenDeclarations.UnionWith(nodes);
		}
		
		if (droppedMembers > window.Dropped)
			_brokenDeclarations.UnionWith(nodes);
		
		if (errors > 0)
			_declarationsWithErrors.UnionWith(nodes);
		
		_parsedDeclarations.AddRange(nodes);
	}
	
	private List<IDeclarationNode>? ParseExternalDeclarations(ref int index, Token? block)
	{
		// 'ext' token already consumed by caller
		
		// Optional library origin
		Token? origin;
		if (Match(ref index, out var openParen, TokenType.OpOpenParen))
		{
			var nameIndex = index;
			if (!Match(ref index, out var str, TokenType.StringLiteral) || str.Suffix is not null ||
			    str.Interpolation is { Holes.IsEmpty: false } || str.Text.Length == 0 || str.Text.Contains('\0'))
			{
				ReportExpected(nameIndex, "a library name");
				return null;
			}
			
			origin = str;
			
			if (!Match(ref index, TokenType.OpCloseParen))
			{
				ReportExpected(index, "')'", openParen);
				return null;
			}
		}
		else
			origin = null;
		
		if (!Match(ref index, out var openBrace, TokenType.OpOpenBrace))
			return ParseExternalDeclaration(ref index, origin, block) is { } single ? [single] : null;
		
		var nodes = new List<IDeclarationNode>();
		return ParseExternalBody(ref index, openBrace, origin, block, nodes) ? nodes : null;
	}
	
	private bool ParseExternalBody(ref int index, Token openBrace, Token? origin, Token? block,
		List<IDeclarationNode> nodes)
	{
		int? errorsBefore = null;
		while (!Match(ref index, TokenType.OpCloseBrace))
		{
			if (AtEnd(index))
			{
				ReportUnclosed(openBrace);
				return false;
			}
			
			if (!SeparateMember(ref index, ref errorsBefore, "declaration"))
				continue;
			
			if (Match(ref index, TokenType.KeywordWhen))
			{
				if (!ParseWhen(ref index, (ref i, open, isActive) =>
					    ParseExternalBody(ref i, open, origin, block, isActive ? nodes : [])))
					return false;
				
				continue;
			}
			
			if (MatchVisibilityBlock(ref index, _topLevelContextualKeywords, out var visibility, out var inner))
			{
				Report(visibility, $"Cannot use '{visibility.Text}' in 'ext' blocks");
				if (!ParseExternalBody(ref index, inner, origin, block, nodes))
					return false;
				
				continue;
			}
			
			var start = index;
			var errorCount = Diagnostics.ErrorCount;
			if (ParseExternalDeclaration(ref index, origin, block) is not { } ext)
			{
				if (Diagnostics.ErrorCount == errorCount)
					Report(Tokens[start], "Invalid external declaration");
				
				if (index == start)
					index++;
				
				ResyncTopLevel(ref index, true);
				continue;
			}
			
			nodes.Add(ext);
		}
		
		return true;
	}
}