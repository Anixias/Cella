package com.anixias.cella.lexer

enum class CelpToken {
	WHITESPACE,
	COMMENT,
	SETTING,
	KEY,
	STRING,
	STRING_ESCAPE,
	INVALID_STRING_ESCAPE,
	NUMBER,
	BOOLEAN,
	LEFT_BRACKET,
	RIGHT_BRACKET,
	LEFT_BRACE,
	RIGHT_BRACE,
	EQUALS,
	COMMA,
	DOT,
	BAD_CHARACTER
}

class CelpScanner {
	companion object {
		const val NORMAL = 0
		const val TRIPLE_BASIC = 1
		const val TRIPLE_LITERAL = 2
		const val HEADER = 3
		const val BASIC = 4
		
		private val settings = setOf(
			"OutputType", "CellaVersion", "StandardLibrary", "SystemLinkPreference", "AssemblyName", "BoundsChecks",
			"OverflowChecks", "DebugInfo", "Referenceable", "ProjectReferences", "Path", "Alias", "Flags", "Imports",
			"Prelude", "When"
		)
	}
	
	private var buffer: CharSequence = ""
	private var bufferEnd = 0
	private var state = NORMAL
	
	var tokenStart = 0
		private set
	var tokenEnd = 0
		private set
	var tokenType: CelpToken? = null
		private set
	var tokenState = NORMAL
		private set
	
	fun start(buffer: CharSequence, start: Int, end: Int, initialState: Int) {
		this.buffer = buffer
		bufferEnd = end
		state = initialState
		tokenEnd = start
		advance()
	}
	
	fun advance() {
		tokenStart = tokenEnd
		tokenState = state
		if (tokenStart >= bufferEnd) {
			tokenType = null
			return
		}
		
		tokenType = when (state) {
			TRIPLE_BASIC -> scanStringRun("\"\"\"", true)
			TRIPLE_LITERAL -> scanStringRun("'''", false)
			BASIC -> if (isNewline(buffer[tokenStart])) scanCode() else scanStringRun("\"", true)
			else -> scanCode()
		}
	}
	
	private fun scanCode(): CelpToken {
		val c = buffer[tokenStart]
		if (c.isWhitespace()) {
			tokenEnd = tokenStart
			while (tokenEnd < bufferEnd && buffer[tokenEnd].isWhitespace()) {
				if (isNewline(buffer[tokenEnd]))
					state = NORMAL
				
				tokenEnd++
			}
			
			return CelpToken.WHITESPACE
		}
		
		if (c == '#') {
			tokenEnd = tokenStart
			while (tokenEnd < bufferEnd && !isNewline(buffer[tokenEnd]))
				tokenEnd++
			
			return CelpToken.COMMENT
		}
		
		if (startsWith("\"\"\"")) {
			tokenEnd = tokenStart + 3
			state = TRIPLE_BASIC
			return scanStringRun("\"\"\"", true)
		}
		
		if (startsWith("'''")) {
			tokenEnd = tokenStart + 3
			state = TRIPLE_LITERAL
			return scanStringRun("'''", false)
		}
		
		if (c == '"') {
			tokenEnd = tokenStart + 1
			state = if (state == HEADER) HEADER else BASIC
			return if (state == HEADER) scanHeaderString() else scanStringRun("\"", true)
		}
		
		if (c == '\'') {
			tokenEnd = tokenStart + 1
			while (tokenEnd < bufferEnd && !isNewline(buffer[tokenEnd]) && buffer[tokenEnd] != '\'')
				tokenEnd++
			
			if (tokenEnd < bufferEnd && buffer[tokenEnd] == '\'')
				tokenEnd++
			
			return CelpToken.STRING
		}
		
		if (c.isDigit() || (c == '+' || c == '-') && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1].isDigit())
			return scanNumber()
		
		if (c.isLetterOrDigit() || c == '_' || c == '-')
			return scanWord()
		
		tokenEnd = tokenStart + 1
		return when (c) {
			'[' -> {
				if (isLineStart())
					state = HEADER
				
				CelpToken.LEFT_BRACKET
			}
			
			']' -> {
				if (state == HEADER && !(tokenEnd < bufferEnd && buffer[tokenEnd] == ']'))
					state = NORMAL
				
				CelpToken.RIGHT_BRACKET
			}
			
			'{' -> CelpToken.LEFT_BRACE
			'}' -> CelpToken.RIGHT_BRACE
			'=' -> CelpToken.EQUALS
			',' -> CelpToken.COMMA
			'.' -> CelpToken.DOT
			else -> CelpToken.BAD_CHARACTER
		}
	}
	
	private fun scanHeaderString(): CelpToken {
		while (tokenEnd < bufferEnd && !isNewline(buffer[tokenEnd])) {
			if (buffer[tokenEnd++] == '"')
				break
		}
		
		return CelpToken.STRING
	}
	
	private fun scanWord(): CelpToken {
		tokenEnd = tokenStart
		while (tokenEnd < bufferEnd && (buffer[tokenEnd].isLetterOrDigit() || buffer[tokenEnd] in "_-"))
			tokenEnd++
		
		val text = buffer.subSequence(tokenStart, tokenEnd).toString()
		val isKey = state == HEADER || peekNext() in setOf('=', '.')
		return when {
			isKey && text in settings -> CelpToken.SETTING
			isKey -> CelpToken.KEY
			text == "true" || text == "false" -> CelpToken.BOOLEAN
			else -> CelpToken.KEY
		}
	}
	
	private fun scanNumber(): CelpToken {
		tokenEnd = tokenStart + 1
		while (tokenEnd < bufferEnd && (buffer[tokenEnd].isLetterOrDigit() || buffer[tokenEnd] in "_.+-")) {
			val c = buffer[tokenEnd]
			if ((c == '+' || c == '-') && buffer[tokenEnd - 1] !in "eE")
				break
			
			tokenEnd++
		}
		
		return CelpToken.NUMBER
	}
	
	private fun scanStringRun(closer: String, hasEscapes: Boolean): CelpToken {
		if (hasEscapes && tokenEnd == tokenStart && buffer[tokenStart] == '\\')
			return scanEscape()
		
		while (tokenEnd < bufferEnd) {
			if (startsWith(closer, tokenEnd)) {
				tokenEnd += closer.length
				while (closer.length == 3 && tokenEnd < bufferEnd && buffer[tokenEnd] == closer[0])
					tokenEnd++
				
				state = NORMAL
				return CelpToken.STRING
			}
			
			val c = buffer[tokenEnd]
			if (hasEscapes && c == '\\')
				return CelpToken.STRING
			
			if (closer.length == 1 && isNewline(c)) {
				state = NORMAL
				return CelpToken.STRING
			}
			
			tokenEnd++
		}
		
		return CelpToken.STRING
	}
	
	private fun scanEscape(): CelpToken {
		val kind = if (tokenStart + 1 < bufferEnd) buffer[tokenStart + 1] else null
		val (length, isValid) = when (kind) {
			'b', 't', 'n', 'f', 'r', '"', '\\' -> 2 to true
			'u' -> 6 to hasHexDigits(tokenStart + 2, 4)
			'U' -> 10 to hasHexDigits(tokenStart + 2, 8)
			else -> 2 to false
		}
		
		tokenEnd = if (isValid) tokenStart + length else minOf(tokenStart + 2, bufferEnd)
		return if (isValid) CelpToken.STRING_ESCAPE else CelpToken.INVALID_STRING_ESCAPE
	}
	
	private fun hasHexDigits(position: Int, count: Int) = position + count <= bufferEnd &&
		(0 until count).all { buffer[position + it].let { c -> c.isDigit() || c in 'a'..'f' || c in 'A'..'F' } }
	
	private fun peekNext(): Char? {
		var position = tokenEnd
		while (position < bufferEnd && (buffer[position] == ' ' || buffer[position] == '\t'))
			position++
		
		return if (position < bufferEnd) buffer[position] else null
	}
	
	private fun isLineStart(): Boolean {
		var position = tokenStart - 1
		while (position >= 0 && (buffer[position] == ' ' || buffer[position] == '\t'))
			position--
		
		return position < 0 || isNewline(buffer[position]) || (position > 0 && buffer[position] == '[' && isLineStartAt(position))
	}
	
	private fun isLineStartAt(bracket: Int): Boolean {
		var position = bracket - 1
		while (position >= 0 && (buffer[position] == ' ' || buffer[position] == '\t'))
			position--
		
		return position < 0 || isNewline(buffer[position])
	}
	
	private fun startsWith(text: String, position: Int = tokenStart) =
		position + text.length <= bufferEnd && (text.indices).all { buffer[position + it] == text[it] }
	
	private fun isNewline(c: Char) = c == '\n' || c == '\r'
}
