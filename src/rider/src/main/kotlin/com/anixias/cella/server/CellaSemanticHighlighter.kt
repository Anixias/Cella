package com.anixias.cella.server

import com.anixias.cella.highlighting.CellaColors
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.editor.impl.DocumentMarkupModel
import com.intellij.openapi.editor.markup.HighlighterLayer
import com.intellij.openapi.editor.markup.HighlighterTargetArea
import com.intellij.openapi.editor.markup.RangeHighlighter
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import org.eclipse.lsp4j.SemanticTokensParams
import org.eclipse.lsp4j.TextDocumentIdentifier

class CellaSemanticHighlighter(private val project: Project, private val server: CellaServer) {
	private val highlightersKey = Key.create<List<RangeHighlighter>>("cella.semantic.highlighters")
	
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
			val key = colorOf(type, modifiers) ?: continue
			added += markup.addRangeHighlighter(key, start, end, HighlighterLayer.SYNTAX + 1,
				HighlighterTargetArea.EXACT_RANGE)
		}
		
		document.putUserData(highlightersKey, added)
	}
	
	private fun colorOf(type: String, modifiers: Set<String>): TextAttributesKey? = when (type) {
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
		"keyword" -> CellaColors.BUILTIN_TYPE
		else -> null
	}
}
