package com.anixias.cella.psi

import com.anixias.cella.CellaFileType
import com.anixias.cella.CellaLanguage
import com.anixias.cella.CelpFileType
import com.anixias.cella.CelpLanguage
import com.anixias.cella.lexer.CelpLexer
import com.anixias.cella.lexer.CelpTokenTypes
import com.anixias.cella.lexer.CellaLexer
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.extapi.psi.ASTWrapperPsiElement
import com.intellij.extapi.psi.PsiFileBase
import com.intellij.lang.ASTNode
import com.intellij.lang.ParserDefinition
import com.intellij.lang.PsiParser
import com.intellij.openapi.project.Project
import com.intellij.psi.FileViewProvider
import com.intellij.psi.PsiElement

class CellaFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, CellaLanguage) {
	override fun getFileType() = CellaFileType
}

class CelpFile(viewProvider: FileViewProvider) : PsiFileBase(viewProvider, CelpLanguage) {
	override fun getFileType() = CelpFileType
}

private val flatParser = PsiParser { root, builder ->
	val file = builder.mark()
	while (!builder.eof())
		builder.advanceLexer()
	
	file.done(root)
	builder.treeBuilt
}

class CellaParserDefinition : ParserDefinition {
	override fun createLexer(project: Project?) = CellaLexer()
	override fun createParser(project: Project?) = flatParser
	override fun getFileNodeType() = CellaTokenTypes.FILE
	override fun getCommentTokens() = CellaTokenTypes.COMMENTS
	override fun getStringLiteralElements() = CellaTokenTypes.STRINGS
	override fun createElement(node: ASTNode): PsiElement = ASTWrapperPsiElement(node)
	override fun createFile(viewProvider: FileViewProvider) = CellaFile(viewProvider)
}

class CelpParserDefinition : ParserDefinition {
	override fun createLexer(project: Project?) = CelpLexer()
	override fun createParser(project: Project?) = flatParser
	override fun getFileNodeType() = CelpTokenTypes.FILE
	override fun getCommentTokens() = CelpTokenTypes.COMMENTS
	override fun getStringLiteralElements() = CelpTokenTypes.STRINGS
	override fun createElement(node: ASTNode): PsiElement = ASTWrapperPsiElement(node)
	override fun createFile(viewProvider: FileViewProvider) = CelpFile(viewProvider)
}
