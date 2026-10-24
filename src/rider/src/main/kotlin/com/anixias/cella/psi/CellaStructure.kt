package com.anixias.cella.psi

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.lang.ASTNode
import com.intellij.lang.PsiBuilder
import com.intellij.psi.PsiElement
import com.intellij.psi.tree.IElementType
import com.intellij.ui.breadcrumbs.BreadcrumbsProvider

object CellaElementTypes {
	val DECLARATION = IElementType("DECLARATION", CellaLanguage)
}

private val nameTokens = setOf(
	CellaToken.IDENTIFIER, CellaToken.BUILTIN_TYPE, CellaToken.DECLARATION_KEYWORD, CellaToken.CONTROL_KEYWORD,
	CellaToken.MODIFIER_KEYWORD, CellaToken.VALUE_KEYWORD, CellaToken.OPERATOR_KEYWORD, CellaToken.OPERATOR
)

private val visibilities = setOf("pub", "pvt", "mod")

private const val RecordedTokens = 32

private class Statement(val marker: StructureMarker) {
	val tokens = ArrayList<Pair<CellaToken?, String>>()
	var depth = 0
	
	fun record(token: CellaToken?, text: String) {
		if (tokens.size < RecordedTokens)
			tokens += token to text
	}
	
	fun isDeclaration(): Boolean {
		val kinds = tokens.map { it.first }
		if (isExtBlock(tokens.map { it.second }))
			return true
		
		val nameEnd = nameEnd(kinds) ?: return false
		return kinds.getOrNull(nameEnd) == CellaToken.COLON && kinds.getOrNull(nameEnd + 1) != CellaToken.CONTROL_KEYWORD
	}
	
	private fun isExtBlock(texts: List<String>): Boolean {
		var index = 0
		while (index < texts.size && texts[index] in visibilities)
			index++
		
		return index == texts.size - 1 && texts[index] == "ext"
	}
}

private fun skipBrackets(kinds: List<CellaToken?>, start: Int): Int? {
	var depth = 0
	for (index in start until kinds.size) {
		when (kinds[index]) {
			CellaToken.LEFT_BRACKET -> depth++
			CellaToken.RIGHT_BRACKET -> if (--depth == 0) return index + 1
			else -> {}
		}
	}
	
	return null
}

private fun nameEnd(kinds: List<CellaToken?>): Int? {
	val first = kinds.firstOrNull()
	if (first == CellaToken.LEFT_BRACKET)
		return skipBrackets(kinds, 0)
	
	if (first !in nameTokens)
		return null
	
	return if (kinds.getOrNull(1) == CellaToken.LEFT_BRACKET) skipBrackets(kinds, 1) else 1
}

private class Block(val owner: Statement?) {
	var statement: Statement? = null
}

internal interface StructureMarker {
	fun done()
	fun drop()
}

internal interface StructureSource {
	val text: CharSequence
	val token: CellaToken?
	val tokenStart: Int
	val tokenText: String
	fun isAtEnd(): Boolean
	fun mark(): StructureMarker
	fun advance()
}

private class BuilderSource(private val builder: PsiBuilder) : StructureSource {
	override val text: CharSequence get() = builder.originalText
	override val token get() = CellaTokenTypes.tokenOf(builder.tokenType)
	override val tokenStart get() = builder.currentOffset
	override val tokenText get() = builder.tokenText.orEmpty()
	
	override fun isAtEnd() = builder.eof()
	
	override fun mark(): StructureMarker {
		val marker = builder.mark()
		return object : StructureMarker {
			override fun done() = marker.done(CellaElementTypes.DECLARATION)
			override fun drop() = marker.drop()
		}
	}
	
	override fun advance() = builder.advanceLexer()
}

internal fun parseStructure(builder: PsiBuilder) = parseStructure(BuilderSource(builder))

internal fun parseStructure(source: StructureSource) {
	val text = source.text
	val blocks = ArrayDeque<Block>().apply { addLast(Block(null)) }
	var previousEnd = 0
	while (!source.isAtEnd()) {
		val token = source.token
		val start = source.tokenStart
		val tokenText = source.tokenText
		val block = blocks.last()
		var statement = block.statement
		val isOnNewLine = (previousEnd until start).any { text[it] == '\n' }
		if (statement != null && isOnNewLine && statement.depth == 0 && token != CellaToken.LEFT_BRACE) {
			statement.marker.drop()
			statement = null
			block.statement = null
		}
		
		if (token == CellaToken.RIGHT_BRACE && blocks.size > 1) {
			statement?.marker?.drop()
			blocks.removeLast()
			source.advance()
			previousEnd = start + tokenText.length
			block.owner?.let { finish(it) }
			if (block.owner != null)
				blocks.last().statement = null
			
			continue
		}
		
		if (statement == null) {
			statement = Statement(source.mark())
			block.statement = statement
		}
		
		when (token) {
			CellaToken.LEFT_PAREN, CellaToken.LEFT_BRACKET -> statement.depth++
			CellaToken.RIGHT_PAREN, CellaToken.RIGHT_BRACKET -> if (statement.depth > 0) statement.depth--
			else -> {}
		}
		
		if (token == CellaToken.LEFT_BRACE)
			blocks.addLast(Block(statement.takeIf { it.depth == 0 }))
		else
			statement.record(token, tokenText)
		
		source.advance()
		previousEnd = start + tokenText.length
	}
	
	while (blocks.isNotEmpty()) {
		val block = blocks.removeLast()
		block.statement?.marker?.drop()
		val owner = block.owner ?: continue
		finish(owner)
		blocks.lastOrNull()?.takeIf { it.statement === owner }?.statement = null
	}
}

private fun finish(statement: Statement) {
	if (statement.isDeclaration())
		statement.marker.done()
	else
		statement.marker.drop()
}

class CellaBreadcrumbsProvider : BreadcrumbsProvider {
	override fun getLanguages() = arrayOf(CellaLanguage)
	
	override fun acceptElement(element: PsiElement) = element.node?.elementType == CellaElementTypes.DECLARATION
	
	override fun getElementInfo(element: PsiElement): String {
		val tokens = generateSequence(element.node.firstChildNode, ASTNode::getTreeNext)
			.takeWhile { CellaTokenTypes.tokenOf(it.elementType) != CellaToken.LEFT_BRACE }
			.toList()
		
		val header = tokens.joinToString("") { it.text }.lineSequence().first().trim()
		var depth = 0
		for ((index, node) in tokens.withIndex()) {
			when (CellaTokenTypes.tokenOf(node.elementType)) {
				CellaToken.LEFT_BRACKET, CellaToken.LEFT_PAREN -> depth++
				CellaToken.RIGHT_BRACKET, CellaToken.RIGHT_PAREN -> depth--
				CellaToken.COLON -> if (depth == 0) {
					val kind = tokens.drop(index + 1).map { it.text.trim() }
						.firstOrNull { it.isNotEmpty() && it !in visibilities }
					
					return if (kind == "impl") header else tokens.take(index).joinToString("") { it.text }.trim()
				}
				
				else -> {}
			}
		}
		
		return header
	}
}
