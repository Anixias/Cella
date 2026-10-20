package com.anixias.cella.projects

import com.anixias.cella.server.CellaServer
import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.intellij.openapi.Disposable
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.VirtualFileManager
import com.intellij.openapi.vfs.newvfs.BulkFileListener
import com.intellij.openapi.vfs.newvfs.events.VFileEvent
import com.intellij.util.Alarm
import com.jetbrains.rider.projectView.isDirectorySolution
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerViewPane
import com.jetbrains.rider.workspaceModel.WorkspaceUserModelUpdater
import java.net.URI
import java.nio.file.Path

data class CellaReference(
	val owner: Path,
	val target: Path,
	val path: String,
	val alias: String?,
	val condition: String?,
	val index: Int,
	val exists: Boolean) {
	val name: String get() = target.fileName.toString().removeSuffix(".celp")
}

data class CellaProjectInfo(
	val path: Path,
	val name: String,
	val outputType: String?,
	val referenceable: Boolean,
	val references: List<CellaReference>) {
	val isExecutable: Boolean get() = outputType == "Executable" || outputType == "WindowedExecutable"
}

@Service(Service.Level.PROJECT)
class CellaProjectModel(private val project: Project) : Disposable {
	companion object {
		fun get(project: Project): CellaProjectModel = project.service()

		fun pathOf(uri: String): Path = Path.of(URI(uri)).normalize()

		fun parse(projects: JsonArray): List<CellaProjectInfo> = projects.mapNotNull { element ->
			val entry = element as? JsonObject ?: return@mapNotNull null
			val path = pathOf(entry.text("uri") ?: return@mapNotNull null)
			CellaProjectInfo(path, entry.text("name") ?: path.fileName.toString().removeSuffix(".celp"),
				entry.text("outputType"), entry.get("referenceable")?.asBoolean == true,
				entry.getAsJsonArray("references")?.mapNotNull { parseReference(path, it) }.orEmpty())
		}

		private fun parseReference(owner: Path, element: JsonElement): CellaReference? {
			val entry = element as? JsonObject ?: return null
			return CellaReference(owner, pathOf(entry.text("uri") ?: return null), entry.text("path") ?: return null,
				entry.text("alias"), entry.text("condition"), entry.get("index")?.asInt ?: return null,
				entry.get("exists")?.asBoolean == true)
		}

		fun JsonObject.text(name: String): String? = get(name)?.takeIf { it.isJsonPrimitive }?.asString
	}

	private val alarm = Alarm(Alarm.ThreadToUse.POOLED_THREAD, this)

	@Volatile
	private var loaded = false

	@Volatile
	var projects: List<CellaProjectInfo> = emptyList()
		private set

	init {
		project.messageBus.connect(this).subscribe(VirtualFileManager.VFS_CHANGES, object : BulkFileListener {
			override fun after(events: List<VFileEvent>) {
				if (events.any { it.path.endsWith(".celp", ignoreCase = true) })
					refresh()
			}
		})
	}

	fun find(celp: Path): CellaProjectInfo? {
		if (!loaded) {
			loaded = true
			refresh()
		}

		val normalized = celp.normalize()
		return projects.firstOrNull { it.path == normalized }
	}

	fun refresh() {
		alarm.cancelAllRequests()
		alarm.addRequest({
			val server = CellaServer.get(project)
			val (result, _) = server.await(server.requestProject { it.projects(JsonObject()) }, 30000) ?: return@addRequest
			projects = parse(result ?: return@addRequest)
			loaded = true
			ApplicationManager.getApplication().invokeLater({ update() }, project.disposed)
		}, 300)
	}

	private fun update() {
		attachOutsideReferences()
		SolutionExplorerViewPane.tryGetInstance(project)?.updateFromRoot(true)
	}

	private fun attachOutsideReferences() {
		val base = project.basePath?.let { Path.of(it).normalize() } ?: return
		if (!project.isDirectorySolution)
			return

		val updater = WorkspaceUserModelUpdater.getInstance(project)
		val folders = projects.flatMap { it.references }
			.filter { it.exists }
			.mapNotNull { it.target.parent }
			.filter { !it.startsWith(base) }
			.distinct()

		for (folder in folders) {
			val file = folder.toFile()
			if (updater.contains(file) != true && !updater.isAttachedFolder(file))
				updater.attachFolder(file)
		}
	}

	fun virtualFile(path: Path): VirtualFile? = LocalFileSystem.getInstance().findFileByNioFile(path)

	override fun dispose() {
	}
}
