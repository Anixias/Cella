package com.anixias.cella.lexer

class CellaScanner {
	companion object {
		const val NORMAL = 0
		const val STRING_CONTENT = 1
		const val SUFFIX = 2
		const val FORMAT = 3
		const val HOLE = 100
		
		private val keywords = buildMap {
			for (word in listOf("fun", "rec", "enum", "union", "trait", "impl", "mod", "use", "ext", "op", "prop", "get", "set"))
				put(word, CellaToken.DECLARATION_KEYWORD)
			for (word in listOf("if", "else", "for", "in", "loop", "while", "do", "break", "cont", "ret", "match", "is", "when", "panic"))
				put(word, CellaToken.CONTROL_KEYWORD)
			for (word in listOf("val", "var", "const", "mut", "imm", "own", "ref", "pub", "pvt", "req", "dyn", "atomic", "copy", "noref"))
				put(word, CellaToken.MODIFIER_KEYWORD)
			for (word in listOf("true", "false", "null", "undef", "default", "self"))
				put(word, CellaToken.VALUE_KEYWORD)
			for (word in listOf("new", "drop", "sizeOf", "alignOf", "nameOf", "as"))
				put(word, CellaToken.OPERATOR_KEYWORD)
		}
		
		val reservedWords = setOf(
			"alignOf", "atomic", "break", "const", "cont", "default", "do", "drop", "dyn", "else", "false", "for",
			"fun", "if", "imm", "in", "is", "loop", "match", "mut", "nameOf", "new", "null", "own", "panic", "ref",
			"ret", "self", "sizeOf", "true", "undef", "val", "var", "when", "while"
		)
		
		private val builtinTypes = setOf(
			"i8", "i16", "i32", "i64", "i128", "isize", "u8", "u16", "u32", "u64", "u128", "usize", "f32", "f64",
			"char", "bool", "str", "cstr", "fstr", "ptr", "array", "never", "Self"
		)
		
		private val operators = setOf(
			"!", "!=", "%", "%=", "&", "&&", "&=", "(", ")", "*", "*%", "*%=", "*=", "+", "+%", "+%=", "+=", ",",
			"-", "-%", "-%=", "-=", "->", ".", "..", "...", "..=", "/", "/=", ":", "<", "<<", "<<<", "<<<=", "<<=",
			"<=", "=", "==", "=>", ">", ">=", ">>", ">>=", ">>>", ">>>=", "?", "@", "[", "]", "^", "^=", "{", "|",
			"|=", "||", "}", "~"
		)
		
		private const val LONGEST_OPERATOR = 4
		
		fun isKeyword(text: String) = text in reservedWords
		
		fun keywordCategory(text: String) = keywords[text]
		
		fun isBuiltinType(text: String) = text in builtinTypes
	}
	
	private var buffer: CharSequence = ""
	private var bufferEnd = 0
	private var state = NORMAL
	
	var tokenStart = 0
		private set
	var tokenEnd = 0
		private set
	var tokenType: CellaToken? = null
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
		
		if (state == FORMAT) {
			state = HOLE
			tokenEnd = tokenStart
			var escaped = false
			while (tokenEnd < bufferEnd && !isNewline(buffer[tokenEnd]) && (buffer[tokenEnd] != '}' || escaped)) {
				escaped = buffer[tokenEnd] == '\\' && !escaped
				tokenEnd++
			}
			
			if (tokenEnd > tokenStart) {
				tokenType = CellaToken.FORMAT_SPEC
				return
			}
		}
		
		if (state == SUFFIX) {
			state = NORMAL
			tokenEnd = tokenStart
			while (tokenEnd < bufferEnd && (buffer[tokenEnd].isLetterOrDigit() || buffer[tokenEnd] == '_'))
				tokenEnd++
			
			if (tokenEnd > tokenStart) {
				tokenType = CellaToken.LITERAL_SUFFIX
				return
			}
		}
		
		tokenType = if (state == STRING_CONTENT && !isNewline(buffer[tokenStart])) scanStringContent() else scanCode()
	}
	
	private val inHole get() = state >= HOLE
	
	private fun scanCode(): CellaToken {
		val c = buffer[tokenStart]
		if (c.isWhitespace())
			return scanWhitespace()
		
		if (c in '0'..'9')
			return finish(scanNumber())
		
		val codePoint = Character.codePointAt(buffer, tokenStart)
		if (isIdentifierStart(codePoint))
			return scanIdentifier()
		
		if (c == '"') {
			if (inHole) {
				tokenEnd = skipPlainQuoted(tokenStart, '"')
				return CellaToken.STRING
			}
			
			return scanStringStart()
		}
		
		if (c == '\'') {
			tokenEnd = skipPlainQuoted(tokenStart, '\'')
			markSuffix(tokenEnd)
			return finish(CellaToken.CHAR)
		}
		
		if (c == '/' && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1] == '/') {
			tokenEnd = tokenStart + 2
			while (tokenEnd < bufferEnd && !isNewline(buffer[tokenEnd]))
				tokenEnd++
			
			return CellaToken.LINE_COMMENT
		}
		
		if (c == '/' && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1] == '*')
			return scanBlockComment()
		
		if (inHole)
			scanHoleSymbol(c)?.let { return it }
		
		val operator = scanOperator()
		if (operator != null)
			return operator
		
		tokenEnd = tokenStart + Character.charCount(codePoint)
		return finish(CellaToken.BAD_CHARACTER)
	}
	
	private fun finish(type: CellaToken): CellaToken {
		if (!inHole && state != SUFFIX)
			state = NORMAL
		
		return type
	}
	
	private fun scanWhitespace(): CellaToken {
		tokenEnd = tokenStart
		var sawNewline = false
		while (tokenEnd < bufferEnd && buffer[tokenEnd].isWhitespace()) {
			sawNewline = sawNewline || isNewline(buffer[tokenEnd])
			tokenEnd++
		}
		
		if (sawNewline)
			state = NORMAL
		
		return CellaToken.WHITESPACE
	}
	
	private fun scanBlockComment(): CellaToken {
		var depth = 1
		tokenEnd = tokenStart + 2
		while (tokenEnd < bufferEnd && depth > 0) {
			val pair = if (tokenEnd + 1 < bufferEnd) "${buffer[tokenEnd]}${buffer[tokenEnd + 1]}" else ""
			when (pair) {
				"/*" -> {
					depth++
					tokenEnd += 2
				}
				
				"*/" -> {
					depth--
					tokenEnd += 2
				}
				
				else -> tokenEnd++
			}
		}
		
		return CellaToken.BLOCK_COMMENT
	}
	
	private fun scanIdentifier(): CellaToken {
		tokenEnd = tokenStart + Character.charCount(Character.codePointAt(buffer, tokenStart))
		while (tokenEnd < bufferEnd) {
			val codePoint = Character.codePointAt(buffer, tokenEnd)
			if (!isIdentifierContinue(codePoint))
				break
			
			tokenEnd += Character.charCount(codePoint)
		}
		
		val text = buffer.subSequence(tokenStart, tokenEnd).toString()
		return finish(when (text) {
			in reservedWords -> keywords.getValue(text)
			in builtinTypes -> CellaToken.BUILTIN_TYPE
			else -> CellaToken.IDENTIFIER
		})
	}
	
	private fun scanNumber(): CellaToken {
		if (buffer[tokenStart] == '0' && tokenStart + 1 < bufferEnd && buffer[tokenStart + 1] in "xXbBoO") {
			val radix = when (buffer[tokenStart + 1]) {
				'x', 'X' -> 16
				'o', 'O' -> 8
				else -> 2
			}
			
			var end = tokenStart + 2
			while (end < bufferEnd && (buffer[end].isLetterOrDigit() || buffer[end] == '_'))
				end++
			
			var digitsEnd = tokenStart + 2
			while (digitsEnd < end && !(buffer[digitsEnd].isLetter() && !isRadixDigit(buffer[digitsEnd], radix)))
				digitsEnd++
			
			tokenEnd = digitsEnd
			if (end > digitsEnd && !inHole)
				state = SUFFIX
			
			return CellaToken.INTEGER
		}
		
		var end = skipDigits(tokenStart + 1)
		var type = CellaToken.INTEGER
		if (end + 1 < bufferEnd && buffer[end] == '.' && buffer[end + 1] in '0'..'9') {
			end = skipDigits(end + 1)
			type = CellaToken.FLOAT
		}
		
		if (end < bufferEnd && buffer[end] in "eE") {
			var exponent = end + 1
			if (exponent < bufferEnd && buffer[exponent] in "+-")
				exponent++
			
			if (exponent < bufferEnd && buffer[exponent] in '0'..'9') {
				end = skipDigits(exponent)
				type = CellaToken.FLOAT
			}
		}
		
		tokenEnd = end
		markSuffix(end)
		return type
	}
	
	private fun markSuffix(position: Int) {
		if (position < bufferEnd && buffer[position].isLetter() && !inHole)
			state = SUFFIX
	}
	
	private fun skipDigits(position: Int): Int {
		var end = position
		while (end < bufferEnd && (buffer[end] in '0'..'9' || buffer[end] == '_'))
			end++
		
		return end
	}
	
	private fun scanStringStart(): CellaToken {
		tokenEnd = tokenStart + 1
		return scanStringRun()
	}
	
	private fun scanStringContent(): CellaToken {
		val c = buffer[tokenStart]
		if (c == '\\')
			return scanEscape()
		
		if (c == '{') {
			tokenEnd = tokenStart + 1
			state = HOLE
			return CellaToken.INTERPOLATION_START
		}
		
		tokenEnd = tokenStart
		return scanStringRun()
	}
	
	private fun scanStringRun(): CellaToken {
		while (tokenEnd < bufferEnd) {
			when (buffer[tokenEnd]) {
				'"' -> {
					tokenEnd++
					state = NORMAL
					markSuffix(tokenEnd)
					return CellaToken.STRING
				}
				
				'\\', '{' -> {
					state = STRING_CONTENT
					return CellaToken.STRING
				}
				
				'\n', '\r' -> {
					state = NORMAL
					return CellaToken.STRING
				}
				
				else -> tokenEnd++
			}
		}
		
		state = NORMAL
		return CellaToken.STRING
	}
	
	private fun scanEscape(): CellaToken {
		val kind = if (tokenStart + 1 < bufferEnd) buffer[tokenStart + 1] else null
		val (length, isValid) = when (kind) {
			null -> 1 to false
			'\\', '\'', '"', '0', 'a', 'b', 'f', 'n', 'r', 't', 'v', '{', '}' -> 2 to true
			'o' -> 5 to hasDigits(tokenStart + 2, 3) { it in '0'..'7' }
			'x' -> 4 to hasDigits(tokenStart + 2, 2) { isHexDigit(it) }
			'u' -> 6 to hasDigits(tokenStart + 2, 4) { isHexDigit(it) }
			'U' -> 10 to hasDigits(tokenStart + 2, 8) { isHexDigit(it) }
			else -> 2 to false
		}
		
		tokenEnd = if (isValid) tokenStart + length else minOf(tokenStart + 2, bufferEnd)
		return if (isValid) CellaToken.STRING_ESCAPE else CellaToken.INVALID_STRING_ESCAPE
	}
	
	private fun hasDigits(position: Int, count: Int, isDigit: (Char) -> Boolean) =
		position + count <= bufferEnd && (0 until count).all { isDigit(buffer[position + it]) }
	
	private fun skipPlainQuoted(start: Int, quote: Char): Int {
		var end = start + 1
		var escaped = false
		while (end < bufferEnd && !isNewline(buffer[end])) {
			val c = buffer[end]
			end++
			if (c == quote && !escaped)
				return end
			
			escaped = c == '\\' && !escaped
		}
		
		return end
	}
	
	private fun scanHoleSymbol(c: Char): CellaToken? {
		val depth = state - HOLE
		return when {
			c == '{' -> {
				tokenEnd = tokenStart + 1
				state = HOLE + depth + 1
				CellaToken.LEFT_BRACE
			}
			
			c == '}' && depth == 0 -> {
				tokenEnd = tokenStart + 1
				state = STRING_CONTENT
				CellaToken.INTERPOLATION_END
			}
			
			c == '}' -> {
				tokenEnd = tokenStart + 1
				state = HOLE + depth - 1
				CellaToken.RIGHT_BRACE
			}
			
			c == ':' && depth == 0 -> {
				tokenEnd = tokenStart + 1
				state = FORMAT
				CellaToken.FORMAT_SEPARATOR
			}
			
			else -> null
		}
	}
	
	private fun scanOperator(): CellaToken? {
		var length = minOf(LONGEST_OPERATOR, bufferEnd - tokenStart)
		while (length > 0) {
			val text = buffer.subSequence(tokenStart, tokenStart + length).toString()
			if (text in operators) {
				tokenEnd = tokenStart + length
				return classifyOperator(text)
			}
			
			length--
		}
		
		return null
	}
	
	private fun classifyOperator(text: String): CellaToken {
		val type = when (text) {
			"{" -> CellaToken.LEFT_BRACE
			"}" -> CellaToken.RIGHT_BRACE
			"(" -> CellaToken.LEFT_PAREN
			")" -> CellaToken.RIGHT_PAREN
			"[" -> CellaToken.LEFT_BRACKET
			"]" -> CellaToken.RIGHT_BRACKET
			"," -> CellaToken.COMMA
			"." -> CellaToken.DOT
			":" -> CellaToken.COLON
			"->", "=>" -> CellaToken.ARROW
			else -> CellaToken.OPERATOR
		}
		
		return finish(type)
	}
	
	private fun isNewline(c: Char) = c == '\n' || c == '\r'
	
	private fun isHexDigit(c: Char) = c in '0'..'9' || c in 'a'..'f' || c in 'A'..'F'
	
	private fun isRadixDigit(c: Char, radix: Int) = when (radix) {
		16 -> isHexDigit(c)
		8 -> c in '0'..'7'
		else -> c == '0' || c == '1'
	}
	
	private fun isIdentifierStart(codePoint: Int) = codePoint == '_'.code ||
		when (Character.getType(codePoint).toByte()) {
			Character.UPPERCASE_LETTER, Character.LOWERCASE_LETTER, Character.TITLECASE_LETTER,
			Character.MODIFIER_LETTER, Character.OTHER_LETTER, Character.LETTER_NUMBER -> true
			
			else -> false
		}
	
	private fun isIdentifierContinue(codePoint: Int) = isIdentifierStart(codePoint) ||
		when (Character.getType(codePoint).toByte()) {
			Character.NON_SPACING_MARK, Character.COMBINING_SPACING_MARK, Character.DECIMAL_DIGIT_NUMBER,
			Character.CONNECTOR_PUNCTUATION -> true
			
			else -> false
		}
}
