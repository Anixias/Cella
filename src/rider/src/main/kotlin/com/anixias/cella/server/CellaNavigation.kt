package com.anixias.cella.server

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.codeInsight.navigation.actions.GotoDeclarationHandler
import com.intellij.lang.documentation.DocumentationMarkup
import com.intellij.model.Pointer
import com.intellij.openapi.application.runReadActionBlocking
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.project.Project
import com.intellij.openapi.editor.richcopy.HtmlSyntaxInfoUtil
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.platform.backend.documentation.DocumentationResult
import com.intellij.platform.backend.documentation.DocumentationTarget
import com.intellij.platform.backend.documentation.DocumentationTargetProvider
import com.intellij.platform.backend.presentation.TargetPresentation
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.PsiManager
import com.intellij.psi.SmartPointerManager
import org.eclipse.lsp4j.DefinitionParams
import org.eclipse.lsp4j.HoverParams
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.TextDocumentIdentifier
import java.net.URI
import java.nio.file.Path

private val namedTokens = setOf(
	CellaToken.IDENTIFIER, CellaToken.BUILTIN_TYPE, CellaToken.OPERATOR, CellaToken.DOT,
	CellaToken.DECLARATION_KEYWORD, CellaToken.MODIFIER_KEYWORD
)

private fun isNamed(element: PsiElement) = CellaTokenTypes.tokenOf(element.node?.elementType) in namedTokens

class CellaDocumentationTargetProvider : DocumentationTargetProvider {
	override fun documentationTargets(file: PsiFile, offset: Int): List<DocumentationTarget> {
		if (file.language != CellaLanguage)
			return emptyList()
		
		val element = file.findElementAt(offset) ?: return emptyList()
		return if (isNamed(element)) listOf(CellaDocumentationTarget(element)) else emptyList()
	}
}

class CellaDocumentationTarget(private val element: PsiElement) : DocumentationTarget {
	override fun createPointer(): Pointer<out DocumentationTarget> {
		val pointer = SmartPointerManager.createPointer(element)
		return Pointer { pointer.element?.let(::CellaDocumentationTarget) }
	}
	
	override fun computePresentation() = TargetPresentation.builder(element.text).presentation()
	
	override fun computeDocumentationHint() = describe()
	
	override fun computeDocumentation() = describe()?.let { DocumentationResult.documentation(it) }
	
	private fun describe(): String? {
		val (project, uri, position) = runReadActionBlocking<Triple<Project, String, Position>?> {
			val file = element.containingFile ?: return@runReadActionBlocking null
			val virtualFile = file.virtualFile ?: return@runReadActionBlocking null
			val document = file.viewProvider.document ?: return@runReadActionBlocking null
			Triple(file.project, CellaServer.uriOf(virtualFile),
				CellaServer.get(file.project).position(document, element.textRange.startOffset))
		} ?: return null
		
		val server = CellaServer.get(project)
		val hover = server.await(server.request {
			it.textDocumentService.hover(HoverParams(TextDocumentIdentifier(uri), position))
		}) ?: return null
		
		val value = hover.contents?.right?.value ?: return null
		val code = value.removePrefix("```cella").removeSuffix("```").trim()
		val html = StringBuilder()
		runReadActionBlocking {
			HtmlSyntaxInfoUtil.appendHighlightedByLexerAndEncodedAsHtmlCodeSnippet(html, project, CellaLanguage, code, 1f)
		}
		
		return DocumentationMarkup.DEFINITION_START + html + DocumentationMarkup.DEFINITION_END
	}
}

class CellaGotoDeclarationHandler : GotoDeclarationHandler {
	override fun getGotoDeclarationTargets(source: PsiElement?, offset: Int, editor: Editor): Array<PsiElement>? {
		if (source == null || source.language != CellaLanguage || !isNamed(source))
			return null
		
		val project = editor.project ?: return null
		val file = source.containingFile?.virtualFile ?: return null
		val server = CellaServer.get(project)
		val position = server.position(editor.document, source.textRange.startOffset)
		val result = server.await(server.request {
			it.textDocumentService.definition(DefinitionParams(TextDocumentIdentifier(CellaServer.uriOf(file)), position))
		}, 2000) ?: return null
		
		val locations = if (result.isLeft) result.left.map { it.uri to it.range.start } else
			result.right.map { it.targetUri to it.targetSelectionRange.start }
		
		return locations.mapNotNull { (uri, start) ->
			val target = LocalFileSystem.getInstance().findFileByNioFile(Path.of(URI(uri))) ?: return@mapNotNull null
			val psi = PsiManager.getInstance(project).findFile(target) ?: return@mapNotNull null
			val document = psi.viewProvider.document ?: return@mapNotNull null
			if (start.line >= document.lineCount)
				return@mapNotNull null
			
			psi.findElementAt(document.getLineStartOffset(start.line) + start.character)
		}.toTypedArray()
	}
}
