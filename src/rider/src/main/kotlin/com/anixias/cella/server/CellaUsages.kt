package com.anixias.cella.server

import com.anixias.cella.CellaFileType
import com.anixias.cella.CellaIcons
import com.anixias.cella.CellaLanguage
import com.intellij.find.actions.FindUsagesAction
import com.intellij.find.usages.api.PsiUsage
import com.intellij.find.usages.api.SearchTarget
import com.intellij.find.usages.api.Usage
import com.intellij.find.usages.api.UsageHandler
import com.intellij.find.usages.api.UsageSearchParameters
import com.intellij.find.usages.api.UsageSearcher
import com.intellij.model.Pointer
import com.intellij.model.Symbol
import com.intellij.model.psi.PsiSymbolDeclaration
import com.intellij.model.psi.PsiSymbolDeclarationProvider
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.AnActionResult
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.DataSink
import com.intellij.openapi.actionSystem.DataSnapshot
import com.intellij.openapi.actionSystem.UiDataRule
import com.intellij.openapi.actionSystem.ex.AnActionListener
import com.intellij.openapi.application.runReadActionBlocking
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.backend.presentation.TargetPresentation
import com.intellij.psi.PsiDocumentManager
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiManager
import com.intellij.util.AbstractQuery
import com.intellij.util.Processor
import com.intellij.util.Query
import org.eclipse.lsp4j.ReferenceContext
import org.eclipse.lsp4j.ReferenceParams
import org.eclipse.lsp4j.TextDocumentIdentifier

class CellaSymbol(val file: VirtualFile, val offset: Int, val name: String) : Symbol, SearchTarget {
	override fun createPointer(): Pointer<CellaSymbol> = Pointer.hardPointer(this)
	
	override fun presentation() = TargetPresentation.builder(name).icon(CellaIcons.FILE).presentation()
	
	override val usageHandler: UsageHandler
		get() = UsageHandler.createEmptyUsageHandler(name)
	
	override fun equals(other: Any?) = other is CellaSymbol && other.file == file && other.offset == offset
	
	override fun hashCode() = 31 * file.hashCode() + offset
}

class CellaActionListener : AnActionListener {
	companion object {
		@Volatile
		var currentAction: String? = null
			private set
	}
	
	override fun beforeActionPerformed(action: AnAction, event: AnActionEvent) {
		currentAction = ActionManager.getInstance().getId(action)
	}
	
	override fun afterActionPerformed(action: AnAction, event: AnActionEvent, result: AnActionResult) {
		currentAction = null
	}
}

class CellaDeclarationProvider : PsiSymbolDeclarationProvider {
	override fun getDeclarations(element: PsiElement, offsetInElement: Int): Collection<PsiSymbolDeclaration> {
		if (CellaActionListener.currentAction != "GotoDeclaration" || element.language != CellaLanguage ||
			!isNamed(element))
			return emptyList()
		
		val file = element.containingFile ?: return emptyList()
		val virtualFile = file.virtualFile ?: return emptyList()
		val document = file.viewProvider.document ?: return emptyList()
		val location = definitionAt(file.project, virtualFile, document, element.textRange.startOffset)
		if (location == null || !location.isAt(element))
			return emptyList()
		
		val symbol = CellaSymbol(virtualFile, location.offset, element.text)
		return listOf(object : PsiSymbolDeclaration {
			override fun getDeclaringElement() = element
			override fun getRangeInDeclaringElement() = TextRange(0, element.textLength)
			override fun getSymbol() = symbol
		})
	}
}

class CellaSearchTargetsRule : UiDataRule {
	override fun uiDataSnapshot(sink: DataSink, snapshot: DataSnapshot) {
		val editor = snapshot[CommonDataKeys.EDITOR] ?: return
		val project = editor.project ?: return
		val document = editor.document
		val file = FileDocumentManager.getInstance().getFile(document) ?: return
		if (file.fileType != CellaFileType)
			return
		
		val offset = editor.caretModel.offset
		sink.lazyValue(FindUsagesAction.SEARCH_TARGETS) {
			searchTargetAt(project, file, document, offset)?.let { listOf(it) }
		}
	}
	
	private fun searchTargetAt(project: Project, file: VirtualFile, document: Document, offset: Int): SearchTarget? {
		val start = runReadActionBlocking {
			PsiDocumentManager.getInstance(project).getPsiFile(document)?.let { namedElementAt(it, offset) }
				?.textRange?.startOffset
		} ?: return null
		
		val location = definitionAt(project, file, document, start) ?: return null
		val name = runReadActionBlocking {
			PsiManager.getInstance(project).findFile(location.file)?.findElementAt(location.offset)?.text
		} ?: return null
		
		return CellaSymbol(location.file, location.offset, name)
	}
}

class CellaUsageSearcher : UsageSearcher {
	override fun collectSearchRequest(parameters: UsageSearchParameters): Query<out Usage>? {
		val symbol = parameters.target as? CellaSymbol ?: return null
		return CellaUsageQuery(parameters.project, symbol)
	}
}

private class CellaUsageQuery(private val project: Project, private val symbol: CellaSymbol) : AbstractQuery<Usage>() {
	override fun processResults(consumer: Processor<in Usage>): Boolean {
		val server = CellaServer.get(project)
		val position = runReadActionBlocking {
			FileDocumentManager.getInstance().getDocument(symbol.file)?.let { server.position(it, symbol.offset) }
		} ?: return true
		
		val locations = server.await(server.request {
			it.textDocumentService.references(ReferenceParams(TextDocumentIdentifier(CellaServer.uriOf(symbol.file)),
				position, ReferenceContext(false)))
		}, 10000) ?: return true
		
		return runReadActionBlocking {
			locations.all { location ->
				val start = locate(location.uri, location.range.start) ?: return@all true
				val end = locate(location.uri, location.range.end) ?: return@all true
				val psi = PsiManager.getInstance(project).findFile(start.file) ?: return@all true
				consumer.process(PsiUsage.textUsage(psi, TextRange(start.offset, end.offset)))
			}
		}
	}
}
