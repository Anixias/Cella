package com.anixias.cella.server

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.codeInsight.TargetElementEvaluatorEx
import com.intellij.codeInsight.TargetElementEvaluatorEx2
import com.intellij.codeInsight.daemon.impl.HighlightInfoType
import com.intellij.codeInsight.navigation.actions.GotoDeclarationHandler
import com.intellij.lang.annotation.AnnotationHolder
import com.intellij.lang.annotation.Annotator
import com.intellij.lang.documentation.DocumentationMarkup
import com.intellij.model.Pointer
import com.intellij.openapi.application.runReadActionBlocking
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.richcopy.HtmlSyntaxInfoUtil
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
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

private val hoverTokens = setOf(CellaToken.IDENTIFIER, CellaToken.BUILTIN_TYPE, CellaToken.OPERATOR)

internal fun isNamed(element: PsiElement) = CellaTokenTypes.tokenOf(element.node?.elementType) in namedTokens

internal fun namedElementAt(file: PsiFile, offset: Int): PsiElement? =
	file.findElementAt(offset)?.takeIf(::isNamed)
		?: file.findElementAt(offset - 1)?.takeIf(::isNamed)

internal class CellaLocation(val file: VirtualFile, val offset: Int) {
	fun isAt(element: PsiElement) =
		element.containingFile?.virtualFile == file && element.textRange.startOffset == offset
}

internal fun locate(uri: String, position: Position): CellaLocation? {
	val file = LocalFileSystem.getInstance().findFileByNioFile(Path.of(URI(uri))) ?: return null
	val document = FileDocumentManager.getInstance().getDocument(file) ?: return null
	if (position.line >= document.lineCount)
		return null
	
	val lineStart = document.getLineStartOffset(position.line)
	return CellaLocation(file, (lineStart + position.character).coerceAtMost(document.getLineEndOffset(position.line)))
}

internal fun definitionAt(project: Project, file: VirtualFile, document: Document, offset: Int): CellaLocation? {
	val server = CellaServer.get(project)
	val position = server.position(document, offset)
	val result = server.await(server.request {
		it.textDocumentService.definition(DefinitionParams(TextDocumentIdentifier(CellaServer.uriOf(file)), position))
	}, 2000) ?: return null
	
	val (uri, start) = (if (result.isLeft) result.left.map { it.uri to it.range.start } else
		result.right.map { it.targetUri to it.targetSelectionRange.start }).firstOrNull() ?: return null
	
	return runReadActionBlocking { locate(uri, start) }
}

class CellaHoverAnnotator : Annotator {
	override fun annotate(element: PsiElement, holder: AnnotationHolder) {
		if (CellaTokenTypes.tokenOf(element.node?.elementType) in hoverTokens)
			holder.newSilentAnnotation(HighlightInfoType.SYMBOL_TYPE_SEVERITY).create()
	}
}

class CellaTargetElementEvaluator : TargetElementEvaluatorEx2(), TargetElementEvaluatorEx {
	override fun isIdentifierPart(file: PsiFile, text: CharSequence, offset: Int) =
		CellaTokenTypes.tokenOf(file.findElementAt(offset)?.node?.elementType) in hoverTokens
}

class CellaDocumentationTargetProvider : DocumentationTargetProvider {
	override fun documentationTargets(file: PsiFile, offset: Int): List<DocumentationTarget> {
		if (file.language != CellaLanguage)
			return emptyList()
		
		return listOfNotNull(namedElementAt(file, offset)?.let(::CellaDocumentationTarget))
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
		val location = definitionAt(project, file, editor.document, source.textRange.startOffset) ?: return null
		if (location.isAt(source))
			return null
		
		val target = PsiManager.getInstance(project).findFile(location.file)?.findElementAt(location.offset)
		return target?.let { arrayOf(it) }
	}
}
