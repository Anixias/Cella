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
	
	val DROP_TYPE = key("DROP_TYPE", rider("CSHARP_CLASS_IDENTIFIER"))
	val RECORD = key("RECORD", rider("STRUCT_IDENTIFIER"))
	val REF_RECORD = key("REF_RECORD", rider("STRUCT_IDENTIFIER"))
	val ENUM = key("ENUM", rider("ENUM_IDENTIFIER"))
	val TRAIT = key("TRAIT", rider("CSHARP_INTERFACE_IDENTIFIER"))
	val TYPE_PARAMETER = key("TYPE_PARAMETER", rider("TYPE_PARAMETER_IDENTIFIER"))
	val TYPE = key("TYPE", rider("CSHARP_CLASS_IDENTIFIER"))
	val FUNCTION = key("FUNCTION", rider("CSHARP_METHOD_IDENTIFIER"))
	val METHOD = key("METHOD", rider("CSHARP_METHOD_IDENTIFIER"))
	val PARAMETER = key("PARAMETER", rider("CSHARP_PARAMETER_IDENTIFIER"))
	val LOCAL_VARIABLE = key("LOCAL_VARIABLE", rider("CSHARP_MUTABLE_LOCAL_VARIABLE_IDENTIFIER"))
	val LOCAL_VALUE = key("LOCAL_VALUE", rider("CSHARP_LOCAL_VARIABLE_IDENTIFIER"))
	val CONSTANT = key("CONSTANT", rider("CSHARP_CONSTANT_IDENTIFIER"))
	val GLOBAL_VARIABLE = key("GLOBAL_VARIABLE", rider("CSHARP_STATIC_FIELD_IDENTIFIER"))
	val FIELD = key("FIELD", rider("CSHARP_FIELD_IDENTIFIER"))
	val ENUM_CASE = key("ENUM_CASE", rider("CSHARP_STATIC_FIELD_IDENTIFIER"))
	
	private fun key(name: String, fallback: TextAttributesKey) = createTextAttributesKey("CELLA_$name", fallback)
	
	private fun rider(name: String) = TextAttributesKey.find("ReSharper.$name")
}
