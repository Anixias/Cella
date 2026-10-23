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
	private val highlightersKey = Key.create<List<Pair<Painted, RangeHighlighter>>>("cella.diagnostics.highlighters")
	
	private data class Painted(val start: Int, val end: Int, val message: String?, val severity: DiagnosticSeverity?,
		val isUnnecessary: Boolean)
	
	fun paint(document: Document, diagnostics: List<Diagnostic>) {
		val markup = DocumentMarkupModel.forDocument(document, project, true) as MarkupModelEx
		val existing = HashMap<Painted, ArrayDeque<RangeHighlighter>>()
		for ((painted, highlighter) in document.getUserData(highlightersKey).orEmpty()) {
			if (highlighter.isValid)
				existing.getOrPut(painted.copy(start = highlighter.startOffset, end = highlighter.endOffset)) {
					ArrayDeque()
				} += highlighter
		}
		
		val kept = diagnostics.map { diagnostic ->
			val painted = describe(document, diagnostic)
			painted to (existing[painted]?.removeFirstOrNull() ?: paint(markup, document, painted))
		}
		
		for (stale in existing.values.flatten())
			markup.removeHighlighter(stale)
		
		document.putUserData(highlightersKey, kept)
	}
	
	private fun describe(document: Document, diagnostic: Diagnostic): Painted {
		val start = offsetOf(document, diagnostic.range.start)
		var end = offsetOf(document, diagnostic.range.end).coerceAtLeast(start)
		val isUnnecessary = diagnostic.tags?.contains(DiagnosticTag.Unnecessary) == true
		if (!isUnnecessary && start == end && !isAfterEndOfLine(document, start))
			end++
		
		return Painted(start, end, diagnostic.message, diagnostic.severity, isUnnecessary)
	}
	
	private fun isAfterEndOfLine(document: Document, offset: Int) =
		offset >= document.textLength || document.charsSequence[offset] == '\n'
	
	private fun paint(markup: MarkupModelEx, document: Document, painted: Painted): RangeHighlighter {
		val (start, end) = painted
		if (painted.isUnnecessary)
			return markup.addRangeHighlighter(CodeInsightColors.NOT_USED_ELEMENT_ATTRIBUTES, start, end,
				HighlighterLayer.WARNING - 1, HighlighterTargetArea.EXACT_RANGE)
		
		val (type, key, layer) = when (painted.severity) {
			DiagnosticSeverity.Error -> Triple(HighlightInfoType.ERROR, CodeInsightColors.ERRORS_ATTRIBUTES,
				HighlighterLayer.ERROR)
			DiagnosticSeverity.Warning -> Triple(HighlightInfoType.WARNING, CodeInsightColors.WARNINGS_ATTRIBUTES,
				HighlighterLayer.WARNING)
			else -> Triple(HighlightInfoType.WEAK_WARNING, CodeInsightColors.WEAK_WARNING_ATTRIBUTES,
				HighlighterLayer.WARNING)
		}
		
		val isAfterEndOfLine = start == end
		val info = HighlightInfo.newHighlightInfo(type).range(start, end).descriptionAndTooltip(painted.message.orEmpty())
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
