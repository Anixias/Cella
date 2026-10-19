package com.anixias.cella.server

import com.anixias.cella.CelpFileType
import com.anixias.cella.highlighting.CellaColors
import com.anixias.cella.highlighting.CellaHighlighter
import com.anixias.cella.lexer.CellaScanner
import com.intellij.codeInsight.daemon.DaemonCodeAnalyzer
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.editor.impl.DocumentMarkupModel
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.HighlighterTargetArea
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiDocumentManager
import org.eclipse.lsp4j.DocumentSymbolParams
import org.eclipse.lsp4j.SemanticTokensParams
import org.eclipse.lsp4j.TextDocumentIdentifier

class CellaSemanticHighlighter(private val project: Project, private val server: CellaServer) {
	companion object {
		private val highlightersKey = Key.create<List<RangeHighlighter>>("cella.semantic.highlighters")
		private val runnablesKey = Key.create<List<RunnableFunction>>("cella.runnables")
		
		fun runnableAt(document: Document, range: TextRange) = document.getUserData(runnablesKey)
			?.firstOrNull { it.start == range.startOffset && it.end == range.endOffset }
	}
	
	fun request(document: Document, uri: String, version: Int) {
		server.request { it.textDocumentService.semanticTokensFull(SemanticTokensParams(TextDocumentIdentifier(uri))) }
			.thenAccept { tokens ->
				val data = tokens?.data ?: return@thenAccept
				ApplicationManager.getApplication().invokeLater({ apply(document, uri, version, data) }, project.disposed)
			}
	}
	
	private fun apply(document: Document, uri: String, version: Int, data: List<Int>) {
		if (server.versionOf(uri) != version)
			return
		
		val legend = server.legend ?: return
		val isProject = FileDocumentManager.getInstance().getFile(document)?.fileType == CelpFileType
		val markup = DocumentMarkupModel.forDocument(document, project, true)
		document.getUserData(highlightersKey)?.forEach { if (it.isValid) markup.removeHighlighter(it) }
		
		val added = ArrayList<RangeHighlighter>(data.size / 5)
		var line = 0
		var character = 0
		for (index in data.indices step 5) {
			if (index + 4 >= data.size)
				break
			
			line += data[index]
			character = if (data[index] == 0) character + data[index + 1] else data[index + 1]
			if (line >= document.lineCount)
				break
			
			val start = document.getLineStartOffset(line) + character
			val end = start + data[index + 2]
			if (end > document.getLineEndOffset(line))
				continue
			
			val type = legend.tokenTypes.getOrNull(data[index + 3]) ?: continue
			val modifiers = legend.tokenModifiers.filterIndexed { bit, _ -> data[index + 4] and (1 shl bit) != 0 }.toSet()
			val key = (if (isProject) projectColorOf(type) else colorOf(type, modifiers,
				document.charsSequence.subSequence(start, end).toString())) ?: continue
			val highlighter = markup.addRangeHighlighter(key, start, end, HighlighterLayer.SYNTAX + 1,
				HighlighterTargetArea.EXACT_RANGE)
			
			added += highlighter
		}
		
		document.putUserData(highlightersKey, added)
		requestRunnables(document, uri, version)
	}
	
	private fun requestRunnables(document: Document, uri: String, version: Int) {
		server.request { it.runnables(DocumentSymbolParams(TextDocumentIdentifier(uri))) }
			.thenAccept { runnables ->
				ApplicationManager.getApplication().invokeLater({ applyRunnables(document, uri, version, runnables.orEmpty()) },
					project.disposed)
			}
	}
	
	private fun applyRunnables(document: Document, uri: String, version: Int, runnables: List<CellaRunnable>) {
		if (server.versionOf(uri) != version)
			return
		
		val found = runnables.mapNotNull { runnable ->
			val range = runnable.range ?: return@mapNotNull null
			if (range.start.line >= document.lineCount || range.end.line >= document.lineCount)
				return@mapNotNull null
			
			RunnableFunction(document.getLineStartOffset(range.start.line) + range.start.character,
				document.getLineStartOffset(range.end.line) + range.end.character, runnable.name, runnable.main)
		}
		
		if (document.getUserData(runnablesKey) == found)
			return
		
		document.putUserData(runnablesKey, found)
		PsiDocumentManager.getInstance(project).getPsiFile(document)
			?.let { DaemonCodeAnalyzer.getInstance(project).restart(it) }
	}
	
	data class RunnableFunction(val start: Int, val end: Int, val name: String, val isMain: Boolean)
	
	private fun projectColorOf(type: String) = if (type == "property") CellaColors.PROJECT_SETTING else null

	private fun colorOf(type: String, modifiers: Set<String>, text: String): TextAttributesKey? = when (type) {
		"struct" -> when {
			"drop" in modifiers -> CellaColors.DROP_TYPE
			"ref" in modifiers -> CellaColors.REF_RECORD
			else -> CellaColors.RECORD
		}
		
		"enum" -> CellaColors.ENUM
		"interface" -> CellaColors.TRAIT
		"typeParameter" -> CellaColors.TYPE_PARAMETER
		"type" -> CellaColors.TYPE
		"function" -> CellaColors.FUNCTION
		"method" -> CellaColors.METHOD
		"parameter" -> CellaColors.PARAMETER
		"variable" -> when {
			"static" in modifiers && "readonly" in modifiers -> CellaColors.CONSTANT
			"static" in modifiers -> CellaColors.GLOBAL_VARIABLE
			"readonly" in modifiers -> CellaColors.LOCAL_VALUE
			else -> CellaColors.LOCAL_VARIABLE
		}
		
		"property" -> CellaColors.FIELD
		"enumMember" -> CellaColors.ENUM_CASE
		"keyword" -> when {
			"defaultLibrary" in modifiers -> CellaColors.BUILTIN_TYPE
			else -> CellaScanner.keywordCategory(text)?.let(CellaHighlighter::colorOf)
		}
		else -> null
	}
}
