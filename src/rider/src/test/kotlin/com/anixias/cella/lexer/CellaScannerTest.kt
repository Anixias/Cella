package com.anixias.cella.lexer

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CellaScannerTest {
	private data class Lexed(val start: Int, val end: Int, val type: CellaToken, val state: Int)
	
	private fun lex(text: CharSequence, start: Int = 0, state: Int = CellaScanner.NORMAL): List<Lexed> {
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
	
	private fun sources(folder: String): List<File> {
		val directory = cases.resolve(folder)
		assertTrue(directory.isDirectory, "$directory is not a folder")
		val files = directory.walkTopDown().filter { it.extension == "ce" }.toList()
		assertTrue(files.isNotEmpty(), "$directory has no .ce files")
		return files
	}
	
	@Test
	fun coversEveryCaseWithoutGapsOrRestartDifferences() {
		for (file in sources("run") + sources("errors")) {
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
	fun leavesContextualKeywordsToTheServer() {
		assertEquals(
			listOf(
				"Point:IDENTIFIER", "::COLON", "ref:MODIFIER_KEYWORD", "rec:IDENTIFIER",
				"scale:IDENTIFIER", "[:LEFT_BRACKET", "T:IDENTIFIER", "::COLON", "copy:IDENTIFIER",
				"+:OPERATOR", "noref:IDENTIFIER", "]:RIGHT_BRACKET", "::COLON", "pub:IDENTIFIER",
				"fun:DECLARATION_KEYWORD", "->:ARROW", "Self:BUILTIN_TYPE",
				"pub:IDENTIFIER", "ext:IDENTIFIER", "{:LEFT_BRACE", "}:RIGHT_BRACE",
				"val:MODIFIER_KEYWORD", "rec:IDENTIFIER", "=:OPERATOR", "255:INTEGER", "u8:LITERAL_SUFFIX"
			),
			describe("Point: ref rec\nscale[T: copy + noref]: pub fun -> Self\npub ext { }\nval rec = 255u8\n")
		)
	}

	@Test
	fun matchesTheCompilerKeywords() {
		val declared = Regex("""new\("(\w+)", IsKeyword: true(, IsContextual: true)?""")
			.findAll(File(System.getProperty("cella.tokens")).readText())
			.toList()

		val (contextual, reserved) = declared.partition { it.groupValues[2].isNotEmpty() }
		assertEquals(reserved.map { it.groupValues[1] }.toSet(), CellaScanner.reservedWords)
		for (word in contextual.map { it.groupValues[1] }) {
			assertEquals(listOf("$word:IDENTIFIER"), describe(word))
			assertTrue(CellaScanner.keywordCategory(word) != null, "$word has no keyword category")
		}
	}
	
	@Test
	fun splitsInterpolatedStrings() {
		assertEquals(
			listOf(
				"\"a :STRING", "{:INTERPOLATION_START", "x:IDENTIFIER", "+:OPERATOR", "1:INTEGER",
				"::FORMAT_SEPARATOR", "4:FORMAT_SPEC", "}:INTERPOLATION_END", " b:STRING", "\\n:STRING_ESCAPE", "\\q:INVALID_STRING_ESCAPE",
				"\":STRING"
			),
			describe("\"a {x + 1:4} b\\n\\q\"")
		)
	}
}
