package com.anixias.cella.lexer

import com.intellij.lexer.LexerBase
import com.intellij.psi.tree.IElementType

class CellaLexer : LexerBase() {
	private val scanner = CellaScanner()
	private var buffer: CharSequence = ""
	private var end = 0
	
	override fun start(buffer: CharSequence, startOffset: Int, endOffset: Int, initialState: Int) {
		this.buffer = buffer
		end = endOffset
		scanner.start(buffer, startOffset, endOffset, initialState)
	}
	
	override fun getState() = scanner.tokenState
	override fun getTokenType(): IElementType? = scanner.tokenType?.let { CellaTokenTypes[it] }
	override fun getTokenStart() = scanner.tokenStart
	override fun getTokenEnd() = scanner.tokenEnd
	override fun advance() = scanner.advance()
	override fun getBufferSequence() = buffer
	override fun getBufferEnd() = end
}

class CelpLexer : LexerBase() {
	private val scanner = CelpScanner()
	private var buffer: CharSequence = ""
	private var end = 0
	
	override fun start(buffer: CharSequence, startOffset: Int, endOffset: Int, initialState: Int) {
		this.buffer = buffer
		end = endOffset
		scanner.start(buffer, startOffset, endOffset, initialState)
	}
	
	override fun getState() = scanner.tokenState
	override fun getTokenType(): IElementType? = scanner.tokenType?.let { CelpTokenTypes[it] }
	override fun getTokenStart() = scanner.tokenStart
	override fun getTokenEnd() = scanner.tokenEnd
	override fun advance() = scanner.advance()
	override fun getBufferSequence() = buffer
	override fun getBufferEnd() = end
}
