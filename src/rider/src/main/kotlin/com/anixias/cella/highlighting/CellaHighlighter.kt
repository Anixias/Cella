package com.anixias.cella.highlighting

import com.anixias.cella.lexer.CelpLexer
import com.anixias.cella.lexer.CelpToken
import com.anixias.cella.lexer.CelpTokenTypes
import com.anixias.cella.lexer.CellaLexer
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighterBase
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.psi.tree.IElementType

class CellaHighlighter : SyntaxHighlighterBase() {
	override fun getHighlightingLexer() = CellaLexer()
	
	override fun getTokenHighlights(tokenType: IElementType?): Array<TextAttributesKey> =
		pack(CellaTokenTypes.tokenOf(tokenType)?.let(::colorOf))
	
	companion object {
		fun colorOf(token: CellaToken): TextAttributesKey? = when (token) {
			CellaToken.DECLARATION_KEYWORD -> CellaColors.DECLARATION_KEYWORD
			CellaToken.CONTROL_KEYWORD -> CellaColors.CONTROL_KEYWORD
			CellaToken.MODIFIER_KEYWORD -> CellaColors.MODIFIER_KEYWORD
			CellaToken.VALUE_KEYWORD -> CellaColors.VALUE_KEYWORD
			CellaToken.OPERATOR_KEYWORD -> CellaColors.OPERATOR_KEYWORD
			CellaToken.BUILTIN_TYPE -> CellaColors.BUILTIN_TYPE
			CellaToken.LITERAL_SUFFIX -> CellaColors.LITERAL_SUFFIX
			CellaToken.INTEGER, CellaToken.FLOAT -> CellaColors.NUMBER
			CellaToken.STRING -> CellaColors.STRING
			CellaToken.CHAR -> CellaColors.CHAR
			CellaToken.STRING_ESCAPE -> CellaColors.STRING_ESCAPE
			CellaToken.INVALID_STRING_ESCAPE -> CellaColors.INVALID_STRING_ESCAPE
			CellaToken.FORMAT_SPEC -> CellaColors.FORMAT_SPEC
			CellaToken.INTERPOLATION_START, CellaToken.INTERPOLATION_END, CellaToken.FORMAT_SEPARATOR ->
				CellaColors.INTERPOLATION
			CellaToken.LINE_COMMENT -> CellaColors.LINE_COMMENT
			CellaToken.BLOCK_COMMENT -> CellaColors.BLOCK_COMMENT
			CellaToken.IDENTIFIER -> CellaColors.IDENTIFIER
			CellaToken.LEFT_BRACE, CellaToken.RIGHT_BRACE -> CellaColors.BRACES
			CellaToken.LEFT_PAREN, CellaToken.RIGHT_PAREN -> CellaColors.PARENTHESES
			CellaToken.LEFT_BRACKET, CellaToken.RIGHT_BRACKET -> CellaColors.BRACKETS
			CellaToken.COMMA -> CellaColors.COMMA
			CellaToken.DOT -> CellaColors.DOT
			CellaToken.COLON, CellaToken.ARROW, CellaToken.OPERATOR -> CellaColors.OPERATOR
			CellaToken.BAD_CHARACTER -> CellaColors.BAD_CHARACTER
			CellaToken.WHITESPACE -> null
		}
	}
}

class CellaHighlighterFactory : SyntaxHighlighterFactory() {
	override fun getSyntaxHighlighter(project: Project?, virtualFile: VirtualFile?): SyntaxHighlighter =
		CellaHighlighter()
}

class CelpHighlighter : SyntaxHighlighterBase() {
	override fun getHighlightingLexer() = CelpLexer()
	
	override fun getTokenHighlights(tokenType: IElementType?): Array<TextAttributesKey> =
		pack(CelpTokenTypes.tokenOf(tokenType)?.let(::colorOf))
	
	private fun colorOf(token: CelpToken): TextAttributesKey? = when (token) {
		CelpToken.COMMENT -> CellaColors.PROJECT_COMMENT
		CelpToken.SETTING -> CellaColors.PROJECT_SETTING
		CelpToken.KEY -> CellaColors.PROJECT_KEY
		CelpToken.STRING -> CellaColors.STRING
		CelpToken.STRING_ESCAPE -> CellaColors.STRING_ESCAPE
		CelpToken.INVALID_STRING_ESCAPE -> CellaColors.INVALID_STRING_ESCAPE
		CelpToken.NUMBER -> CellaColors.NUMBER
		CelpToken.BOOLEAN -> CellaColors.PROJECT_BOOLEAN
		CelpToken.LEFT_BRACKET, CelpToken.RIGHT_BRACKET -> CellaColors.BRACKETS
		CelpToken.LEFT_BRACE, CelpToken.RIGHT_BRACE -> CellaColors.BRACES
		CelpToken.EQUALS -> CellaColors.PROJECT_EQUALS
		CelpToken.COMMA -> CellaColors.COMMA
		CelpToken.DOT -> CellaColors.DOT
		CelpToken.BAD_CHARACTER -> CellaColors.BAD_CHARACTER
		CelpToken.WHITESPACE -> null
	}
}

class CelpHighlighterFactory : SyntaxHighlighterFactory() {
	override fun getSyntaxHighlighter(project: Project?, virtualFile: VirtualFile?): SyntaxHighlighter =
		CelpHighlighter()
}
