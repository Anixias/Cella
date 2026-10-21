package com.anixias.cella.editor

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

class CellaIndenterTest {
	private fun indentAfter(before: String, rest: String = "") =
		CellaIndenter.indentAt(before + rest, before.length, "\t")
	
	@Test
	fun indentsAfterExpressionArrows() {
		assertEquals("\t", indentAfter("describe: fun(imm enemy: Enemy) -> str =>\n"))
		assertEquals("\t\t\t", indentAfter("main: fun\n{\n\tmatch slot\n\t{\n\t\tfull(val texture) =>\n"))
	}
	
	@Test
	fun snapsBlocksBackToTheirHeader() {
		assertEquals("\t\t", indentAfter("main: fun\n{\n\tmatch slot\n\t{\n\t\tfull(val texture) =>\n", "{"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tif health <= 0\n", "{"))
	}
	
	@Test
	fun indentsWrappedLinesOnce() {
		val start = "main: fun\n{\n\tval total = base +\n"
		assertEquals("\t\t", indentAfter(start))
		assertEquals("\t\t", indentAfter("$start\t\tbonus * multiplier +\n"))
		assertEquals("\t", indentAfter("$start\t\tbonus * multiplier +\n\t\textra\n"))
		assertEquals("\t\t", indentAfter("main: fun\n{\n\tprint(first,\n"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tprint(first,\n\t\tsecond)\n"))
	}
	
	@Test
	fun indentsBracelessBodies() {
		assertEquals("\t\t", indentAfter("main: fun\n{\n\tif health <= 0\n"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tif health <= 0\n\t\tret\n"))
		assertEquals("\t\t\t", indentAfter("main: fun\n{\n\tfor x in xs\n\t\tif x > 0\n"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tfor x in xs\n\t\tif x > 0\n\t\t\tshow(x)\n"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tif a\n\t{\n\t\tshow(a)\n\t}\n"))
		assertEquals("\t\t", indentAfter("main: fun\n{\n\tif a\n\t\tshow(a)\n\telse\n"))
		assertEquals("\t\t", indentAfter("main: fun\n{\n\touter: while running\n"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tdo\n\t\tstep()\n\twhile running\n"))
		assertEquals("\t", indentAfter("main: fun\n{\n\tmatch slot\n"))
	}
	
	@Test
	fun alignsClosingBrackets() {
		assertEquals("\t", indentAfter("main: fun\n{\n\tif a\n\t{\n\t\tshow(a)\n", "}"))
		assertEquals("", indentAfter("main: fun\n{\n\tshow(a)\n", "}"))
	}
	
	@Test
	fun agreesWithTheTestCases() {
		val cases = File(System.getProperty("cella.cases"))
		val mismatches = mutableListOf<String>()
		var lines = 0
		for (file in cases.resolve("run").walkTopDown().filter { it.extension == "ce" }) {
			val text = file.readText().replace("\r\n", "\n")
			var lineStart = 0
			for (line in text.split('\n')) {
				val content = line.trimStart(' ', '\t')
				if (content.isNotEmpty()) {
					lines++
					val expected = line.substring(0, line.length - content.length)
					val actual = CellaIndenter.indentAt(text, lineStart, "\t")
					if (actual != null && actual != expected)
						mismatches += "${file.relativeTo(cases)}:${text.substring(0, lineStart).count { it == '\n' } + 1}: " +
							"expected ${expected.length}, got ${actual.length}: $content"
				}
				
				lineStart += line.length + 1
			}
		}
		
		assertTrue(mismatches.isEmpty(), "${mismatches.size} of $lines lines:\n" + mismatches.take(60).joinToString("\n"))
	}
}
