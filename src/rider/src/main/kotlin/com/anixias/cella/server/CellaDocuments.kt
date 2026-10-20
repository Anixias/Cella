package com.anixias.cella.server

import com.intellij.lang.annotation.AnnotationHolder
import com.intellij.lang.annotation.ExternalAnnotator
import com.intellij.lang.annotation.HighlightSeverity
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.event.EditorFactoryEvent
import com.intellij.openapi.editor.event.EditorFactoryListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.util.Key
import com.intellij.openapi.util.TextRange
import com.intellij.codeInspection.ProblemHighlightType
import com.intellij.psi.PsiFile
import org.eclipse.lsp4j.Diagnostic
import org.eclipse.lsp4j.DiagnosticSeverity
import org.eclipse.lsp4j.DiagnosticTag
import org.eclipse.lsp4j.Position

class CellaEditorListener : EditorFactoryListener {
	private val listeningKey = Key.create<Boolean>("cella.listening")
	
	override fun editorCreated(event: EditorFactoryEvent) {
		val editor = event.editor
		val project = editor.project ?: return
		val document = editor.document
		val file = FileDocumentManager.getInstance().getFile(document)
		if (!CellaServer.isCella(file))
			return
		
		val server = CellaServer.get(project)
		if (document.getUserData(listeningKey) != true) {
			document.putUserData(listeningKey, true)
			document.addDocumentListener(object : DocumentListener {
				override fun documentChanged(event: DocumentEvent) {
					if (!project.isDisposed)
						server.change(event.document)
				}
			}, server)
		}
		
		trackHoveredCharacter(editor)
		server.open(document, file!!)
	}
	
	override fun editorReleased(event: EditorFactoryEvent) {
		val editor = event.editor
		val project = editor.project ?: return
		val document = editor.document
		val file = FileDocumentManager.getInstance().getFile(document) ?: return
		if (!CellaServer.isCella(file))
			return
		
		if (EditorFactory.getInstance().getEditors(document, project).none { it != editor })
			CellaServer.get(project).close(file)
	}
}

class CellaAnnotator : ExternalAnnotator<CellaAnnotator.Info, CellaAnnotator.Info>() {
	class Info(val document: Document, val diagnostics: List<Diagnostic>)
	
	override fun collectInformation(file: PsiFile, editor: Editor, hasErrors: Boolean): Info? {
		val virtualFile = file.virtualFile ?: return null
		return Info(editor.document, CellaServer.get(file.project).diagnosticsFor(virtualFile))
	}
	
	override fun doAnnotate(collectedInfo: Info?) = collectedInfo
	
	override fun apply(file: PsiFile, info: Info?, holder: AnnotationHolder) {
		val document = info?.document ?: return
		for (diagnostic in info.diagnostics) {
			val start = offsetOf(document, diagnostic.range.start)
			val end = offsetOf(document, diagnostic.range.end).coerceAtLeast(start)
			if (diagnostic.tags?.contains(DiagnosticTag.Unnecessary) == true) {
				holder.newSilentAnnotation(HighlightSeverity.INFORMATION)
					.range(TextRange(start, end))
					.highlightType(ProblemHighlightType.LIKE_UNUSED_SYMBOL)
					.create()
				
				continue
			}
			
			val severity = when (diagnostic.severity) {
				DiagnosticSeverity.Error -> HighlightSeverity.ERROR
				DiagnosticSeverity.Warning -> HighlightSeverity.WARNING
				else -> HighlightSeverity.WEAK_WARNING
			}
			
			val builder = holder.newAnnotation(severity, diagnostic.message)
			if (start == end && (end >= document.textLength || document.charsSequence[end] == '\n'))
				builder.range(TextRange(start, end)).afterEndOfLine().create()
			else
				builder.range(TextRange(start, if (start == end) end + 1 else end)).create()
		}
	}
	
	private fun offsetOf(document: Document, position: Position): Int {
		if (position.line >= document.lineCount)
			return document.textLength
		
		val lineStart = document.getLineStartOffset(position.line)
		return (lineStart + position.character).coerceAtMost(document.getLineEndOffset(position.line))
	}
}
