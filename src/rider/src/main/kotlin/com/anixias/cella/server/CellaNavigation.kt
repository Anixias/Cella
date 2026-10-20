package com.anixias.cella.server

import com.anixias.cella.CellaLanguage
import com.anixias.cella.CelpLanguage
import com.anixias.cella.highlighting.CellaHighlighter
import com.anixias.cella.lexer.CellaScanner
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.anixias.cella.lexer.CelpToken
import com.anixias.cella.lexer.CelpTokenTypes
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
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.EditorMouseEvent
import com.intellij.openapi.editor.event.EditorMouseListener
import com.intellij.openapi.editor.event.EditorMouseMotionListener
import com.intellij.openapi.editor.richcopy.HtmlSyntaxInfoUtil
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.openapi.util.text.StringUtil
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
import com.intellij.psi.util.PsiTreeUtil
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

private val keywordTokens = setOf(CellaToken.CONTROL_KEYWORD, CellaToken.VALUE_KEYWORD, CellaToken.OPERATOR_KEYWORD)

private fun isKeywordName(element: PsiElement): Boolean {
	if (CellaTokenTypes.tokenOf(element.node?.elementType) !in keywordTokens)
		return false
	
	if (element.text == "panic")
		return true
	
	val previous = PsiTreeUtil.prevVisibleLeaf(element)?.node?.elementType
	val next = PsiTreeUtil.nextVisibleLeaf(element)?.node?.elementType
	return CellaTokenTypes.tokenOf(previous) == CellaToken.DOT || CellaTokenTypes.tokenOf(next) == CellaToken.COLON
}

private fun isHoverable(element: PsiElement) =
	CellaTokenTypes.tokenOf(element.node?.elementType) in hoverTokens || isKeywordName(element)

internal fun isNamed(element: PsiElement) =
	CellaTokenTypes.tokenOf(element.node?.elementType) in namedTokens || isKeywordName(element)

internal fun isProjectString(element: PsiElement) =
	element.language == CelpLanguage && CelpTokenTypes.tokenOf(element.node?.elementType) == CelpToken.STRING

private fun projectStringAt(file: PsiFile, offset: Int): PsiElement? = file.findElementAt(offset)?.takeIf(::isProjectString)

internal fun namedElementAt(file: PsiFile, offset: Int): PsiElement? =
	file.findElementAt(offset)?.takeIf(::isNamed)
		?: file.findElementAt(offset - 1)?.takeIf(::isNamed)

private val hoveredCharacterKey = Key.create<Int>("cella.hoveredCharacter")

internal fun trackHoveredCharacter(editor: Editor) {
	editor.addEditorMouseMotionListener(object : EditorMouseMotionListener {
		override fun mouseMoved(event: EditorMouseEvent) {
			val point = event.mouseEvent.point
			val offset = editor.logicalPositionToOffset(editor.xyToLogicalPosition(point))
			val character = if (offset > 0 && point.x < editor.offsetToXY(offset).x) offset - 1 else offset
			editor.putUserData(hoveredCharacterKey, character)
		}
	})
	
	editor.addEditorMouseListener(object : EditorMouseListener {
		override fun mouseExited(event: EditorMouseEvent) = editor.putUserData(hoveredCharacterKey, null)
	})
}

private fun hoveredElementAt(file: PsiFile, offset: Int): PsiElement? {
	val document = file.viewProvider.document ?: return null
	val character = EditorFactory.getInstance().getEditors(document)
		.firstNotNullOfOrNull { it.getUserData(hoveredCharacterKey) }
		?.takeIf { it in offset - 1..offset } ?: return null
	
	return file.findElementAt(character)?.takeIf(::isNamed)
}

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
		if (isHoverable(element))
			holder.newSilentAnnotation(HighlightInfoType.SYMBOL_TYPE_SEVERITY).create()
	}
}

class CellaTargetElementEvaluator : TargetElementEvaluatorEx2(), TargetElementEvaluatorEx {
	override fun isIdentifierPart(file: PsiFile, text: CharSequence, offset: Int) =
		file.findElementAt(offset)?.let(::isHoverable) == true
}

class CellaDocumentationTargetProvider : DocumentationTargetProvider {
	override fun documentationTargets(file: PsiFile, offset: Int): List<DocumentationTarget> {
		if (file.language == CelpLanguage)
			return listOfNotNull(projectStringAt(file, offset)?.let {
				CellaDocumentationTarget(it, offset - it.textRange.startOffset)
			})
		
		if (file.language != CellaLanguage)
			return emptyList()
		
		return listOfNotNull((hoveredElementAt(file, offset) ?: namedElementAt(file, offset))
			?.let(::CellaDocumentationTarget))
	}
}

class CellaDocumentationTarget(private val element: PsiElement, private val inside: Int = 0) : DocumentationTarget {
	override fun createPointer(): Pointer<out DocumentationTarget> {
		val pointer = SmartPointerManager.createPointer(element)
		return Pointer { pointer.element?.let { CellaDocumentationTarget(it, inside) } }
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
				CellaServer.get(file.project).position(document, element.textRange.startOffset + inside))
		} ?: return null
		
		val server = CellaServer.get(project)
		val hover = server.await(server.request {
			it.textDocumentService.hover(HoverParams(TextDocumentIdentifier(uri), position))
		}) ?: return null
		
		val code = hover.contents?.right?.value ?: return null
		return DocumentationMarkup.DEFINITION_START + highlightSignature(code) + DocumentationMarkup.DEFINITION_END
	}
	
	private fun highlightSignature(code: String): String {
		val scanner = CellaScanner()
		scanner.start(code, 0, code.length, CellaScanner.NORMAL)
		val tokens = mutableListOf<Pair<CellaToken, String>>()
		while (true) {
			val type = scanner.tokenType ?: break
			tokens += type to code.substring(scanner.tokenStart, scanner.tokenEnd)
			scanner.advance()
		}
		
		val html = StringBuilder()
		for ((index, token) in tokens.withIndex()) {
			val (type, text) = token
			val key = if (type == CellaToken.IDENTIFIER && namesKind(tokens, index))
				CellaScanner.keywordCategory(text)?.let(CellaHighlighter::colorOf)
			else
				CellaHighlighter.colorOf(type)
			
			val escaped = StringUtil.escapeXmlEntities(text)
			if (key == null)
				html.append(escaped)
			else
				HtmlSyntaxInfoUtil.appendStyledSpan(html, key, escaped, 1f)
		}
		
		return html.toString()
	}
	
	private fun namesKind(tokens: List<Pair<CellaToken, String>>, index: Int): Boolean {
		val previous = tokens.subList(0, index).lastOrNull { it.first != CellaToken.WHITESPACE }?.first
		val next = tokens.subList(index + 1, tokens.size).firstOrNull { it.first != CellaToken.WHITESPACE }?.first
		return previous != CellaToken.DOT && next != CellaToken.COLON
	}
}

class CellaGotoDeclarationHandler : GotoDeclarationHandler {
	override fun getGotoDeclarationTargets(source: PsiElement?, offset: Int, editor: Editor): Array<PsiElement>? {
		if (source == null || !(source.language == CellaLanguage && isNamed(source) || isProjectString(source)))
			return null
		
		val project = editor.project ?: return null
		val file = source.containingFile?.virtualFile ?: return null
		val start = if (isProjectString(source)) offset else source.textRange.startOffset
		val location = definitionAt(project, file, editor.document, start) ?: return null
		if (location.isAt(source))
			return null
		
		val target = PsiManager.getInstance(project).findFile(location.file)?.findElementAt(location.offset)
		return target?.let { arrayOf(it) }
	}
}
