package com.anixias.cella.highlighting

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors as Default
import com.intellij.openapi.editor.HighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.editor.colors.TextAttributesKey.createTextAttributesKey

object CellaColors {
	val DECLARATION_KEYWORD = key("DECLARATION_KEYWORD", Default.KEYWORD)
	val CONTROL_KEYWORD = key("CONTROL_KEYWORD", Default.KEYWORD)
	val MODIFIER_KEYWORD = key("MODIFIER_KEYWORD", Default.KEYWORD)
	val VALUE_KEYWORD = key("VALUE_KEYWORD", Default.KEYWORD)
	val OPERATOR_KEYWORD = key("OPERATOR_KEYWORD", Default.KEYWORD)
	val BUILTIN_TYPE = key("BUILTIN_TYPE", Default.KEYWORD)
	val LITERAL_SUFFIX = key("LITERAL_SUFFIX", BUILTIN_TYPE)
	val NUMBER = key("NUMBER", Default.NUMBER)
	val STRING = key("STRING", Default.STRING)
	val CHAR = key("CHAR", STRING)
	val STRING_ESCAPE = key("STRING_ESCAPE", Default.VALID_STRING_ESCAPE)
	val INVALID_STRING_ESCAPE = key("INVALID_STRING_ESCAPE", Default.INVALID_STRING_ESCAPE)
	val FORMAT_SPEC = key("FORMAT_SPEC", Default.VALID_STRING_ESCAPE)
	val INTERPOLATION = key("INTERPOLATION", Default.BRACES)
	val LINE_COMMENT = key("LINE_COMMENT", Default.LINE_COMMENT)
	val BLOCK_COMMENT = key("BLOCK_COMMENT", Default.BLOCK_COMMENT)
	val IDENTIFIER = key("IDENTIFIER", Default.IDENTIFIER)
	val BRACES = key("BRACES", Default.BRACES)
	val PARENTHESES = key("PARENTHESES", Default.PARENTHESES)
	val BRACKETS = key("BRACKETS", Default.BRACKETS)
	val COMMA = key("COMMA", Default.COMMA)
	val DOT = key("DOT", Default.DOT)
	val OPERATOR = key("OPERATOR", Default.OPERATION_SIGN)
	val BAD_CHARACTER = key("BAD_CHARACTER", HighlighterColors.BAD_CHARACTER)
	
	val PROJECT_SETTING = key("PROJECT_SETTING", Default.INSTANCE_FIELD)
	val PROJECT_KEY = key("PROJECT_KEY", Default.IDENTIFIER)
	val PROJECT_BOOLEAN = key("PROJECT_BOOLEAN", Default.KEYWORD)
	val PROJECT_COMMENT = key("PROJECT_COMMENT", Default.LINE_COMMENT)
	val PROJECT_EQUALS = key("PROJECT_EQUALS", Default.OPERATION_SIGN)
	
	private fun key(name: String, fallback: TextAttributesKey) = createTextAttributesKey("CELLA_$name", fallback)
}
