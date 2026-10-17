package com.anixias.cella.lexer

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CellaScannerTest {
	private data class Lexed(val start: Int, val end: Int, val type: CellaToken, val state: Int)
	
	private fun lex(text: CharSequence, start: Int = 0, state: Int = CellaScanner.LINE_START): List<Lexed> {
		val scanner = CellaScanner()
		scanner.start(text, start, text.length, state)
		val tokens = mutableListOf<Lexed>()
		while (true) {
			val type = scanner.tokenType ?: break
			tokens += Lexed(scanner.tokenStart, scanner.tokenEnd, type, scanner.tokenState)
			scanner.advance()
		}
		
		return tokens
	}
	
	private fun describe(text: String) = lex(text)
		.filter { it.type != CellaToken.WHITESPACE }
		.map { "${text.substring(it.start, it.end)}:${it.type}" }
	
	private val cases = File(System.getProperty("cella.cases"))
	
	private fun sources(folder: String) = cases.resolve(folder).walkTopDown().filter { it.extension == "ce" }.toList()
	
	@Test
	fun coversEveryCaseWithoutGapsOrRestartDifferences() {
		val files = sources("run") + sources("errors")
		assertTrue(files.size > 1000)
		for (file in files) {
			val text = file.readText().removePrefix("\uFEFF")
			val tokens = lex(text)
			var position = 0
			for (token in tokens) {
				assertEquals(position, token.start, "${file.name}: gap at $position")
				assertTrue(token.end > token.start, "${file.name}: empty token at $position")
				position = token.end
			}
			
			assertEquals(text.length, position, "${file.name}: stopped early")
			for (index in tokens.indices step 7) {
				val restarted = lex(text, tokens[index].start, tokens[index].state)
				assertEquals(tokens.drop(index), restarted, "${file.name}: restart at ${tokens[index].start}")
			}
		}
	}
	
	@Test
	fun validProgramsHaveNoBadCharacters() {
		for (file in sources("run")) {
			val text = file.readText().removePrefix("\uFEFF")
			val bad = lex(text).filter { it.type == CellaToken.BAD_CHARACTER }
			assertTrue(bad.isEmpty(), "${file.name}: ${bad.map { text.substring(it.start, it.end) }}")
		}
	}
	
	@Test
	fun classifiesContextualKeywordsByPosition() {
		assertEquals(
			listOf(
				"mod:DECLARATION_KEYWORD", "test:IDENTIFIER",
				"Point:IDENTIFIER", "::COLON", "ref:MODIFIER_KEYWORD", "rec:DECLARATION_KEYWORD",
				"scale:IDENTIFIER", "[:LEFT_BRACKET", "T:IDENTIFIER", "::COLON", "copy:MODIFIER_KEYWORD",
				"+:OPERATOR", "noref:MODIFIER_KEYWORD", "]:RIGHT_BRACKET", "::COLON", "pub:MODIFIER_KEYWORD",
				"fun:DECLARATION_KEYWORD", "->:ARROW", "i32:BUILTIN_TYPE",
				"get:IDENTIFIER", "::COLON", "fun:DECLARATION_KEYWORD",
				"get:DECLARATION_KEYWORD", "(:LEFT_PAREN", "self:VALUE_KEYWORD", "):RIGHT_PAREN",
				"val:MODIFIER_KEYWORD", "rec:IDENTIFIER", "=:OPERATOR", "255:INTEGER", "u8:LITERAL_SUFFIX"
			),
			describe(
				"mod test\nPoint: ref rec\nscale[T: copy + noref]: pub fun -> i32\nget: fun\nget(self)\nval rec = 255u8\n"
			)
		)
	}
	
	@Test
	fun splitsInterpolatedStrings() {
		assertEquals(
			listOf(
				"\"a :STRING", "{:INTERPOLATION_START", "x:IDENTIFIER", "+:OPERATOR", "1:INTEGER",
				":4:FORMAT_SPEC", "}:INTERPOLATION_END", " b:STRING", "\\n:STRING_ESCAPE", "\\q:INVALID_STRING_ESCAPE",
				"\":STRING"
			),
			describe("\"a {x + 1:4} b\\n\\q\"")
		)
	}
}
