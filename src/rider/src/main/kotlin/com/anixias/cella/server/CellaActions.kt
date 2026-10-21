package com.anixias.cella.server

import com.anixias.cella.CellaLanguage
import com.anixias.cella.lexer.CellaScanner
import com.intellij.codeInsight.daemon.impl.HighlightInfo
import com.intellij.codeInsight.daemon.impl.IntentionMenuContributor
import com.intellij.codeInsight.daemon.impl.ShowIntentionsPass
import com.intellij.codeInsight.hint.HintManager
import com.intellij.codeInsight.intention.IntentionAction
import com.intellij.codeInsight.intention.preview.IntentionPreviewInfo
import com.intellij.codeInsight.template.Template
import com.intellij.codeInsight.template.TemplateBuilderImpl
import com.intellij.codeInsight.template.TemplateEditingAdapter
import com.intellij.codeInsight.template.TemplateManager
import com.intellij.codeInsight.template.impl.ConstantNode
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.DataContext
import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.RangeMarker
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.psi.PsiDocumentManager
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.refactoring.rename.RenameHandler
import java.net.URI
import java.nio.file.Path
import org.eclipse.lsp4j.CodeAction
import org.eclipse.lsp4j.CodeActionContext
import org.eclipse.lsp4j.CodeActionKind
import org.eclipse.lsp4j.CodeActionParams
import org.eclipse.lsp4j.Location
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.PrepareRenameParams
import org.eclipse.lsp4j.Range
import org.eclipse.lsp4j.ReferenceContext
import org.eclipse.lsp4j.ReferenceParams
import org.eclipse.lsp4j.TextDocumentIdentifier
import org.eclipse.lsp4j.TextEdit
import org.eclipse.lsp4j.WorkspaceEdit

private fun offsetIn(document: Document, position: Position): Int {
	if (position.line >= document.lineCount)
		return document.textLength
	
	val lineStart = document.getLineStartOffset(position.line)
	return (lineStart + position.character).coerceAtMost(document.getLineEndOffset(position.line))
}

private fun documentOf(uri: String): Document? {
	val file = LocalFileSystem.getInstance().findFileByNioFile(Path.of(URI(uri))) ?: return null
	return FileDocumentManager.getInstance().getDocument(file)
}

private fun applyEdits(document: Document, edits: List<TextEdit>) {
	val ranges = edits.map { TextRange(offsetIn(document, it.range.start), offsetIn(document, it.range.end)) to it.newText }
	for ((range, text) in ranges.sortedByDescending { it.first.startOffset })
		document.replaceString(range.startOffset, range.endOffset, text)
}

internal fun applyWorkspaceEdit(project: Project, title: String, edit: WorkspaceEdit) {
	WriteCommandAction.runWriteCommandAction(project, title, null, {
		for ((uri, edits) in edit.changes.orEmpty()) {
			val document = documentOf(uri) ?: continue
			applyEdits(document, edits)
			PsiDocumentManager.getInstance(project).commitDocument(document)
		}
	})
}

class CellaCodeAction(private val action: CodeAction) : IntentionAction {
	override fun getText(): String = action.title
	
	override fun getFamilyName() = "Cella"
	
	override fun isAvailable(project: Project, editor: Editor?, file: PsiFile?) = true
	
	override fun startInWriteAction() = false
	
	override fun generatePreview(project: Project, editor: Editor, file: PsiFile): IntentionPreviewInfo =
		IntentionPreviewInfo.EMPTY
	
	override fun invoke(project: Project, editor: Editor?, file: PsiFile?) {
		action.edit?.let { applyWorkspaceEdit(project, action.title, it) }
	}
}

class CellaIntentionMenuContributor : IntentionMenuContributor {
	override fun collectActions(hostEditor: Editor, hostFile: PsiFile, intentions: ShowIntentionsPass.IntentionsInfo,
		passIdToShowIntentionsFor: Int, offset: Int) {
		val file = hostFile.virtualFile
		if (!CellaServer.isCella(file))
			return
		
		val server = CellaServer.get(hostFile.project)
		val document = hostEditor.document
		val selection = hostEditor.selectionModel
		val range = if (selection.hasSelection())
			Range(server.position(document, selection.selectionStart), server.position(document, selection.selectionEnd))
		else
			server.position(document, offset).let { Range(it, it) }
		
		val actions = server.await(server.request {
			it.textDocumentService.codeAction(CodeActionParams(TextDocumentIdentifier(CellaServer.uriOf(file)), range,
				CodeActionContext(emptyList())))
		}, 2000) ?: return
		
		for (action in actions.mapNotNull { it.right }) {
			val descriptor = HighlightInfo.IntentionActionDescriptor(CellaCodeAction(action), emptyList(), action.title,
				null, null, null, null)
			
			if (action.kind == CodeActionKind.QuickFix)
				intentions.errorFixesToShow += descriptor
			else
				intentions.intentionsToShow += descriptor
		}
	}
}

class CellaRenameHandler : RenameHandler {
	private val namePattern = Regex("[\\p{L}_][\\p{L}\\p{N}_]*")
	
	override fun isAvailableOnDataContext(dataContext: DataContext): Boolean {
		val file = CommonDataKeys.PSI_FILE.getData(dataContext) ?: return false
		return CommonDataKeys.EDITOR.getData(dataContext) != null && file.language == CellaLanguage
	}
	
	override fun invoke(project: Project, editor: Editor, file: PsiFile, dataContext: DataContext) {
		val virtualFile = file.virtualFile ?: return
		val document = editor.document
		val server = CellaServer.get(project)
		val uri = CellaServer.uriOf(virtualFile)
		val position = server.position(document, editor.caretModel.offset)
		var oldName: String? = null
		var locations: List<Location> = emptyList()
		val finished = ProgressManager.getInstance().runProcessWithProgressSynchronously({
			val prepared = server.await(server.request {
				it.textDocumentService.prepareRename(PrepareRenameParams(TextDocumentIdentifier(uri), position))
			}) ?: return@runProcessWithProgressSynchronously
			
			oldName = prepared.second?.placeholder ?: return@runProcessWithProgressSynchronously
			locations = server.await(server.request {
				it.textDocumentService.references(ReferenceParams(TextDocumentIdentifier(uri), position,
					ReferenceContext(true)))
			}, 10000).orEmpty()
		}, "Finding usages", true, project)
		
		val name = oldName
		if (!finished || name == null || locations.isEmpty()) {
			if (finished)
				HintManager.getInstance().showErrorHint(editor, "This can't be renamed")
			
			return
		}
		
		val key = CellaServer.keyOf(uri)
		val (local, others) = locations.partition { CellaServer.keyOf(it.uri) == key }
		val ranges = local.map { TextRange(offsetIn(document, it.range.start), offsetIn(document, it.range.end)) }
			.distinct()
			.sortedBy { it.startOffset }
		
		val caret = editor.caretModel.offset
		val primary = ranges.firstOrNull { caret in it.startOffset..it.endOffset } ?: ranges.firstOrNull() ?: return
		val markers = ranges.map { document.createRangeMarker(it).apply { isGreedyToLeft = true; isGreedyToRight = true } }
		val primaryMarker = markers[ranges.indexOf(primary)]
		val otherEdits = others.groupBy { it.uri }
		
		WriteCommandAction.runWriteCommandAction(project, "Rename", null, {
			val builder = TemplateBuilderImpl(file as PsiElement)
			builder.replaceRange(primary, "NAME", ConstantNode(name), true)
			for ((index, range) in ranges.withIndex()) {
				if (range != primary)
					builder.replaceElement(range, "NAME_$index", "NAME", false)
			}
			
			editor.caretModel.moveToOffset(0)
			val template = builder.buildInlineTemplate()
			template.isToShortenLongNames = false
			template.isToReformat = false
			editor.caretModel.moveToOffset(primary.startOffset)
			TemplateManager.getInstance(project).startTemplate(editor, template, object : TemplateEditingAdapter() {
				override fun templateFinished(template: Template, brokenOff: Boolean) =
					finish(project, editor, name, primaryMarker, markers, otherEdits)
				
				override fun templateCancelled(template: Template?) = markers.forEach(RangeMarker::dispose)
			})
		})
	}
	
	private fun finish(project: Project, editor: Editor, oldName: String, primary: RangeMarker,
		markers: List<RangeMarker>, others: Map<String, List<Location>>) {
		val document = editor.document
		val newName = document.getText(primary.textRange)
		val problem = when {
			newName == oldName -> ""
			CellaScanner.isKeyword(newName) -> "'$newName' is a keyword"
			!namePattern.matches(newName) -> "'$newName' isn't a valid name"
			else -> null
		}
		
		WriteCommandAction.runWriteCommandAction(project, "Rename", null, {
			if (problem != null) {
				for (marker in markers.filter { it.isValid }.sortedByDescending { it.startOffset })
					document.replaceString(marker.startOffset, marker.endOffset, oldName)
			} else {
				for ((uri, locations) in others) {
					val target = documentOf(uri) ?: continue
					applyEdits(target, locations.map { TextEdit(it.range, newName) })
				}
			}
		})
		
		markers.forEach(RangeMarker::dispose)
		if (!problem.isNullOrEmpty())
			HintManager.getInstance().showErrorHint(editor, problem)
	}
	
	override fun invoke(project: Project, elements: Array<out PsiElement>, dataContext: DataContext?) {
	}
}
