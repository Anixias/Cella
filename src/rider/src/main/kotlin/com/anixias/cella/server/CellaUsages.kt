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
import com.intellij.model.psi.ImplicitReferenceProvider
import com.intellij.model.psi.PsiSymbolDeclaration
import com.intellij.model.psi.PsiSymbolDeclarationProvider
import com.intellij.model.psi.PsiSymbolReference
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.DataSink
import com.intellij.openapi.actionSystem.DataSnapshot
import com.intellij.openapi.actionSystem.UiDataRule
import com.intellij.openapi.application.runReadActionBlocking
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.openapi.util.TextRange
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.backend.presentation.TargetPresentation
import com.intellij.psi.PsiDocumentManager
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.PsiManager
import com.intellij.psi.search.LocalSearchScope
import com.intellij.psi.search.SearchScope
import com.intellij.util.AbstractQuery
import com.intellij.util.Processor
import com.intellij.util.Query
import org.eclipse.lsp4j.HoverParams
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.ReferenceContext
import org.eclipse.lsp4j.ReferenceParams
import org.eclipse.lsp4j.TextDocumentIdentifier
import java.util.concurrent.ConcurrentHashMap

class CellaSymbol(val file: VirtualFile, val offset: Int, val name: String) : Symbol, SearchTarget {
	override fun createPointer(): Pointer<CellaSymbol> = Pointer.hardPointer(this)
	
	override fun presentation() = TargetPresentation.builder(name).icon(CellaIcons.FILE).presentation()
	
	override val usageHandler: UsageHandler
		get() = UsageHandler.createEmptyUsageHandler(name)
	
	override fun equals(other: Any?) = other is CellaSymbol && other.file == file && other.offset == offset
	
	override fun hashCode() = 31 * file.hashCode() + offset
}

private fun cellaNameAt(element: PsiElement): Pair<PsiFile, Document>? {
	if (element.language != CellaLanguage || !isNamed(element))
		return null
	
	val file = element.containingFile ?: return null
	val document = file.viewProvider.document ?: return null
	return file to document
}

private sealed interface NameResolution {
	data object Declared : NameResolution
	
	class Referenced(val location: CellaLocation) : NameResolution
}

private val resolutionsKey = Key.create<Pair<Long, ConcurrentHashMap<Int, NameResolution>>>("cella.name.resolutions")

private fun resolveName(file: PsiFile, document: Document, element: PsiElement): NameResolution? {
	val virtualFile = file.virtualFile ?: return null
	val stamp = document.modificationStamp
	val start = element.textRange.startOffset
	val resolutions = document.getUserData(resolutionsKey)?.takeIf { it.first == stamp }?.second
		?: ConcurrentHashMap<Int, NameResolution>().also { document.putUserData(resolutionsKey, stamp to it) }
	
	resolutions[start]?.let { return it }
	val location = definitionAt(file.project, virtualFile, document, start) ?: return null
	val resolution = if (location.isAt(element)) NameResolution.Declared else NameResolution.Referenced(location)
	resolutions[start] = resolution
	return resolution
}

private fun symbolAt(project: Project, location: CellaLocation): CellaSymbol? {
	val name = runReadActionBlocking {
		PsiManager.getInstance(project).findFile(location.file)?.findElementAt(location.offset)?.text
	} ?: return null
	
	return CellaSymbol(location.file, location.offset, name)
}

private class ProjectTarget(val range: TextRange, val location: CellaLocation)

private val projectTargetsKey = Key.create<Pair<Long, ConcurrentHashMap<Int, ProjectTarget>>>("cella.project.targets")

private fun offsetOf(document: Document, position: Position) =
	document.getLineStartOffset(position.line) + position.character

private fun projectTargetAt(file: PsiFile, document: Document, offset: Int): ProjectTarget? {
	val virtualFile = file.virtualFile ?: return null
	val stamp = document.modificationStamp
	val targets = document.getUserData(projectTargetsKey)?.takeIf { it.first == stamp }?.second
		?: ConcurrentHashMap<Int, ProjectTarget>().also { document.putUserData(projectTargetsKey, stamp to it) }
	
	targets[offset]?.let { return it }
	val server = CellaServer.get(file.project)
	val position = server.position(document, offset)
	val range = server.await(server.request {
		it.textDocumentService.hover(HoverParams(TextDocumentIdentifier(CellaServer.uriOf(virtualFile)), position))
	}, 2000)?.range ?: return null
	
	val location = definitionAt(file.project, virtualFile, document, offset) ?: return null
	val target = ProjectTarget(TextRange(offsetOf(document, range.start), offsetOf(document, range.end)), location)
	targets[offset] = target
	return target
}

private fun projectReference(element: PsiElement, offsetInElement: Int): PsiSymbolReference? {
	val file = element.containingFile ?: return null
	val document = file.viewProvider.document ?: return null
	val elementStart = element.textRange.startOffset
	val target = projectTargetAt(file, document, elementStart + offsetInElement) ?: return null
	val symbol = symbolAt(file.project, target.location) ?: return null
	return object : PsiSymbolReference {
		override fun getElement() = element
		override fun getRangeInElement() = target.range.shiftLeft(elementStart)
		override fun resolveReference() = listOf(symbol)
	}
}

class CellaDeclarationProvider : PsiSymbolDeclarationProvider {
	override fun getDeclarations(element: PsiElement, offsetInElement: Int): Collection<PsiSymbolDeclaration> {
		val (file, document) = cellaNameAt(element) ?: return emptyList()
		val virtualFile = file.virtualFile ?: return emptyList()
		if (resolveName(file, document, element) != NameResolution.Declared)
			return emptyList()
		
		val symbol = CellaSymbol(virtualFile, element.textRange.startOffset, element.text)
		return listOf(object : PsiSymbolDeclaration {
			override fun getDeclaringElement() = element
			override fun getRangeInDeclaringElement() = TextRange(0, element.textLength)
			override fun getSymbol() = symbol
		})
	}
}

class CellaReferenceProvider : ImplicitReferenceProvider {
	override fun getImplicitReference(element: PsiElement, offsetInElement: Int): PsiSymbolReference? {
		if (isProjectString(element))
			return projectReference(element, offsetInElement)
		
		val (file, document) = cellaNameAt(element) ?: return null
		val resolution = resolveName(file, document, element) as? NameResolution.Referenced ?: return null
		val symbol = symbolAt(file.project, resolution.location) ?: return null
		return object : PsiSymbolReference {
			override fun getElement() = element
			override fun getRangeInElement() = TextRange(0, element.textLength)
			override fun resolveReference() = listOf(symbol)
		}
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
		
		return definitionAt(project, file, document, start)?.let { symbolAt(project, it) }
	}
}

class CellaUsageSearcher : UsageSearcher {
	override fun collectSearchRequest(parameters: UsageSearchParameters): Query<out Usage>? {
		val symbol = parameters.target as? CellaSymbol ?: return null
		return CellaUsageQuery(parameters.project, symbol, parameters.searchScope)
	}
}

private class CellaUsage(override val file: PsiFile, override val range: TextRange, override val declaration: Boolean) :
	PsiUsage {
	override fun createPointer(): Pointer<CellaUsage> = Pointer.hardPointer(this)
}

private class CellaUsageQuery(private val project: Project, private val symbol: CellaSymbol,
	private val scope: SearchScope) : AbstractQuery<Usage>() {
	override fun processResults(consumer: Processor<in Usage>): Boolean {
		val server = CellaServer.get(project)
		val position = runReadActionBlocking {
			FileDocumentManager.getInstance().getDocument(symbol.file)?.let { server.position(it, symbol.offset) }
		} ?: return true
		
		val locations = server.await(server.request {
			it.textDocumentService.references(ReferenceParams(TextDocumentIdentifier(CellaServer.uriOf(symbol.file)),
				position, ReferenceContext(true)))
		}, 10000) ?: return true
		
		return runReadActionBlocking {
			locations.all { location ->
				val start = locate(location.uri, location.range.start) ?: return@all true
				val end = locate(location.uri, location.range.end) ?: return@all true
				if (scope is LocalSearchScope && !scope.isInScope(start.file))
					return@all true
				
				val psi = PsiManager.getInstance(project).findFile(start.file) ?: return@all true
				val isDeclaration = start.file == symbol.file && start.offset == symbol.offset
				consumer.process(CellaUsage(psi, TextRange(start.offset, end.offset), isDeclaration))
			}
		}
	}
}
