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
	
	private bool skippedToEnd;
	private bool reachedDeclaration;
	
	private static Dictionary<string, TokenType> BuildContextualKeywords(params IEnumerable<TokenType> tokenTypes) =>
		tokenTypes.ToDictionary(static t => t.Representation);
	
	public DiagnosticList Diagnostics { get; } = new();
	
	private bool allowsMissingBodies;
	private bool allowsPublicMembers;
	
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
		if (identifier.Text == "_" || NativeSymbols.IsBuiltInName(identifier.Text))
			Report(identifier, $"Cannot use '{identifier.Text}' as a name");
	}
	
	private void RejectReservedNames(List<TypeParameterNode> typeParameters)
	{
		foreach (var parameter in typeParameters)
			RejectReservedName(parameter.Identifier);
	}
	
	private void ReportUnexpected(Token token) => Report(token, token.Error ?? "Unexpected token");
	
	private bool IsReported(Token token) =>
		Diagnostics.Any(diagnostic => diagnostic.SourceLocation == token.SourceLocation);
	
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
		ReportLexicalErrors();
		return file;
	}
	
	private void ReportLexicalErrors()
	{
		var reported = Diagnostics.Select(static d => (d.SourceLocation, d.Message)).ToHashSet();
		foreach (var token in Tokens)
		{
			if (token.Error is { } error && reported.Add((token.SourceLocation, error)))
				Diagnostics.Add(new(DiagnosticSeverity.Error, token.SourceLocation, error));
		}
		
		var source = Tokens[0].SourceLocation.Source;
		var located = reported.Select(static entry => entry.SourceLocation).ToHashSet();
		for (var i = 0; i < source.Length; i++)
		{
			SourceLocation location = new(source, new(i, i + 1));
			if (IsHiddenCharacter(source[i]) && !located.Contains(location))
				Diagnostics.Add(new(DiagnosticSeverity.Error, location, $"Cannot use U+{(int)source[i]:X4} in source"));
		}
	}
	
	private static bool IsHiddenCharacter(char character) =>
		character is '\u0085' or '\u2028' or '\u2029' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';
	
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
		var importsAllowed = false;
		var conditionAllowed = false;
		var isIncluded = true;
		
		while (!AtEnd(index))
		{
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
						isIncluded &= condition;
					
					conditionAllowed = false;
					continue;
				}
				
				var precedesModuleName = moduleNameAllowed;
				conditionAllowed = false;
				moduleNameAllowed = false;
				if (ParseImportExpression(ref index) is { } import)
					imports.Add(import);
				else
					ResyncTopLevel(ref index);
				
				if (!importsAllowed && !precedesModuleName)
					Report(useToken, "Imports must precede all other declarations");
				
				continue;
			}
			
			if (ParseTopLevelDeclaration(ref index, declarations, null))
			{
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
	
	private bool ParseTopLevelDeclaration(ref int index, List<IDeclarationNode> declarations, Token? block,
		bool insideBraces = false)
	{
		if (StartsContextualName(index))
			return ParseNamedDeclaration(ref index, declarations, block, insideBraces);
		
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
			
			return true;
		}
		
		if (Peek(index, TokenType.OpOpenBracket))
			return ParseBlanketImpl(ref index, declarations, block, insideBraces);
		
		return ParseNamedDeclaration(ref index, declarations, block, insideBraces);
	}
	
	private bool StartsContextualName(int index) =>
		!AtEnd(index + 1) && Tokens[index] is { Type: TokenType.Identifier } token &&
		_topLevelContextualKeywords.ContainsKey(token.Text) && IsOnSameLine(index + 1) &&
		(Peek(index + 1, TokenType.OpColon) || Peek(index + 1, TokenType.OpOpenBracket));
}