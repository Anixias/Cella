package com.anixias.cella.highlighting

import com.intellij.openapi.editor.DefaultLanguageHighlighterColors as Default
import com.intellij.openapi.editor.HighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.editor.colors.TextAttributesKey.createTextAttributesKey
import com.jetbrains.rider.languages.fileTypes.csharp.RiderLanguageTextAttributeKeys as Rider

object CellaColors {
	val DECLARATION_KEYWORD = key("DECLARATION_KEYWORD", Rider.KEYWORD)
	val CONTROL_KEYWORD = key("CONTROL_KEYWORD", Rider.CONTROL_FLOW_KEYWORD)
	val MODIFIER_KEYWORD = key("MODIFIER_KEYWORD", Rider.KEYWORD)
	val VALUE_KEYWORD = key("VALUE_KEYWORD", Rider.KEYWORD)
	val OPERATOR_KEYWORD = key("OPERATOR_KEYWORD", Rider.KEYWORD)
	val BUILTIN_TYPE = key("BUILTIN_TYPE", Rider.BUILTIN_TYPE_KEYWORD)
	val LITERAL_SUFFIX = key("LITERAL_SUFFIX", BUILTIN_TYPE)
	val NUMBER = key("NUMBER", Rider.NUMBER)
	val STRING = key("STRING", Rider.STRING)
	val CHAR = key("CHAR", STRING)
	val STRING_ESCAPE = key("STRING_ESCAPE", Default.VALID_STRING_ESCAPE)
	val INVALID_STRING_ESCAPE = key("INVALID_STRING_ESCAPE", Default.INVALID_STRING_ESCAPE)
	val FORMAT_SPEC = key("FORMAT_SPEC", Rider.FORMAT_STRING_ITEM)
	val INTERPOLATION = key("INTERPOLATION", Rider.BRACES)
	val LINE_COMMENT = key("LINE_COMMENT", Rider.LINE_COMMENT)
	val BLOCK_COMMENT = key("BLOCK_COMMENT", Rider.BLOCK_COMMENT)
	val IDENTIFIER = key("IDENTIFIER", Rider.IDENTIFIER)
	val BRACES = key("BRACES", Rider.BRACES)
	val PARENTHESES = key("PARENTHESES", Rider.PARENTHESES)
	val BRACKETS = key("BRACKETS", Rider.BRACKETS)
	val COMMA = key("COMMA", Rider.COMMA)
	val OPERATOR = key("OPERATOR", Rider.OPERATOR_SIGN)
	val DOT = key("DOT", OPERATOR)
	val BAD_CHARACTER = key("BAD_CHARACTER", HighlighterColors.BAD_CHARACTER)
	
	val PROJECT_SETTING = key("PROJECT_SETTING", Default.INSTANCE_FIELD)
	val PROJECT_KEY = key("PROJECT_KEY", Default.IDENTIFIER)
	val PROJECT_BOOLEAN = key("PROJECT_BOOLEAN", Default.KEYWORD)
	val PROJECT_COMMENT = key("PROJECT_COMMENT", Default.LINE_COMMENT)
	val PROJECT_EQUALS = key("PROJECT_EQUALS", Default.OPERATION_SIGN)
	
	val DROP_TYPE = key("DROP_TYPE", Rider.CLASS)
	val RECORD = key("RECORD", Rider.STRUCT)
	val REF_RECORD = key("REF_RECORD", Rider.STRUCT)
	val ENUM = key("ENUM", Rider.ENUM)
	val TRAIT = key("TRAIT", Rider.INTERFACE)
	val TYPE_PARAMETER = key("TYPE_PARAMETER", Rider.TYPE_PARAMETER)
	val TYPE = key("TYPE", Rider.CLASS)
	val FUNCTION = key("FUNCTION", Rider.FUNCTION)
	val METHOD = key("METHOD", Rider.METHOD_DECLARATION)
	val PARAMETER = key("PARAMETER", Rider.PARAMETER)
	val LOCAL_VARIABLE = key("LOCAL_VARIABLE", Rider.MUTABLE_LOCAL_VARIABLE)
	val LOCAL_VALUE = key("LOCAL_VALUE", Rider.LOCAL_VARIABLE)
	val CONSTANT = key("CONSTANT", Rider.CONSTANT)
	val GLOBAL_VARIABLE = key("GLOBAL_VARIABLE", Rider.STATIC_FIELD)
	val FIELD = key("FIELD", Rider.FIELD)
	val ENUM_CASE = key("ENUM_CASE", Rider.STATIC_FIELD)
	val MODULE = key("MODULE", Rider.NAMESPACE)
	
	private fun key(name: String, fallback: TextAttributesKey) = createTextAttributesKey("CELLA_$name", fallback)
}
