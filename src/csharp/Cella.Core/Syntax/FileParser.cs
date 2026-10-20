using Cella.Core.Binding;
using System.Collections.Immutable;
using Cella.Core.Symbols;
using Cella.Core.Syntax.Nodes;
using Cella.Core.Text;
using Cella.Diagnostics;

namespace Cella.Core.Syntax;

public sealed partial class FileParser
(
	ImmutableArray<Token> tokens,
	string fileName,
	string fullPath,
	IReadOnlyDictionary<string, bool> flags
) : BaseParser<FileNode>(tokens)
{
	private static readonly Dictionary<string, TokenType> _topLevelContextualKeywords =
		BuildContextualKeywords(TokenType.KeywordMod, TokenType.KeywordUse, TokenType.KeywordPub, TokenType.KeywordPvt,
			TokenType.KeywordExt, TokenType.KeywordRec, TokenType.KeywordEnum, TokenType.KeywordUnion,
			TokenType.KeywordRef, TokenType.KeywordTrait, TokenType.KeywordImpl);
	
	private static readonly Dictionary<string, TokenType> _traitListKeywords =
		BuildContextualKeywords(TokenType.KeywordImpl, TokenType.KeywordReq);
	
	private static readonly Dictionary<string, TokenType> _memberContextualKeywords =
		BuildContextualKeywords(TokenType.KeywordPub, TokenType.KeywordPvt, TokenType.KeywordMod, TokenType.KeywordSet,
			TokenType.KeywordOp, TokenType.KeywordReq, TokenType.KeywordGet, TokenType.KeywordProp);
	
	private static readonly HashSet<TokenType> _constraintTypes =
	[
		TokenType.KeywordNoref, TokenType.KeywordCopy, TokenType.KeywordDrop, TokenType.KeywordTrait,
		TokenType.KeywordExt, TokenType.KeywordDefault
	];
	
	private static readonly Dictionary<string, TokenType> _constraintKeywords =
		BuildContextualKeywords(_constraintTypes.Where(static type => type.IsContextual));
	
	private static readonly Dictionary<string, TokenType> _conversionKeywords =
		BuildContextualKeywords(TokenType.KeywordAs);
	
	private static readonly HashSet<TokenType> _operatorNames =
	[
		TokenType.OpEqualEqual, TokenType.OpBangEqual, TokenType.OpLess, TokenType.OpLessEqual, TokenType.OpGreater,
		TokenType.OpGreaterEqual, TokenType.OpPlus, TokenType.OpMinus, TokenType.OpStar, TokenType.OpSlash,
		TokenType.OpPercent, TokenType.OpPlusPercent, TokenType.OpMinusPercent, TokenType.OpStarPercent,
		TokenType.OpLessLess, TokenType.OpGreaterGreater, TokenType.OpLessLessLess,
		TokenType.OpGreaterGreaterGreater, TokenType.OpAmpersand, TokenType.OpBar, TokenType.OpHat, TokenType.OpTilde,
		TokenType.OpQuestion,
		TokenType.OpPlusEqual, TokenType.OpMinusEqual, TokenType.OpStarEqual, TokenType.OpSlashEqual,
		TokenType.OpPercentEqual, TokenType.OpPlusPercentEqual, TokenType.OpMinusPercentEqual,
		TokenType.OpStarPercentEqual, TokenType.OpLessLessEqual, TokenType.OpGreaterGreaterEqual,
		TokenType.OpLessLessLessEqual, TokenType.OpGreaterGreaterGreaterEqual, TokenType.OpAmpersandEqual,
		TokenType.OpBarEqual, TokenType.OpHatEqual, TokenType.KeywordIn, TokenType.KeywordFor
	];
	
	private static readonly HashSet<TokenType> _undeclarableOperators =
		[TokenType.OpBang, TokenType.OpAmpersandAmpersand, TokenType.OpBarBar, TokenType.OpDot, TokenType.OpEqual];
	
	private static readonly HashSet<TokenType> _topLevelKeywords =
		[TokenType.KeywordMod, TokenType.KeywordUse, TokenType.KeywordExt, TokenType.KeywordPvt];
	
	private static readonly HashSet<TokenType> _visibilityKeywords =
		[TokenType.KeywordPub, TokenType.KeywordPvt, TokenType.KeywordMod];
	
	private static readonly HashSet<TokenType> _blockKeywords = [TokenType.KeywordPvt, TokenType.KeywordMod];
	
	private static readonly HashSet<TokenType> _bindingKeywords =
		[TokenType.KeywordVal, TokenType.KeywordVar, TokenType.KeywordConst];
	
	private static readonly HashSet<TokenType> _externalBindingKeywords = [TokenType.KeywordVal, TokenType.KeywordVar];
	
	private static readonly HashSet<TokenType> _loopKeywords =
		[TokenType.KeywordFor, TokenType.KeywordWhile, TokenType.KeywordDo, TokenType.KeywordLoop];
	
	private static readonly HashSet<TokenType> _rangeOperators = [TokenType.OpDotDot, TokenType.OpDotDotEqual];
	
	private static readonly HashSet<TokenType> _loopBindingModes =
		[TokenType.KeywordImm, TokenType.KeywordMut, TokenType.KeywordOwn];
	
	private static readonly HashSet<TokenType> _accessorKeywords = [TokenType.KeywordGet, TokenType.KeywordSet];
	
	private static readonly HashSet<TokenType> _lifecycleKeywords = [TokenType.KeywordNew, TokenType.KeywordDrop];
	
	private static readonly HashSet<string> _bodyDeclarationWords =
		["fun", "val", "var", "rec", "enum", "union", "trait", "impl", "ext", "pub", "mod", "pvt", "ref"];
	
	private static readonly HashSet<string> _declarationKinds =
		["fun", "val", "var", "rec", "enum", "union", "trait", "impl", "op", "prop"];
	
	private static readonly HashSet<string> _topLevelFormWords = ["pub", "pvt", "mod", "use", "ext"];
	
	private static readonly HashSet<string> _nestedKinds = ["rec", "enum", "union", "trait", "impl"];
	
	private readonly HashSet<IStatementNode> _brokenStatements = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<IDeclarationNode> _brokenDeclarations = new(ReferenceEqualityComparer.Instance);
	private readonly HashSet<IDeclarationNode> _declarationsWithErrors = new(ReferenceEqualityComparer.Instance);
	private readonly List<IDeclarationNode> _parsedDeclarations = [];
	private readonly bool[] _lexicalReported = new bool[tokens.Length];
	
	public CompletionProbe? Probe { get; init; }
	private int attributedErrors;
	private int droppedMembers;
	private readonly List<Token> _droppedNames = [];
	private readonly HashSet<TextRange> _inactiveRanges = [];
	
	private static Dictionary<string, TokenType> BuildContextualKeywords(params IEnumerable<TokenType> tokenTypes) =>
		tokenTypes.ToDictionary(static t => t.Representation);
	
	public DiagnosticList Diagnostics { get; } = new();
	
	private bool allowsMissingBodies;
	private bool allowsPublicMembers;
	private bool reportedNesting;
	private int blockDepth;
	
	private void Report(SourceLocation location, string message,
		DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
		Diagnostics.Add(new(severity, location, message));
	
	private void Report(Token token, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
		Diagnostics.Add(new(severity, token.SourceLocation, message));
	
	protected override void ReportSeparatorError(Diagnostic diagnostic) => Diagnostics.Add(diagnostic);
	
	private void RejectDiscardName(Token identifier)
	{
		if (identifier.Text == "_")
			Report(identifier, "Cannot use '_' as a name");
	}
	
	private void RejectReservedName(Token identifier)
	{
		RejectDiscardName(identifier);
		RejectBuiltInName(identifier);
	}
	
	private void RejectBuiltInName(Token identifier)
	{
		if (NativeSymbols.IsBuiltInName(identifier.Text))
			Report(identifier, $"Cannot use '{identifier.Text}' as a name");
	}
	
	private void RejectReservedNames(List<TypeParameterNode> typeParameters)
	{
		foreach (var parameter in typeParameters)
			RejectReservedName(parameter.Identifier);
	}
	
	private void ReportUnexpected(Token token) => Report(token, token.Error ?? "Unexpected token");
	
	private void AddParseError(ParseException exception)
	{
		if (exception.IsNesting && reportedNesting)
			return;
		
		reportedNesting |= exception.IsNesting;
		Diagnostics.Add(exception.Diagnostic);
	}
	
	private bool MatchOnLine(ref int index, TokenType type)
	{
		if (!Peek(index, type))
			return false;
		
		if (!IsOnSameLine(index))
			Report(Tokens[index], $"Cannot start a line with '{Tokens[index].Text}'");
		
		index++;
		return true;
	}
	
	private void ReportExpected(int index, string expected, Token? opener = null)
	{
		var token = Tokens[Math.Min(index, Tokens.Length - 1)];
		Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation, token.Error ?? $"Expected {expected}")
		{
			Hints = token.Error is null && opener is { } o ? [$"To match the '{o.Text}' on line {o.Line}"] : []
		});
	}
	
	public override FileNode? Parse(ref int index)
	{
		var file = ParseFile(ref index);
		ReportLexicalErrors(0, Tokens.Length);
		return file;
	}
	
	private void RecordSite(int index, CompletionSite site)
	{
		if (Probe is not null && !AtEnd(index))
			Probe.RecordSite(Tokens[index], site);
	}
	
	private void RecordSites(int start, int end, CompletionSite site)
	{
		for (var i = start; i <= end; i++)
			RecordSite(i, site);
	}
	
	private void ReportLexicalErrors(int start, int end)
	{
		for (var i = start; i < end && i < Tokens.Length; i++)
		{
			if (_lexicalReported[i])
				continue;
			
			_lexicalReported[i] = true;
			var token = Tokens[i];
			if (token.Error is { } error &&
			    !Diagnostics.Any(d => d.SourceLocation == token.SourceLocation && d.Message == error))
				Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation, error));
			
			foreach (var location in FindHiddenCharacters(i))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location,
					$"Cannot use U+{(int)location.GetText()[0]:X4} in source"));
		}
	}
	
	private IEnumerable<SourceLocation> FindHiddenCharacters(int index)
	{
		var (source, range) = Tokens[index].SourceLocation;
		var start = index == 0 ? 0 : Tokens[index - 1].SourceLocation.Range.End;
		var end = Tokens[index].Type == TokenType.EndOfFile ? source.Length : range.End;
		for (var i = start; i < end; i++)
			if (Scanner.IsHiddenCharacter(source[i]))
				yield return new(source, new(i, i + 1));
	}
	
	private FileNode? ParseFile(ref int index)
	{
		// Setup
		var (source, range) = Tokens[0].SourceLocation;
		
		ModuleName moduleName = default;
		var declarations = new List<IDeclarationNode>();
		var imports = new List<ImportExpression>();
		
		var moduleNameAllowed = true;
		var hasModuleKeyword = false;
		var moduleNames = new List<SourceLocation>();
		var conditions = new List<Token>();
		SourceLocation? importCondition = null;
		var importsAllowed = false;
		var conditionAllowed = false;
		var isIncluded = true;
		int? errorsAfterDeclaration = null;
		
		while (!AtEnd(index))
		{
			RecordSite(index, CompletionSite.TopLevel);
			
			// Parse module name
			if (!IsVisibilityBlock(index, _topLevelContextualKeywords) && !StartsContextualName(index) &&
			    Match(ref index, _topLevelContextualKeywords, TokenType.KeywordMod))
			{
				hasModuleKeyword = true;
				var parsed = ParseModuleName(ref index);
				if (parsed is { } declared)
					moduleNames.Add(declared.SourceLocation);
				
				if (moduleNames.Count > 1 && parsed is not null)
				{
					const string message = "Cannot declare more than one module name";
					if (moduleNames.Count == 2)
						Report(moduleNames[0], message);
					
					Report(moduleNames[^1], message);
				}
				else if (moduleNameAllowed)
				{
					moduleNameAllowed = false;
					importsAllowed = true;
					conditionAllowed = true;
					if (parsed is { } name)
						moduleName = name;
				}
				else if (parsed is { } misplaced)
					Report(misplaced.SourceLocation, "Module name must precede all other declarations or imports");
				
				continue;
			}
			
			// Parse imports
			if (!StartsContextualName(index) &&
			    Match(ref index, out var useToken, _topLevelContextualKeywords, TokenType.KeywordUse))
			{
				if (Match(ref index, TokenType.KeywordWhen))
				{
					conditions.Add(useToken);
					if (conditions.Count > 1)
					{
						if (conditions.Count == 2)
							Report(conditions[0], "Cannot declare more than one 'use when'");
						
						Report(useToken, "Cannot declare more than one 'use when'");
					}
					else if (!conditionAllowed)
						Report(useToken, "'use when' must directly follow the module name");
					
					if (ParseCondition(ref index) is not { } condition)
						ResyncTopLevel(ref index);
					else if (IsOnSameLine(index))
					{
						ReportExpected(index, "'&&' or '||'");
						ResyncTopLevel(ref index);
					}
					else
					{
						if (isIncluded && !condition)
							MarkInactive(new(Tokens[index].SourceLocation.Range.Start,
								Tokens[^1].SourceLocation.Range.Start));
						
						isIncluded &= condition;
					}
					
					importCondition ??= new SourceLocation(source,
						useToken.SourceLocation.Range.Join(Tokens[index - 1].SourceLocation.Range));
					
					conditionAllowed = false;
					continue;
				}
				
				var precedesModuleName = moduleNameAllowed;
				conditionAllowed = false;
				moduleNameAllowed = false;
				var importStart = index;
				var errorsBefore = Diagnostics.ErrorCount;
				var import = ParseImportExpression(ref index);
				if (import is null)
					ResyncTopLevel(ref index);
				
				ReportLexicalErrors(importStart, index);
				if (import is { } parsed)
					imports.Add(parsed with
					{
						HasSyntaxErrors = Diagnostics.ErrorCount > errorsBefore,
						SourceLocation = new(source,
							useToken.SourceLocation.Range.Join(Tokens[index - 1].SourceLocation.Range))
					});
				
				if (!importsAllowed && !precedesModuleName)
					Report(useToken, "Imports must precede all other declarations");
				
				continue;
			}
			
			if (errorsAfterDeclaration == Diagnostics.ErrorCount && IsOnSameLine(index) &&
			    StartsTopLevelDeclaration(index))
				Report(Tokens[index], "Expected a new line before the next declaration");
			
			if (ParseTopLevelDeclaration(ref index, declarations, null))
			{
				errorsAfterDeclaration = Diagnostics.ErrorCount;
				moduleNameAllowed = false;
				importsAllowed = false;
				conditionAllowed = false;
				continue;
			}
			
			RecoverTopLevel(ref index, false);
		}
		
		if (moduleName == default)
		{
			if (!hasModuleKeyword)
				Report(Tokens[0], "All files must begin with a module name");
			
			return null;
		}
		
		// Combine range to include penultimate token (because last must be EOF)
		if (index > 1)
			range = range.Join(Tokens[index - 1].SourceLocation.Range);
		
		return new FileNode(new(source, range))
		{
			FileName = fileName,
			FullPath = fullPath,
			ModuleName = moduleName,
			Imports = imports.ToImmutableArray(),
			ImportCondition = importCondition,
			Declarations = declarations.ToImmutableArray(),
			BrokenStatements = _brokenStatements,
			BrokenDeclarations = _brokenDeclarations,
			DeclarationsWithSyntaxErrors = _declarationsWithErrors,
			DroppedNames = [.._droppedNames],
			InactiveCode =
			[
				.._inactiveRanges
					.OrderBy(static range => range.Start)
					.Select(range => new SourceLocation(source, range))
			],
			ContextualKeywords =
			[
				..ContextualKeywords
					.DistinctBy(static keyword => keyword.SourceLocation.Range.Start)
					.OrderBy(static keyword => keyword.SourceLocation.Range.Start)
			],
			IsExcluded = !isIncluded
		};
	}
	
	private bool ParseTopLevelDeclaration(ref int index, List<IDeclarationNode> declarations, Token? block,
		bool insideBraces = false)
	{
		var window = OpenErrorWindow(index);
		var first = declarations.Count;
		if (StartsContextualName(index))
			return RecordSyntaxErrors(ParseNamedDeclaration(ref index, declarations, block, insideBraces), declarations,
				first, window, index);
		
		var visibilityIndex = index;
		if (StartsVisibleExternals(index) &&
		    (Match(ref visibilityIndex, out var visibility, _topLevelContextualKeywords, _blockKeywords) ||
		     Match(ref visibilityIndex, out visibility, _topLevelContextualKeywords, TokenType.KeywordPub)) &&
		    Match(ref visibilityIndex, _topLevelContextualKeywords, TokenType.KeywordExt))
		{
			if (block is { } outer)
				Report(visibility, $"Cannot use '{visibility.Text}' in '{outer.Text}' blocks");
			
			var visibleStart = index;
			var visibleExt = Tokens[visibilityIndex - 1];
			index = visibilityIndex;
			var visibleBlock = block ?? visibility;
			if (ParseDeclaration(ref index, visibleExt, "external",
				    (ref i) => ParseExternalDeclarations(ref i, visibleBlock)) is { } visibleExternals)
				declarations.AddRange(visibleExternals);
			else
				SkipDeclaration(ref index, visibleStart, insideBraces);
			
			return true;
		}
		
		if (MatchVisibilityBlock(ref index, _topLevelContextualKeywords, out var keyword, out var openBrace) ||
		    MatchPubBlock(ref index, _topLevelContextualKeywords, out keyword, out openBrace))
		{
			if (block is { } outer)
				Report(keyword, $"Cannot use '{keyword.Text}' in '{outer.Text}' blocks");
			
			ParseTopLevelBlock(ref index, openBrace, block ?? keyword, declarations);
			return true;
		}
		
		if (Match(ref index, TokenType.KeywordWhen))
		{
			if (!ParseWhen(ref index, (ref i, open, isActive) =>
				    ParseTopLevelBlock(ref i, open, block, isActive ? declarations : [])))
				ResyncTopLevel(ref index, insideBraces);
			
			return true;
		}
		
		var insideBlock = insideBraces;
		if (FindUnnamedImplTarget(index) is var (target, end))
		{
			Report(target.SourceLocation, $"Cannot implement traits for '{target.SourceLocation.GetText()}'");
			index = end;
			ResyncTopLevel(ref index, insideBlock);
			return true;
		}
		
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
		
		var panicStart = index;
		if (Match(ref index, out var panicToken, TokenType.KeywordPanic))
		{
			if (ParseOperatorMember(ref index, panicToken, block) is { } panic)
				declarations.Add(panic);
			else
				SkipDeclaration(ref index, panicStart, insideBlock);
			
			return RecordSyntaxErrors(true, declarations, first, window, index);
		}
		
		return RecordSyntaxErrors(Peek(index, TokenType.OpOpenBracket)
			? ParseBlanketImpl(ref index, declarations, block, insideBraces)
			: ParseNamedDeclaration(ref index, declarations, block, insideBraces), declarations, first, window, index);
	}
	
	private (ITypeNode Target, int End)? FindUnnamedImplTarget(int index)
	{
		if (index > 0 && IsOnSameLine(index) || !StartsUnnamedType(index))
			return null;
		
		var end = index;
		ITypeNode target;
		try
		{
			target = new TypeParser(Tokens).Parse(ref end);
		}
		catch (ParseException)
		{
			return null;
		}
		
		var implIndex = end + 1;
		return Peek(end, TokenType.OpColon) &&
		       Match(ref implIndex, _topLevelContextualKeywords, TokenType.KeywordImpl)
			? (target, implIndex)
			: null;
	}
	
	private bool StartsUnnamedType(int index) =>
		TokenSets.BorrowKeywords.Contains(Tokens[index].Type) ||
		Tokens[index].Type is TokenType.KeywordDyn or TokenType.KeywordFun ||
		Tokens[index].Type is TokenType.KeywordRef or TokenType.KeywordOwn && Peek(index + 1, TokenType.KeywordFun) ||
		Tokens[index].Text == "ext" && Peek(index + 1, TokenType.KeywordFun);
	
	private bool StartsContextualName(int index) =>
		!AtEnd(index + 1) && Tokens[index] is { Type: TokenType.Identifier } token &&
		_topLevelContextualKeywords.ContainsKey(token.Text) && IsOnSameLine(index + 1) &&
		(Peek(index + 1, TokenType.OpColon) || Peek(index + 1, TokenType.OpOpenBracket));
}