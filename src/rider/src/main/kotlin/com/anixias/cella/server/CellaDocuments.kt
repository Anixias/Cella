package com.anixias.cella.server

import com.intellij.codeInsight.daemon.impl.HighlightInfo
import com.intellij.codeInsight.daemon.impl.HighlightInfoType
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.EditorFactory
import com.intellij.openapi.editor.colors.CodeInsightColors
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.editor.event.EditorFactoryEvent
import com.intellij.openapi.editor.event.EditorFactoryListener
import com.intellij.openapi.editor.ex.MarkupModelEx
import com.intellij.openapi.editor.impl.DocumentMarkupModel
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.HighlighterTargetArea
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
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

class CellaDiagnosticsPainter(private val project: Project) {
	private val highlightersKey = Key.create<List<RangeHighlighter>>("cella.diagnostics.highlighters")
	
	fun paint(document: Document, diagnostics: List<Diagnostic>) {
		val markup = DocumentMarkupModel.forDocument(document, project, true) as MarkupModelEx
		document.getUserData(highlightersKey)?.forEach { if (it.isValid) markup.removeHighlighter(it) }
		document.putUserData(highlightersKey, diagnostics.map { paint(markup, document, it) })
	}
	
	private fun paint(markup: MarkupModelEx, document: Document, diagnostic: Diagnostic): RangeHighlighter {
		val start = offsetOf(document, diagnostic.range.start)
		var end = offsetOf(document, diagnostic.range.end).coerceAtLeast(start)
		if (diagnostic.tags?.contains(DiagnosticTag.Unnecessary) == true)
			return markup.addRangeHighlighter(CodeInsightColors.NOT_USED_ELEMENT_ATTRIBUTES, start, end,
				HighlighterLayer.WARNING - 1, HighlighterTargetArea.EXACT_RANGE)
		
		val (type, key, layer) = when (diagnostic.severity) {
			DiagnosticSeverity.Error -> Triple(HighlightInfoType.ERROR, CodeInsightColors.ERRORS_ATTRIBUTES,
				HighlighterLayer.ERROR)
			DiagnosticSeverity.Warning -> Triple(HighlightInfoType.WARNING, CodeInsightColors.WARNINGS_ATTRIBUTES,
				HighlighterLayer.WARNING)
			else -> Triple(HighlightInfoType.WEAK_WARNING, CodeInsightColors.WEAK_WARNING_ATTRIBUTES,
				HighlighterLayer.WARNING)
		}
		
		val isAfterEndOfLine = start == end && (end >= document.textLength || document.charsSequence[end] == '\n')
		if (start == end && !isAfterEndOfLine)
			end++
		
		val info = HighlightInfo.newHighlightInfo(type).range(start, end).descriptionAndTooltip(diagnostic.message)
			.createUnconditionally()
		
		return markup.addRangeHighlighterAndChangeAttributes(key, start, end, layer, HighlighterTargetArea.EXACT_RANGE,
			false) { highlighter ->
			highlighter.isAfterEndOfLine = isAfterEndOfLine
			highlighter.errorStripeTooltip = info
			highlighter.isThinErrorStripeMark = type == HighlightInfoType.WEAK_WARNING
			info.highlighter = highlighter
		}
	}
	
	private fun offsetOf(document: Document, position: Position): Int {
		if (position.line >= document.lineCount)
			return document.textLength
		
		val lineStart = document.getLineStartOffset(position.line)
		return (lineStart + position.character).coerceAtMost(document.getLineEndOffset(position.line))
	}
}
