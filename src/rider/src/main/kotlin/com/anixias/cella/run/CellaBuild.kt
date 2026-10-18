package com.anixias.cella.run

import com.anixias.cella.server.CellaSettings
import com.intellij.build.BuildViewManager
import com.intellij.build.DefaultBuildDescriptor
import com.intellij.build.FilePosition
import com.intellij.build.events.MessageEvent
import com.intellij.build.events.impl.FailureResultImpl
import com.intellij.build.events.impl.FileMessageEventImpl
import com.intellij.build.events.impl.FinishBuildEventImpl
import com.intellij.build.events.impl.OutputBuildEventImpl
import com.intellij.build.events.impl.StartBuildEventImpl
import com.intellij.build.events.impl.SuccessResultImpl
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.execution.filters.Filter
import com.intellij.execution.filters.OpenFileHyperlinkInfo
import com.intellij.execution.process.OSProcessHandler
import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessListener
import com.intellij.execution.process.ProcessOutputTypes
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.components.service
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.openapi.vfs.LocalFileSystem
import java.io.File
import java.nio.charset.StandardCharsets
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.ConcurrentHashMap

class CellaBuildResult(val succeeded: Boolean, val outputPath: Path?)

object CellaBuild {
	private val location = Regex("""^(Error|Warning|Hint|Info) at (.+):(\d+):(\d+)(?:: (.*))?$""")
	private val caret = Regex("""^\s*(?:\^|╘═*╛)\s(.*)$""")
	private val outputs = ConcurrentHashMap<Pair<Path, String?>, Path>()

	fun findCella(): String? = CellaSettings.instance.state.cellaPath?.takeIf { File(it).isFile }
		?: PathEnvironmentVariableUtil.findInPath("cella")?.path

	fun lastOutput(celp: Path, entry: String?): Path? = outputs[celp to entry]?.takeIf { Files.isRegularFile(it) }

	fun build(project: Project, celp: Path, release: Boolean, entry: String? = null): CellaBuildResult {
		val cella = findCella()
		if (cella == null) {
			NotificationGroupManager.getInstance().getNotificationGroup("Cella")
				.createNotification("Cella not found", "Install Cella, or set it in Settings | Languages & Frameworks | Cella.",
					NotificationType.WARNING)
				.notify(project)
			return CellaBuildResult(false, null)
		}

		val directory = celp.parent
		val name = CellaProjects.name(celp)
		val arguments = listOfNotNull(cella, "build", celp.toString(), "--release".takeIf { release }) +
			(entry?.let { listOf("--entry", it) } ?: emptyList())
		val commandLine = GeneralCommandLine(arguments)
			.withWorkDirectory(directory.toFile())
			.withCharset(StandardCharsets.UTF_8)
			.withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)

		val handler = OSProcessHandler(commandLine)
		val buildId = Any()
		val view = project.service<BuildViewManager>()
		val descriptor = DefaultBuildDescriptor(buildId, "Build $name", directory.toString(), System.currentTimeMillis())
			.withExecutionFilter(LocationFilter(project, directory))

		view.onEvent(buildId, StartBuildEventImpl(descriptor, "Building $name"))
		val text = StringBuilder()
		handler.addProcessListener(object : ProcessListener {
			override fun onTextAvailable(event: ProcessEvent, outputType: Key<*>) {
				if (outputType == ProcessOutputTypes.SYSTEM)
					return

				synchronized(text) { text.append(event.text) }
				view.onEvent(buildId, OutputBuildEventImpl(buildId, event.text, outputType == ProcessOutputTypes.STDOUT))
			}
		})

		handler.startNotify()
		handler.waitFor()
		val exitCode = handler.exitCode ?: -1
		val lines = synchronized(text) { text.toString() }.lines()
		reportMessages(view, buildId, directory, lines)

		val outputPath = lines.firstNotNullOfOrNull { line ->
			line.trimEnd().takeIf { it.startsWith("$name -> ") }?.let { directory.resolve(it.substring(name.length + 4)) }
		}

		val succeeded = exitCode == 0
		if (succeeded && outputPath != null)
			outputs[celp to entry] = outputPath.normalize()

		view.onEvent(buildId, FinishBuildEventImpl(buildId, null, System.currentTimeMillis(),
			if (succeeded) "Built $name" else "Build failed",
			if (succeeded) SuccessResultImpl() else FailureResultImpl()))

		return CellaBuildResult(succeeded, outputPath?.normalize())
	}

	private fun reportMessages(view: BuildViewManager, buildId: Any, directory: Path, lines: List<String>) {
		for ((index, line) in lines.withIndex()) {
			val match = location.matchEntire(line.trimEnd()) ?: continue
			val (severity, path, lineNumber, column) = match.destructured
			val message = match.groups[5]?.value ?: lines.drop(index + 1).takeWhile { it.isNotBlank() }
				.firstNotNullOfOrNull { caret.matchEntire(it.trimEnd())?.groupValues?.get(1) } ?: continue

			val details = lines.drop(index).takeWhile { it.isNotBlank() }.joinToString("\n")
			val kind = when (severity) {
				"Error" -> MessageEvent.Kind.ERROR
				"Warning" -> MessageEvent.Kind.WARNING
				else -> MessageEvent.Kind.INFO
			}

			val file = directory.resolve(path).normalize().toFile()
			view.onEvent(buildId, FileMessageEventImpl(buildId, kind, "Cella", message, details,
				FilePosition(file, lineNumber.toInt() - 1, column.toInt() - 1)))
		}
	}

	private class LocationFilter(private val project: Project, private val directory: Path) : Filter {
		private val pattern = Regex("""(?:Error|Warning|Hint|Info) at (.+?):(\d+):(\d+)""")

		override fun applyFilter(line: String, entireLength: Int): Filter.Result? {
			val match = pattern.find(line) ?: return null
			val target = match.groups[1]!!
			val file = LocalFileSystem.getInstance().findFileByNioFile(directory.resolve(target.value).normalize())
				?: return null

			val start = entireLength - line.length + target.range.first
			val end = entireLength - line.length + match.range.last + 1
			return Filter.Result(start, end,
				OpenFileHyperlinkInfo(project, file, match.groupValues[2].toInt() - 1, match.groupValues[3].toInt() - 1))
		}
	}
}

class CellaBuildAction : AnAction() {
	override fun getActionUpdateThread() = ActionUpdateThread.BGT

	override fun update(event: AnActionEvent) {
		val celp = event.getData(CommonDataKeys.VIRTUAL_FILE)?.let { CellaProjects.find(it) }
		event.presentation.isEnabledAndVisible = event.project != null && celp != null
		celp?.let { event.presentation.text = "Build '${CellaProjects.name(it)}'" }
	}

	override fun actionPerformed(event: AnActionEvent) {
		val project = event.project ?: return
		val celp = event.getData(CommonDataKeys.VIRTUAL_FILE)?.let { CellaProjects.find(it) } ?: return
		FileDocumentManager.getInstance().saveAllDocuments()
		object : Task.Backgroundable(project, "Building ${CellaProjects.name(celp)}", true) {
			override fun run(indicator: ProgressIndicator) {
				CellaBuild.build(project, celp, false)
			}
		}.queue()
	}
}
