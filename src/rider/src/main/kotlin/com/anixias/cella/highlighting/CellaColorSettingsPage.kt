package com.anixias.cella.highlighting

import com.anixias.cella.CellaIcons
import com.intellij.openapi.options.colors.AttributesDescriptor
import com.intellij.openapi.options.colors.ColorDescriptor
import com.intellij.openapi.options.colors.ColorSettingsPage

class CellaColorSettingsPage : ColorSettingsPage {
	private val descriptors = arrayOf(
		AttributesDescriptor("Keywords//Declarations", CellaColors.DECLARATION_KEYWORD),
		AttributesDescriptor("Keywords//Control flow", CellaColors.CONTROL_KEYWORD),
		AttributesDescriptor("Keywords//Modifiers", CellaColors.MODIFIER_KEYWORD),
		AttributesDescriptor("Keywords//Values", CellaColors.VALUE_KEYWORD),
		AttributesDescriptor("Keywords//Operators", CellaColors.OPERATOR_KEYWORD),
		AttributesDescriptor("Keywords//Built-in types", CellaColors.BUILTIN_TYPE),
		AttributesDescriptor("Literals//Number", CellaColors.NUMBER),
		AttributesDescriptor("Literals//Suffix", CellaColors.LITERAL_SUFFIX),
		AttributesDescriptor("Literals//String", CellaColors.STRING),
		AttributesDescriptor("Literals//Character", CellaColors.CHAR),
		AttributesDescriptor("Literals//Escape sequence", CellaColors.STRING_ESCAPE),
		AttributesDescriptor("Literals//Invalid escape sequence", CellaColors.INVALID_STRING_ESCAPE),
		AttributesDescriptor("Literals//Interpolation", CellaColors.INTERPOLATION),
		AttributesDescriptor("Literals//Format", CellaColors.FORMAT_SPEC),
		AttributesDescriptor("Comments//Line comment", CellaColors.LINE_COMMENT),
		AttributesDescriptor("Comments//Block comment", CellaColors.BLOCK_COMMENT),
		AttributesDescriptor("Identifier", CellaColors.IDENTIFIER),
		AttributesDescriptor("Module", CellaColors.MODULE),
		AttributesDescriptor("Punctuation//Braces", CellaColors.BRACES),
		AttributesDescriptor("Punctuation//Parentheses", CellaColors.PARENTHESES),
		AttributesDescriptor("Punctuation//Brackets", CellaColors.BRACKETS),
		AttributesDescriptor("Punctuation//Comma", CellaColors.COMMA),
		AttributesDescriptor("Punctuation//Dot", CellaColors.DOT),
		AttributesDescriptor("Punctuation//Colon", CellaColors.COLON),
		AttributesDescriptor("Punctuation//Arrow", CellaColors.ARROW),
		AttributesDescriptor("Punctuation//Operator", CellaColors.OPERATOR),
		AttributesDescriptor("Bad character", CellaColors.BAD_CHARACTER),
		AttributesDescriptor("Types//Drop type", CellaColors.DROP_TYPE),
		AttributesDescriptor("Types//Record", CellaColors.RECORD),
		AttributesDescriptor("Types//Ref record", CellaColors.REF_RECORD),
		AttributesDescriptor("Types//Enum", CellaColors.ENUM),
		AttributesDescriptor("Types//Trait", CellaColors.TRAIT),
		AttributesDescriptor("Types//Type parameter", CellaColors.TYPE_PARAMETER),
		AttributesDescriptor("Types//Other type", CellaColors.TYPE),
		AttributesDescriptor("Functions//Function", CellaColors.FUNCTION),
		AttributesDescriptor("Functions//Method", CellaColors.METHOD),
		AttributesDescriptor("Variables//Parameter", CellaColors.PARAMETER),
		AttributesDescriptor("Variables//Local var", CellaColors.LOCAL_VARIABLE),
		AttributesDescriptor("Variables//Local val", CellaColors.LOCAL_VALUE),
		AttributesDescriptor("Variables//Constant", CellaColors.CONSTANT),
		AttributesDescriptor("Variables//Global var", CellaColors.GLOBAL_VARIABLE),
		AttributesDescriptor("Members//Field", CellaColors.FIELD),
		AttributesDescriptor("Members//Property", CellaColors.PROPERTY),
		AttributesDescriptor("Members//Enum case", CellaColors.ENUM_CASE)
	)
	
	private val tags = mapOf(
		"drop" to CellaColors.DROP_TYPE,
		"record" to CellaColors.RECORD,
		"ref" to CellaColors.REF_RECORD,
		"enum" to CellaColors.ENUM,
		"trait" to CellaColors.TRAIT,
		"typeParameter" to CellaColors.TYPE_PARAMETER,
		"function" to CellaColors.FUNCTION,
		"method" to CellaColors.METHOD,
		"parameter" to CellaColors.PARAMETER,
		"var" to CellaColors.LOCAL_VARIABLE,
		"val" to CellaColors.LOCAL_VALUE,
		"constant" to CellaColors.CONSTANT,
		"global" to CellaColors.GLOBAL_VARIABLE,
		"field" to CellaColors.FIELD,
		"property" to CellaColors.PROPERTY,
		"case" to CellaColors.ENUM_CASE,
		"declaration" to CellaColors.DECLARATION_KEYWORD,
		"modifier" to CellaColors.MODIFIER_KEYWORD,
		"module" to CellaColors.MODULE
	)
	
	override fun getDisplayName() = "Cella"
	override fun getIcon() = CellaIcons.FILE
	override fun getHighlighter() = CellaHighlighter()
	override fun getAttributeDescriptors() = descriptors
	override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY
	override fun getAdditionalHighlightingTagToDescriptorMap() = tags
	
	override fun getDemoText() = """
		<declaration>mod</declaration> <module>demo</module>

		<declaration>use</declaration> <module>std</module>.<module>io</module>

		/* Shapes */
		<enum>Shape</enum>: <declaration>enum</declaration>
		{
			<case>circle</case>
			<case>square</case>
		}

		<record>Point</record>: <declaration>rec</declaration>
		{
			<field>x</field>: i32

			<method>length</method>: fun(imm self) -> f64 => 1.5e3

			<property>doubled</property>: get(imm self) -> i32 => self.<field>x</field> * 2
		}

		<ref>View</ref>: ref <declaration>rec</declaration>
		{
			<field>point</field>: imm[<record>Point</record>]
		}

		<drop>Handle</drop>: <declaration>rec</declaration>
		{
			<field>id</field>: i32

			drop: <declaration>op</declaration> { }
		}

		<trait>Sized</trait>: <declaration>trait</declaration>
		{
			<method>size</method>: fun(imm self) -> usize
		}

		<global>count</global>: var i32 = 0

		// Scales a point
		<function>scale</function>[<typeParameter>T</typeParameter>: <modifier>copy</modifier> + <modifier>noref</modifier>]: <modifier>pub</modifier> fun(mut <parameter>target</parameter>: <record>Point</record>, <parameter>factor</parameter>: i32) -> bool
		{
			val <val>label</val> = "x = {<parameter>target</parameter>.<field>x</field>:4}\n"
			val <val>bad</val> = "\q"
			const <constant>limit</constant> = 255u8
			var <var>size</var> = sizeOf(<record>Point</record>)
			if <parameter>factor</parameter> > 0 && <parameter>target</parameter>.<method>length</method>() > 1.0
			{
				ret true
			}

			ret false
		}
	""".trimIndent()
}

class CelpColorSettingsPage : ColorSettingsPage {
	private val descriptors = arrayOf(
		AttributesDescriptor("Setting", CellaColors.PROJECT_SETTING),
		AttributesDescriptor("Key", CellaColors.PROJECT_KEY),
		AttributesDescriptor("String", CellaColors.STRING),
		AttributesDescriptor("Number", CellaColors.NUMBER),
		AttributesDescriptor("Boolean", CellaColors.PROJECT_BOOLEAN),
		AttributesDescriptor("Comment", CellaColors.PROJECT_COMMENT),
		AttributesDescriptor("Equals", CellaColors.PROJECT_EQUALS)
	)
	
	override fun getDisplayName() = "Cella Project"
	override fun getIcon() = CellaIcons.FILE
	override fun getHighlighter() = CelpHighlighter()
	override fun getAttributeDescriptors() = descriptors
	override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY
	override fun getAdditionalHighlightingTagToDescriptorMap() = mapOf("setting" to CellaColors.PROJECT_SETTING)
	
	override fun getDemoText() = """
		# Game project
		<setting>OutputType</setting> = "Executable"
		<setting>Referenceable</setting> = false
		<setting>Imports</setting> = ["std.io"]
		
		[[<setting>ProjectReferences</setting>]]
		<setting>Path</setting> = "../Engine/engine.celp"
		<setting>Alias</setting> = "engine"
		
		[<setting>When</setting>."debug"]
		<setting>Flags</setting> = { trace = true }
	""".trimIndent()
}
