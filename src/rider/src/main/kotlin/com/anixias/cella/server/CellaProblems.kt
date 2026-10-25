package com.anixias.cella.server

import com.anixias.cella.projects.CellaExplorer
import com.intellij.analysis.problemsView.FileProblem
import com.intellij.analysis.problemsView.Problem
import com.intellij.analysis.problemsView.ProblemsProvider
import com.intellij.analysis.problemsView.toolWindow.ProblemsView
import com.intellij.analysis.problemsView.toolWindow.ProblemsViewPanel
import com.intellij.analysis.problemsView.toolWindow.ProblemsViewState
import com.intellij.analysis.problemsView.toolWindow.ProblemsViewTab
import com.intellij.analysis.problemsView.toolWindow.Root
import com.intellij.icons.AllIcons
import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.actionSystem.ActionGroup
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.remoting.ActionRemoteBehaviorSpecification
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.project.DumbAwareToggleAction
import com.intellij.openapi.project.Project
import com.intellij.openapi.startup.ProjectActivity
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.problems.WolfTheProblemSolver
import com.intellij.util.concurrency.AppExecutorUtil
import java.net.URI
import java.nio.file.Path
import javax.swing.Icon
import org.eclipse.lsp4j.Diagnostic
import org.eclipse.lsp4j.DiagnosticSeverity
import org.eclipse.lsp4j.DiagnosticTag

enum class CellaProblemLevel(val label: String, val icon: Icon) {
	Error("Errors", AllIcons.General.Error),
	Warning("Warnings", AllIcons.General.Warning),
	Hint("Hints", AllIcons.General.Information);

	companion object {
		fun of(severity: DiagnosticSeverity) = when (severity) {
			DiagnosticSeverity.Error -> Error
			DiagnosticSeverity.Warning -> Warning
			else -> Hint
		}
	}
}

data class CellaProblem(
	private val owner: CellaProblems,
	override val file: VirtualFile,
	override val line: Int,
	override val column: Int,
	override val text: String,
	override val description: String?,
	val level: CellaProblemLevel) : FileProblem {
	override val provider: ProblemsProvider get() = owner

	override val icon: Icon get() = level.icon
}

class CellaProblemFilters : ActionGroup(), ActionRemoteBehaviorSpecification.Frontend {
	private class Filter(private val level: CellaProblemLevel) : DumbAwareToggleAction(level.label),
		ActionRemoteBehaviorSpecification.Frontend {
		override fun getActionUpdateThread() = ActionUpdateThread.EDT

		override fun isSelected(event: AnActionEvent) =
			event.project?.let { CellaProblems.get(it).isShown(level) } == true

		override fun setSelected(event: AnActionEvent, state: Boolean) {
			event.project?.let { CellaProblems.get(it).setShown(level, state) }
		}
	}

	private val filters = CellaProblemLevel.entries.map(::Filter).toTypedArray<AnAction>()

	override fun getActionUpdateThread() = ActionUpdateThread.EDT

	override fun getChildren(event: AnActionEvent?): Array<AnAction> {
		val project = event?.project ?: return emptyArray()
		return if (ProblemsView.getSelectedTab(project)?.getTabId() == CellaProblemsTab.TabId) filters else emptyArray()
	}
}

@Service(Service.Level.PROJECT)
class CellaProblems(override val project: Project) : ProblemsProvider {
	companion object {
		private const val HiddenKey = "cella.problems.hidden"

		fun get(project: Project): CellaProblems = project.service()
	}

	private val problems = linkedMapOf<VirtualFile, List<CellaProblem>>()
	private val roots = mutableListOf<CellaProblemsRoot>()
	private val solverQueue = AppExecutorUtil.createBoundedApplicationPoolExecutor("Cella problems", 1)
	private val hidden = PropertiesComponent.getInstance(project).getValue(HiddenKey).orEmpty()
		.split(',')
		.mapNotNull { name -> CellaProblemLevel.entries.firstOrNull { it.name == name } }
		.toMutableSet()

	val files: Collection<VirtualFile> get() = problems.filterValues { list -> list.any(::isShown) }.keys.toList()

	val count: Int get() = problems.values.sumOf { list -> list.count(::isShown) }

	fun of(file: VirtualFile): List<CellaProblem> = problems[file].orEmpty().filter(::isShown)

	fun isShown(level: CellaProblemLevel) = level !in hidden

	fun setShown(level: CellaProblemLevel, shown: Boolean) {
		if (shown) hidden -= level else hidden += level
		PropertiesComponent.getInstance(project).setValue(HiddenKey, hidden.joinToString(",") { it.name }, "")
		roots.toList().forEach { it.structureChanged() }
	}

	private fun isShown(problem: CellaProblem) = isShown(problem.level)

	fun publish(uri: String, diagnostics: List<Diagnostic>) {
		ApplicationManager.getApplication().invokeLater({
			val file = runCatching { LocalFileSystem.getInstance().findFileByNioFile(Path.of(URI(uri))) }.getOrNull()
				?: return@invokeLater

			val updated = diagnostics
				.filter { it.tags?.contains(DiagnosticTag.Unnecessary) != true }
				.map { diagnostic ->
					val message = diagnostic.message.orEmpty()
					CellaProblem(this, file, diagnostic.range.start.line, diagnostic.range.start.character,
						message.lineSequence().first(), message.takeIf { it.contains('\n') },
						CellaProblemLevel.of(diagnostic.severity ?: DiagnosticSeverity.Error))
				}

			val previous = problems[file].orEmpty()
			if (updated.isEmpty())
				problems.remove(file)
			else
				problems[file] = updated
			
			val hasErrors = updated.any { it.level == CellaProblemLevel.Error }
			if (hasErrors != previous.any { it.level == CellaProblemLevel.Error })
				solverQueue.execute {
					if (project.isDisposed)
						return@execute
					
					val solver = WolfTheProblemSolver.getInstance(project)
					if (hasErrors)
						solver.reportProblemsFromExternalSource(file, this)
					else
						solver.clearProblemsFromExternalSource(file, this)
				}

			for (root in roots.toList()) {
				previous.filter { it !in updated && isShown(it) }.forEach(root::problemDisappeared)
				updated.filter { it !in previous && isShown(it) }.forEach(root::problemAppeared)
			}
		}, project.disposed)
	}

	fun clear() {
		ApplicationManager.getApplication().invokeLater({
			for (file in problems.keys.toList())
				publish(CellaServer.uriOf(file), emptyList())
		}, project.disposed)
	}
	
	fun listen(root: CellaProblemsRoot) {
		roots += root
	}

	fun forget(root: CellaProblemsRoot) {
		roots -= root
	}
}

class CellaProblemsRoot(panel: ProblemsViewPanel, private val store: CellaProblems) : Root(panel) {
	init {
		store.listen(this)
	}

	override fun getProblemCount() = store.count

	override fun getProblemFiles() = store.files

	override fun getFileProblemCount(file: VirtualFile) = store.of(file).size

	override fun getFileProblems(file: VirtualFile): Collection<Problem> = store.of(file)

	override fun getOtherProblemCount() = 0

	override fun getOtherProblems(): Collection<Problem> = emptyList()

	override fun dispose() {
		store.forget(this)
		super.dispose()
	}
}

class CellaProblemsTab : ProjectActivity {
	companion object {
		const val TabId = "CellaProblems"
		private const val ToolWindowId = "Problems View"
		private const val SolutionTabId = "SWEA"
	}

	override suspend fun execute(project: Project) {
		if (!CellaExplorer.hasProjects(project))
			return

		ToolWindowManager.getInstance(project).invokeLater {
			if (!project.isDisposed)
				addTab(project)
		}
	}

	private fun addTab(project: Project) {
		val window = ToolWindowManager.getInstance(project).getToolWindow(ToolWindowId) ?: return
		val manager = window.contentManager
		if (manager.contents.any { (it.component as? ProblemsViewTab)?.getTabId() == TabId })
			return

		val panel = ProblemsViewPanel(project, TabId, ProblemsViewState.getInstance(project)) { "All Cella Files" }
		panel.treeModel.root = CellaProblemsRoot(panel, CellaProblems.get(project))
		val content = manager.factory.createContent(panel, panel.getName(0), false).apply {
			isCloseable = false
			setDisposer(panel)
		}

		val solutionTab = manager.contents.indexOfFirst { (it.component as? ProblemsViewTab)?.getTabId() == SolutionTabId }
		manager.addContent(content, if (solutionTab < 0) manager.contentCount else solutionTab + 1)
	}
}
