package com.anixias.cella.editor

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CelpToken
import com.anixias.cella.lexer.CelpTokenTypes
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.application.options.CodeStyle
import com.intellij.codeInsight.editorActions.SimpleTokenSetQuoteHandler
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.lang.BracePair
import com.intellij.lang.Commenter
import com.intellij.lang.Language
import com.intellij.lang.PairedBraceMatcher
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.codeStyle.lineIndent.LineIndentProvider
import com.intellij.psi.tree.IElementType
import com.intellij.spellchecker.BundledDictionaryProvider
import com.intellij.spellchecker.tokenizer.SpellcheckingStrategy
import com.intellij.spellchecker.tokenizer.Tokenizer

class CellaCommenter : Commenter {
	override fun getLineCommentPrefix() = "//"
	override fun getBlockCommentPrefix() = "/*"
	override fun getBlockCommentSuffix() = "*/"
	override fun getCommentedBlockCommentPrefix(): String? = null
	override fun getCommentedBlockCommentSuffix(): String? = null
}

class CelpCommenter : Commenter {
	override fun getLineCommentPrefix() = "#"
	override fun getBlockCommentPrefix(): String? = null
	override fun getBlockCommentSuffix(): String? = null
	override fun getCommentedBlockCommentPrefix(): String? = null
	override fun getCommentedBlockCommentSuffix(): String? = null
}

private val cellaWords = setOf(
	CellaToken.IDENTIFIER, CellaToken.DECLARATION_KEYWORD, CellaToken.CONTROL_KEYWORD, CellaToken.MODIFIER_KEYWORD,
	CellaToken.VALUE_KEYWORD, CellaToken.OPERATOR_KEYWORD, CellaToken.BUILTIN_TYPE, CellaToken.INTEGER, CellaToken.FLOAT
)

private val celpWords = setOf(CelpToken.KEY, CelpToken.NUMBER, CelpToken.BOOLEAN)

class CellaBraceMatcher : PairedBraceMatcher {
	private val pairs = arrayOf(
		BracePair(CellaTokenTypes[CellaToken.LEFT_BRACE], CellaTokenTypes[CellaToken.RIGHT_BRACE], true),
		BracePair(CellaTokenTypes[CellaToken.LEFT_PAREN], CellaTokenTypes[CellaToken.RIGHT_PAREN], false),
		BracePair(CellaTokenTypes[CellaToken.LEFT_BRACKET], CellaTokenTypes[CellaToken.RIGHT_BRACKET], false),
		BracePair(CellaTokenTypes[CellaToken.INTERPOLATION_START], CellaTokenTypes[CellaToken.INTERPOLATION_END], false)
	)
	
	override fun getPairs() = pairs
	override fun isPairedBracesAllowedBeforeType(lbraceType: IElementType, contextType: IElementType?) =
		CellaTokenTypes.tokenOf(contextType) !in cellaWords
	override fun getCodeConstructStart(file: PsiFile?, openingBraceOffset: Int) = openingBraceOffset
}

class CelpBraceMatcher : PairedBraceMatcher {
	private val pairs = arrayOf(
		BracePair(CelpTokenTypes[CelpToken.LEFT_BRACKET], CelpTokenTypes[CelpToken.RIGHT_BRACKET], false),
		BracePair(CelpTokenTypes[CelpToken.LEFT_BRACE], CelpTokenTypes[CelpToken.RIGHT_BRACE], false)
	)
	
	override fun getPairs() = pairs
	override fun isPairedBracesAllowedBeforeType(lbraceType: IElementType, contextType: IElementType?) =
		CelpTokenTypes.tokenOf(contextType) !in celpWords
	override fun getCodeConstructStart(file: PsiFile?, openingBraceOffset: Int) = openingBraceOffset
}

class CellaQuoteHandler : SimpleTokenSetQuoteHandler(CellaTokenTypes.STRINGS)

class CelpQuoteHandler : SimpleTokenSetQuoteHandler(CelpTokenTypes.STRINGS)

class CellaSpellcheckingStrategy : SpellcheckingStrategy() {
	private val checked = setOf(CellaToken.LINE_COMMENT, CellaToken.BLOCK_COMMENT, CellaToken.STRING)
	
	override fun getTokenizer(element: PsiElement): Tokenizer<*> =
		if (CellaTokenTypes.tokenOf(element.node?.elementType) in checked) TEXT_TOKENIZER else EMPTY_TOKENIZER
}

class CelpSpellcheckingStrategy : SpellcheckingStrategy() {
	override fun getTokenizer(element: PsiElement): Tokenizer<*> =
		if (CelpTokenTypes.tokenOf(element.node?.elementType) == CelpToken.COMMENT) TEXT_TOKENIZER else EMPTY_TOKENIZER
}

private fun indentUnit(project: Project, document: Document): String {
	val options = CodeStyle.getIndentOptions(project, document)
	return if (options.USE_TAB_CHARACTER) "\t" else " ".repeat(options.INDENT_SIZE)
}

class CellaLineIndentProvider : LineIndentProvider {
	override fun getLineIndent(project: Project, editor: Editor, language: Language?, offset: Int) =
		CellaIndenter.indentAt(editor.document.immutableCharSequence, offset, indentUnit(project, editor.document))
	
	override fun isSuitableFor(language: Language?) = language?.isKindOf(CellaLanguage) == true
}

class CellaBraceIndentHandler : TypedHandlerDelegate() {
	override fun charTyped(c: Char, project: Project, editor: Editor, file: PsiFile): Result {
		if (file.language != CellaLanguage || c != '{' && c != '}')
			return Result.CONTINUE
		
		val document = editor.document
		val text = document.immutableCharSequence
		val line = document.getLineNumber(editor.caretModel.offset)
		val lineStart = document.getLineStartOffset(line)
		var contentStart = lineStart
		while (text[contentStart] == ' ' || text[contentStart] == '\t')
			contentStart++
		
		if (text[contentStart] != c || contentStart + 1 != editor.caretModel.offset)
			return Result.CONTINUE
		
		val indent = CellaIndenter.indentAt(text, lineStart, indentUnit(project, document)) ?: return Result.CONTINUE
		if (text.subSequence(lineStart, contentStart).toString() != indent)
			document.replaceString(lineStart, contentStart, indent)
		
		return Result.CONTINUE
	}
}

class CellaDictionaryProvider : BundledDictionaryProvider {
	override fun getBundledDictionaries() = arrayOf("/dictionaries/cella.dic")
}
