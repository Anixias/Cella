package com.anixias.cella.server

import com.anixias.cella.CellaFileType
import com.anixias.cella.CelpFileType
import com.intellij.codeInsight.daemon.DaemonCodeAnalyzer
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.runReadActionBlocking
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.psi.PsiManager
import com.intellij.util.concurrency.AppExecutorUtil
import org.eclipse.lsp4j.ClientCapabilities
import org.eclipse.lsp4j.CompletionCapabilities
import org.eclipse.lsp4j.CompletionItemCapabilities
import org.eclipse.lsp4j.Diagnostic
import org.eclipse.lsp4j.DidChangeTextDocumentParams
import org.eclipse.lsp4j.DidCloseTextDocumentParams
import org.eclipse.lsp4j.DidOpenTextDocumentParams
import org.eclipse.lsp4j.InitializeParams
import org.eclipse.lsp4j.InitializedParams
import org.eclipse.lsp4j.MessageActionItem
import org.eclipse.lsp4j.MessageParams
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.PublishDiagnosticsParams
import org.eclipse.lsp4j.SemanticTokensLegend
import org.eclipse.lsp4j.SemanticTokensWorkspaceCapabilities
import org.eclipse.lsp4j.ShowMessageRequestParams
import org.eclipse.lsp4j.TextDocumentClientCapabilities
import org.eclipse.lsp4j.TextDocumentContentChangeEvent
import org.eclipse.lsp4j.TextDocumentIdentifier
import org.eclipse.lsp4j.TextDocumentItem
import org.eclipse.lsp4j.VersionedTextDocumentIdentifier
import org.eclipse.lsp4j.WorkspaceClientCapabilities
import org.eclipse.lsp4j.WorkspaceFolder
import org.eclipse.lsp4j.launch.LSPLauncher
import org.eclipse.lsp4j.services.LanguageClient
import org.eclipse.lsp4j.services.LanguageServer
import java.io.File
import java.net.URI
import java.nio.file.Path
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException

@Service(Service.Level.PROJECT)
class CellaServer(private val project: Project) : Disposable {
	companion object {
		private val log = logger<CellaServer>()
		
		fun get(project: Project): CellaServer = project.service()
		
		fun isCella(file: VirtualFile?) = file != null && (file.fileType == CellaFileType || file.fileType == CelpFileType)
		
		fun uriOf(file: VirtualFile): String = Path.of(file.path).toUri().toString()
		
		fun keyOf(uri: String): String = try {
			val path = Path.of(URI(uri)).toString()
			if (File.separatorChar == '\\') path.lowercase() else path
		} catch (_: Exception) {
			uri
		}
	}
	
	private class Connection(val process: Process, val server: LanguageServer, val legend: SemanticTokensLegend?)
	
	private val sender = AppExecutorUtil.createBoundedApplicationPoolExecutor("Cella language server", 1)
	private val documents = ConcurrentHashMap<String, Pair<Document, Int>>()
	private val diagnostics = ConcurrentHashMap<String, List<Diagnostic>>()
	private var connection: Connection? = null
	private var hasReportedMissing = false
	
	val highlighter = CellaSemanticHighlighter(project, this)
	
	val legend: SemanticTokensLegend?
		get() = connection?.legend
	
	fun <T> request(call: (LanguageServer) -> CompletableFuture<T>): CompletableFuture<T?> {
		val result = CompletableFuture<T?>()
		sender.execute {
			val server = connect()?.server
			if (server == null) {
				result.complete(null)
				return@execute
			}
			
			try {
				call(server).whenComplete { value, error ->
					if (error != null)
						result.complete(null)
					else
						result.complete(value)
				}
			} catch (exception: Exception) {
				log.warn(exception)
				result.complete(null)
			}
		}
		
		return result
	}
	
	fun <T> await(future: CompletableFuture<T?>, milliseconds: Long = 3000): T? {
		val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(milliseconds)
		while (true) {
			ProgressManager.checkCanceled()
			try {
				return future.get(10, TimeUnit.MILLISECONDS)
			} catch (_: TimeoutException) {
				if (System.nanoTime() >= deadline)
					return null
			} catch (_: Exception) {
				return null
			}
		}
	}
	
	fun open(document: Document, file: VirtualFile) {
		val uri = uriOf(file)
		if (documents.putIfAbsent(uri, document to 1) != null)
			return
		
		val text = document.text
		val language = if (file.fileType == CellaFileType) "cella" else "cella-project"
		sender.execute {
			connect()?.server?.textDocumentService?.didOpen(DidOpenTextDocumentParams(TextDocumentItem(uri, language, 1, text)))
		}
		
		highlighter.request(document, uri, 1)
	}
	
	fun change(document: Document) {
		val file = FileDocumentManager.getInstance().getFile(document) ?: return
		val uri = uriOf(file)
		val version = (documents[uri]?.second ?: return) + 1
		documents[uri] = document to version
		val text = document.text
		sender.execute {
			connect()?.server?.textDocumentService?.didChange(
				DidChangeTextDocumentParams(VersionedTextDocumentIdentifier(uri, version),
					listOf(TextDocumentContentChangeEvent(text))))
		}
		
		highlighter.request(document, uri, version)
	}
	
	fun close(file: VirtualFile) {
		val uri = uriOf(file)
		if (documents.remove(uri) == null)
			return
		
		sender.execute {
			connection?.server?.textDocumentService?.didClose(DidCloseTextDocumentParams(TextDocumentIdentifier(uri)))
		}
	}
	
	fun versionOf(uri: String) = documents[uri]?.second
	
	fun diagnosticsFor(file: VirtualFile): List<Diagnostic> = diagnostics[keyOf(uriOf(file))].orEmpty()
	
	fun position(document: Document, offset: Int): Position {
		val line = document.getLineNumber(offset)
		return Position(line, offset - document.getLineStartOffset(line))
	}
	
	fun restart() {
		sender.execute {
			disconnect()
			hasReportedMissing = false
			for (entry in documents.values)
				ApplicationManager.getApplication().invokeLater({ reopen(entry.first) }, project.disposed)
		}
	}
	
	private fun reopen(document: Document) {
		val file = FileDocumentManager.getInstance().getFile(document) ?: return
		documents.remove(uriOf(file))
		open(document, file)
	}
	
	private fun connect(): Connection? {
		connection?.let {
			if (it.process.isAlive)
				return it
			
			connection = null
		}
		
		val executable = findExecutable() ?: return null
		return try {
			val process = ProcessBuilder(executable).redirectError(ProcessBuilder.Redirect.DISCARD).start()
			val launcher = LSPLauncher.createClientLauncher(Client(), process.inputStream, process.outputStream)
			launcher.startListening()
			val server = launcher.remoteProxy
			val root = project.basePath?.let { Path.of(it).toUri().toString() }
			val result = server.initialize(InitializeParams().apply {
				processId = ProcessHandle.current().pid().toInt()
				workspaceFolders = root?.let { listOf(WorkspaceFolder(it, project.name)) }
				capabilities = ClientCapabilities().apply {
					workspace = WorkspaceClientCapabilities().apply {
						semanticTokens = SemanticTokensWorkspaceCapabilities(true)
					}
					textDocument = TextDocumentClientCapabilities().apply {
						completion = CompletionCapabilities(CompletionItemCapabilities(true))
					}
				}
			}).get(30, TimeUnit.SECONDS)
			server.initialized(InitializedParams())
			Connection(process, server, result.capabilities.semanticTokensProvider?.legend).also { connection = it }
		} catch (exception: Exception) {
			log.warn("Couldn't start $executable", exception)
			null
		}
	}
	
	private fun disconnect() {
		val current = connection ?: return
		connection = null
		try {
			current.server.shutdown().get(2, TimeUnit.SECONDS)
			current.server.exit()
		} catch (_: Exception) {
		}
		
		current.process.destroy()
	}
	
	private fun findExecutable(): String? {
		val configured = CellaSettings.instance.state.serverPath
		val path = configured?.takeIf { File(it).isFile }
			?: PathEnvironmentVariableUtil.findInPath("cella-lsp")?.path
			?: PathEnvironmentVariableUtil.findInPath("cella-lsp.exe")?.path
		
		if (path == null && !hasReportedMissing) {
			hasReportedMissing = true
			NotificationGroupManager.getInstance().getNotificationGroup("Cella")
				.createNotification("Cella language server not found",
					"Set its location in Settings | Languages & Frameworks | Cella.", NotificationType.WARNING)
				.notify(project)
		}
		
		return path
	}
	
	private fun refreshAnnotations(uri: String) {
		val key = keyOf(uri)
		ApplicationManager.getApplication().invokeLater({
			val entry = documents.entries.firstOrNull { keyOf(it.key) == key }?.value ?: return@invokeLater
			val file = FileDocumentManager.getInstance().getFile(entry.first) ?: return@invokeLater
			val psi = runReadActionBlocking { PsiManager.getInstance(project).findFile(file) }
			if (psi != null)
				DaemonCodeAnalyzer.getInstance(project).restart(psi, "Cella diagnostics")
		}, project.disposed)
	}
	
	private inner class Client : LanguageClient {
		override fun telemetryEvent(event: Any?) {
		}
		
		override fun publishDiagnostics(params: PublishDiagnosticsParams) {
			diagnostics[keyOf(params.uri)] = params.diagnostics
			refreshAnnotations(params.uri)
		}
		
		override fun showMessage(params: MessageParams) {
		}
		
		override fun showMessageRequest(params: ShowMessageRequestParams): CompletableFuture<MessageActionItem?> =
			CompletableFuture.completedFuture(null)
		
		override fun logMessage(params: MessageParams) {
			log.info(params.message)
		}
		
		override fun refreshSemanticTokens(): CompletableFuture<Void?> {
			for ((uri, entry) in documents)
				highlighter.request(entry.first, uri, entry.second)
			
			return CompletableFuture.completedFuture(null)
		}
	}
	
	override fun dispose() {
		sender.execute { disconnect() }
		sender.shutdown()
	}
}
