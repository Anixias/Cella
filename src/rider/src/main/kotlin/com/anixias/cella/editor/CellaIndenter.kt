package com.anixias.cella.editor

import com.anixias.cella.lexer.CellaScanner
import com.anixias.cella.lexer.CellaToken

object CellaIndenter {
	private val headerKeywords = setOf("if", "else", "while", "for", "loop", "do")
	private val loopKeywords = setOf("while", "for", "loop", "do")
	private val blockKeywords = setOf("match", "when")
	private val continuingWords = setOf("is", "in", "as")
	private val skipped = setOf(CellaToken.WHITESPACE, CellaToken.LINE_COMMENT, CellaToken.BLOCK_COMMENT)
	
	private val openers = mapOf(
		CellaToken.RIGHT_BRACE to CellaToken.LEFT_BRACE,
		CellaToken.RIGHT_PAREN to CellaToken.LEFT_PAREN,
		CellaToken.RIGHT_BRACKET to CellaToken.LEFT_BRACKET,
		CellaToken.INTERPOLATION_END to CellaToken.INTERPOLATION_START
	)
	
	private val closingCharacters = mapOf(
		'}' to CellaToken.LEFT_BRACE,
		')' to CellaToken.LEFT_PAREN,
		']' to CellaToken.LEFT_BRACKET
	)
	
	private class Token(val type: CellaToken, val text: String, val line: Int, val index: Int, val isAfterDot: Boolean) {
		var enclosing: Token? = null
		var opener: Token? = null
	}
	
	private class Scan(val text: CharSequence, val tokens: List<Token>, val lineStarts: List<Int>, val open: Token?) {
		private val firstOnLine = HashMap<Int, Token>().also { first ->
			for (token in tokens)
				first.putIfAbsent(token.line, token)
		}
		
		fun firstOn(line: Int) = firstOnLine.getValue(line)
		
		fun previous(token: Token) = tokens.getOrNull(token.index - 1)
		
		fun indentOf(line: Int): String {
			val start = lineStarts[line]
			var end = start
			while (end < text.length && (text[end] == ' ' || text[end] == '\t'))
				end++
			
			return text.substring(start, end)
		}
	}
	
	fun indentAt(text: CharSequence, offset: Int, unit: String): String? {
		var lineStart = offset
		while (lineStart > 0 && text[lineStart - 1] != '\n')
			lineStart--
		
		val scan = scan(text, lineStart) ?: return null
		var restStart = lineStart
		while (restStart < text.length && (text[restStart] == ' ' || text[restStart] == '\t'))
			restStart++
		
		val rest = text.getOrNull(restStart)?.takeUnless { it == '\r' || it == '\n' }
		closingCharacters[rest]?.let { type ->
			var opener = scan.open
			while (opener != null && opener.type != type)
				opener = opener.enclosing
			
			return opener?.let { scan.indentOf(it.line) } ?: ""
		}
		
		val opensBlock = rest == '{'
		scan.open?.takeIf { it.type != CellaToken.LEFT_BRACE }?.let {
			return scan.indentOf(it.line) + if (opensBlock) "" else unit
		}
		
		val last = scan.tokens.lastOrNull() ?: return ""
		if (last.type == CellaToken.LEFT_BRACE)
			return scan.indentOf(last.line) + unit
		
		if (continues(last) || startsContinuation(text, restStart)) {
			val start = statementStart(scan, last)
			return scan.indentOf(start.line) + if (opensBlock && last.text == "=>") "" else unit
		}
		
		headerStart(scan, last)?.let { return scan.indentOf(it.line) + if (opensBlock) "" else unit }
		var start = statementStart(scan, last)
		if (start.text in blockKeywords && start.type == CellaToken.CONTROL_KEYWORD)
			return scan.indentOf(start.line)
		
		while (true) {
			if (start.type == CellaToken.RIGHT_BRACE) {
				val owner = start.opener?.let(scan::previous)?.takeIf { it.type != CellaToken.LEFT_BRACE } ?: break
				start = headerStart(scan, owner) ?: statementStart(scan, owner)
				continue
			}
			
			start = scan.previous(start)?.let { headerStart(scan, it) } ?: break
		}
		
		return scan.indentOf(start.line)
	}
	
	private fun scan(text: CharSequence, end: Int): Scan? {
		val lineStarts = mutableListOf(0)
		for (index in 0 until end) {
			if (text[index] == '\n')
				lineStarts += index + 1
		}
		
		val scanner = CellaScanner()
		scanner.start(text, 0, end, CellaScanner.NORMAL)
		val tokens = mutableListOf<Token>()
		val stack = ArrayDeque<Token>()
		var line = 0
		var lastType: CellaToken? = null
		var lastEnd = 0
		while (true) {
			val type = scanner.tokenType ?: break
			lastType = type
			lastEnd = scanner.tokenEnd
			while (line + 1 < lineStarts.size && lineStarts[line + 1] <= scanner.tokenStart)
				line++
			
			if (type !in skipped) {
				val token = Token(type, text.substring(scanner.tokenStart, scanner.tokenEnd), line, tokens.size,
					tokens.lastOrNull()?.type == CellaToken.DOT)
				token.enclosing = stack.lastOrNull()
				openers[type]?.let { opening ->
					val index = stack.indexOfLast { it.type == opening }
					if (index >= 0) {
						token.opener = stack[index]
						while (stack.size > index)
							stack.removeLast()
					}
				}
				
				if (type in openers.values)
					stack.addLast(token)
				
				tokens += token
			}
			
			scanner.advance()
		}
		
		val isInsideComment = lastType == CellaToken.BLOCK_COMMENT && lastEnd == end &&
			!text.subSequence(0, end).endsWith("*/")
		
		if (isInsideComment || scanner.tokenState != CellaScanner.NORMAL)
			return null
		
		return Scan(text, tokens, lineStarts, stack.lastOrNull())
	}
	
	private fun continues(token: Token) = when (token.type) {
		CellaToken.OPERATOR -> token.text != "?" && !token.isAfterDot
		CellaToken.ARROW, CellaToken.COLON, CellaToken.DOT, CellaToken.COMMA -> true
		CellaToken.CONTROL_KEYWORD, CellaToken.OPERATOR_KEYWORD -> token.text in continuingWords
		else -> false
	}
	
	private fun statementStart(scan: Scan, token: Token): Token {
		var first = scan.firstOn(token.line)
		while (true) {
			val enclosing = first.enclosing
			if (enclosing != null && enclosing.type != CellaToken.LEFT_BRACE && enclosing.line < first.line) {
				first = scan.firstOn(enclosing.line)
				continue
			}
			
			val previous = scan.previous(first)
			if (previous != null && previous.line < first.line && (continues(previous) || isLeading(first))) {
				first = scan.firstOn(previous.line)
				continue
			}
			
			return first
		}
	}
	
	private fun isLeading(token: Token) = token.type == CellaToken.DOT || token.text == "->"
	
	private fun startsContinuation(text: CharSequence, start: Int) =
		text.startsWith("->", start) || text.getOrNull(start) == '.' && text.getOrNull(start + 1) != '.'
	
	private fun headerStart(scan: Scan, token: Token): Token? {
		if (continues(token) || token.type == CellaToken.RIGHT_BRACE && !closesMatch(scan, token) ||
			scan.tokens.getOrNull(token.index + 1)?.line == token.line)
			return null
		
		val start = statementStart(scan, token)
		val keyword = keywordOf(scan, start) ?: return null
		if (keyword.enclosing !== token.enclosing && token.opener?.enclosing !== keyword.enclosing)
			return null
		
		if (keyword.text == "while" && endsDoLoop(scan, start))
			return null
		
		return start
	}
	
	private fun closesMatch(scan: Scan, token: Token): Boolean {
		val opener = token.opener ?: return false
		return scan.tokens.subList(0, opener.index).asReversed().takeWhile { it.line == opener.line }.any {
			it.type == CellaToken.CONTROL_KEYWORD && it.text == "match" && it.enclosing === opener.enclosing
		}
	}
	
	private fun keywordOf(scan: Scan, start: Token): Token? {
		if (start.type == CellaToken.CONTROL_KEYWORD && start.text in headerKeywords)
			return start.takeUnless { scan.tokens.getOrNull(start.index + 1)?.type == CellaToken.COLON }
		
		val colon = scan.tokens.getOrNull(start.index + 1)
		val keyword = scan.tokens.getOrNull(start.index + 2)
		val isLabel = start.type == CellaToken.IDENTIFIER && colon?.type == CellaToken.COLON &&
			keyword?.type == CellaToken.CONTROL_KEYWORD && keyword.text in loopKeywords && keyword.line == start.line
		
		return keyword.takeIf { isLabel }
	}
	
	private fun endsDoLoop(scan: Scan, start: Token): Boolean {
		var body = scan.previous(start) ?: return false
		if (body.type == CellaToken.RIGHT_BRACE)
			body = body.opener ?: return false
		else
			body = statementStart(scan, body)
		
		val header = scan.previous(body) ?: return false
		return header.text == "do" && header.type == CellaToken.CONTROL_KEYWORD
	}
}
