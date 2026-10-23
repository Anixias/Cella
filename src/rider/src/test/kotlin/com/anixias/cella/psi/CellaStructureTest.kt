package com.anixias.cella.psi

import com.anixias.cella.lexer.CellaScanner
import com.anixias.cella.lexer.CellaToken
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CellaStructureTest {
	private class ScannerSource(override val text: String) : StructureSource {
		private val tokens = buildList {
			val scanner = CellaScanner()
			scanner.start(text, 0, text.length, CellaScanner.NORMAL)
			while (true) {
				val type = scanner.tokenType ?: break
				if (type != CellaToken.WHITESPACE && type != CellaToken.LINE_COMMENT && type != CellaToken.BLOCK_COMMENT)
					add(Triple(type, scanner.tokenStart, scanner.tokenEnd))
				
				scanner.advance()
			}
		}
		
		private var index = 0
		private val open = mutableListOf<IntArray>()
		val declarations = mutableListOf<Pair<Int, Int>>()
		
		override val token get() = tokens[index].first
		override val tokenStart get() = tokens[index].second
		override val tokenText get() = text.substring(tokens[index].second, tokens[index].third)
		
		override fun isAtEnd() = index >= tokens.size
		
		override fun mark(): StructureMarker {
			val start = intArrayOf(if (isAtEnd()) text.length else tokenStart)
			open += start
			return object : StructureMarker {
				override fun done() {
					assertTrue(open.last() === start, "a later marker is still open")
					open.removeLast()
					declarations += start[0] to if (index == 0) start[0] else tokens[index - 1].third
				}
				
				override fun drop() {
					open.remove(start)
				}
			}
		}
		
		override fun advance() {
			index++
		}
	}
	
	private fun headers(text: String): List<String> {
		val source = ScannerSource(text)
		parseStructure(source)
		assertTrue(source.declarations.all { (start, end) -> text.substring(start, end).trimEnd().endsWith("}") ||
			end == text.trimEnd().length })
		
		return source.declarations.sortedBy { it.first }.map { text.substring(it.first).lineSequence().first().trim() }
	}
	
	@Test
	fun findsNestedDeclarations() {
		val text = """
			mod test

			Point: rec
			{
				x: i32
				y: i32
			}

			Point: impl Show
			{
				show: fun(imm self) -> str
				{
					if x > 0
					{
						helper: fun(a: i32,
							b: i32) -> i32
						{
							ret a
						}
					}

					ret "p"
				}

				?: op(imm self) -> bool => match self
				{
					_ => true
				}
			}

			pvt ext
			{
				puts: fun(s: cstr) -> i32
			}

			[T: copy]: impl Show
			{
			}

			Pair[T]: impl Show { describe: fun -> str { ret "" } }

			main: fun
			{
				outer: while true
				{
				}
			}
		""".trimIndent()

		assertEquals(listOf(
			"Point: rec",
			"Point: impl Show",
			"show: fun(imm self) -> str",
			"helper: fun(a: i32,",
			"?: op(imm self) -> bool => match self",
			"pvt ext",
			"[T: copy]: impl Show",
			"Pair[T]: impl Show { describe: fun -> str { ret \"\" } }",
			"describe: fun -> str { ret \"\" } }",
			"main: fun"
		), headers(text))
	}

	@Test
	fun closesUnfinishedDeclarationsAtTheEnd() {
		assertEquals(listOf("main: fun", "inner: fun"), headers("main: fun\n{\n\tinner: fun\n\t{\n\t\tret\n"))
		assertEquals(listOf("main: fun"), headers("main: fun\n{\n}\n}\n}\n"))
		assertEquals(emptyList(), headers("{ ( } ] )"))
	}
}
