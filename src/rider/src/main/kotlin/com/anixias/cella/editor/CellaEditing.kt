package com.anixias.cella.editor

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CelpToken
import com.anixias.cella.lexer.CelpTokenTypes
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.codeInsight.editorActions.SimpleTokenSetQuoteHandler
import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.lang.BracePair
import com.intellij.lang.Commenter
import com.intellij.lang.Language
import com.intellij.lang.PairedBraceMatcher
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileTypes.FileType
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.TokenType
import com.intellij.psi.impl.source.codeStyle.lineIndent.JavaLikeLangLineIndentProvider
import com.intellij.psi.impl.source.codeStyle.SemanticEditorPosition
import com.intellij.psi.tree.IElementType
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

class CellaBraceMatcher : PairedBraceMatcher {
	private val pairs = arrayOf(
		BracePair(CellaTokenTypes[CellaToken.LEFT_BRACE], CellaTokenTypes[CellaToken.RIGHT_BRACE], true),
		BracePair(CellaTokenTypes[CellaToken.LEFT_PAREN], CellaTokenTypes[CellaToken.RIGHT_PAREN], false),
		BracePair(CellaTokenTypes[CellaToken.LEFT_BRACKET], CellaTokenTypes[CellaToken.RIGHT_BRACKET], false),
		BracePair(CellaTokenTypes[CellaToken.INTERPOLATION_START], CellaTokenTypes[CellaToken.INTERPOLATION_END], false)
	)
	
	override fun getPairs() = pairs
	override fun isPairedBracesAllowedBeforeType(lbraceType: IElementType, contextType: IElementType?) = true
	override fun getCodeConstructStart(file: PsiFile?, openingBraceOffset: Int) = openingBraceOffset
}

class CelpBraceMatcher : PairedBraceMatcher {
	private val pairs = arrayOf(
		BracePair(CelpTokenTypes[CelpToken.LEFT_BRACKET], CelpTokenTypes[CelpToken.RIGHT_BRACKET], false),
		BracePair(CelpTokenTypes[CelpToken.LEFT_BRACE], CelpTokenTypes[CelpToken.RIGHT_BRACE], false)
	)
	
	override fun getPairs() = pairs
	override fun isPairedBracesAllowedBeforeType(lbraceType: IElementType, contextType: IElementType?) = true
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

class CellaLineIndentProvider : JavaLikeLangLineIndentProvider() {
	override fun mapType(tokenType: IElementType): SemanticEditorPosition.SyntaxElement? = when {
		tokenType == TokenType.WHITE_SPACE -> JavaLikeElement.Whitespace
		else -> when (CellaTokenTypes.tokenOf(tokenType)) {
			CellaToken.LEFT_BRACE -> JavaLikeElement.BlockOpeningBrace
			CellaToken.RIGHT_BRACE -> JavaLikeElement.BlockClosingBrace
			CellaToken.LEFT_PAREN -> JavaLikeElement.LeftParenthesis
			CellaToken.RIGHT_PAREN -> JavaLikeElement.RightParenthesis
			CellaToken.LEFT_BRACKET -> JavaLikeElement.ArrayOpeningBracket
			CellaToken.RIGHT_BRACKET -> JavaLikeElement.ArrayClosingBracket
			CellaToken.COMMA -> JavaLikeElement.Comma
			CellaToken.LINE_COMMENT -> JavaLikeElement.LineComment
			CellaToken.BLOCK_COMMENT -> JavaLikeElement.BlockComment
			else -> null
		}
	}
	
	override fun isSuitableForLanguage(language: Language) = language.isKindOf(CellaLanguage)
}

class CellaTypedHandler : TypedHandlerDelegate() {
	override fun beforeCharTyped(c: Char, project: Project, editor: Editor, file: PsiFile, fileType: FileType): Result {
		if (c != '}' || file.language != CellaLanguage)
			return Result.CONTINUE
		
		val offset = editor.caretModel.offset
		val text = editor.document.charsSequence
		if (offset < text.length && text[offset] == '}' && isBalanced(text)) {
			editor.caretModel.moveToOffset(offset + 1)
			return Result.STOP
		}
		
		return Result.CONTINUE
	}
	
	override fun charTyped(c: Char, project: Project, editor: Editor, file: PsiFile): Result {
		if (c != '{' || file.language != CellaLanguage)
			return Result.CONTINUE
		
		val offset = editor.caretModel.offset
		val text = editor.document.charsSequence
		val next = if (offset < text.length) text[offset] else '\n'
		if (next.isWhitespace() || next in ")]}")
			editor.document.insertString(offset, "}")
		
		return Result.CONTINUE
	}
	
	private fun isBalanced(text: CharSequence): Boolean {
		var depth = 0
		for (c in text) {
			if (c == '{')
				depth++
			else if (c == '}')
				depth--
		}
		
		return depth <= 0
	}
}
