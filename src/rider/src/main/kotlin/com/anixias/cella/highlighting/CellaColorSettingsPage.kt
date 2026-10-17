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
		AttributesDescriptor("Punctuation//Braces", CellaColors.BRACES),
		AttributesDescriptor("Punctuation//Parentheses", CellaColors.PARENTHESES),
		AttributesDescriptor("Punctuation//Brackets", CellaColors.BRACKETS),
		AttributesDescriptor("Punctuation//Comma", CellaColors.COMMA),
		AttributesDescriptor("Punctuation//Dot", CellaColors.DOT),
		AttributesDescriptor("Punctuation//Operator", CellaColors.OPERATOR),
		AttributesDescriptor("Bad character", CellaColors.BAD_CHARACTER)
	)
	
	override fun getDisplayName() = "Cella"
	override fun getIcon() = CellaIcons.FILE
	override fun getHighlighter() = CellaHighlighter()
	override fun getAttributeDescriptors() = descriptors
	override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY
	override fun getAdditionalHighlightingTagToDescriptorMap(): Map<String, Nothing>? = null
	
	override fun getDemoText() = """
		mod demo

		use std.io

		/* Shapes */
		Shape: enum
		{
			circle
			square
		}

		Point: ref rec
		{
			x: i32

			length: fun(imm self) -> f64 = 1.5e3
		}

		// Scales a point
		scale[T: copy + noref]: pub fun(mut target: Point, factor: i32) -> bool
		{
			val label = "x = {target.x:4}\n"
			val bad = "\q"
			const limit = 255u8
			val size = sizeOf(Point)
			if factor > 0 && self != null
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
	override fun getAdditionalHighlightingTagToDescriptorMap(): Map<String, Nothing>? = null
	
	override fun getDemoText() = """
		# Game project
		OutputType = "Executable"
		Referenceable = false
		Imports = ["std.io"]
		
		[[ProjectReferences]]
		Path = "../Engine/engine.celp"
		Alias = "engine"
		
		[When."debug"]
		Flags = { trace = true }
	""".trimIndent()
}
