package com.anixias.cella.lexer

import com.anixias.cella.CellaLanguage
import com.anixias.cella.CelpLanguage
import com.intellij.psi.TokenType
import com.intellij.psi.tree.IElementType
import com.intellij.psi.tree.IFileElementType
import com.intellij.psi.tree.TokenSet

object CellaTokenTypes {
	private val types = CellaToken.entries.associateWith {
		when (it) {
			CellaToken.WHITESPACE -> TokenType.WHITE_SPACE
			CellaToken.BAD_CHARACTER -> TokenType.BAD_CHARACTER
			else -> IElementType(it.name, CellaLanguage)
		}
	}
	
	private val tokens = types.entries.associate { (token, type) -> type to token }
	
	val FILE = IFileElementType(CellaLanguage)
	
	operator fun get(token: CellaToken): IElementType = types.getValue(token)
	
	fun tokenOf(type: IElementType?): CellaToken? = tokens[type]
	
	val COMMENTS = TokenSet.create(this[CellaToken.LINE_COMMENT], this[CellaToken.BLOCK_COMMENT])
	val STRINGS = TokenSet.create(this[CellaToken.STRING], this[CellaToken.CHAR])
}

object CelpTokenTypes {
	private val types = CelpToken.entries.associateWith {
		when (it) {
			CelpToken.WHITESPACE -> TokenType.WHITE_SPACE
			CelpToken.BAD_CHARACTER -> TokenType.BAD_CHARACTER
			else -> IElementType("CELP_${it.name}", CelpLanguage)
		}
	}
	
	private val tokens = types.entries.associate { (token, type) -> type to token }
	
	val FILE = IFileElementType(CelpLanguage)
	
	operator fun get(token: CelpToken): IElementType = types.getValue(token)
	
	fun tokenOf(type: IElementType?): CelpToken? = tokens[type]
	
	val COMMENTS = TokenSet.create(this[CelpToken.COMMENT])
	val STRINGS = TokenSet.create(this[CelpToken.STRING])
}
