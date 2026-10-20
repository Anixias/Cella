package com.anixias.cella.projects

import com.anixias.cella.CellaIcons
import com.anixias.cella.run.CellaProjects
import com.intellij.ide.SelectInContext
import com.intellij.ide.projectView.ProjectView
import com.intellij.ide.util.treeView.AbstractTreeNode
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.guessProjectDir
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VirtualFile
import com.jetbrains.rider.projectView.views.SolutionViewPaneBase
import com.jetbrains.rider.projectView.views.SolutionViewRootNodeBase
import com.jetbrains.rider.projectView.views.getChildrenForSolutionView
import com.jetbrains.rider.projectView.views.impl.SolutionViewSelectInTargetBase
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerViewSettings
import com.jetbrains.rider.projectView.views.solutionExplorer.nodes.SolutionExplorerFileNode
import com.jetbrains.rider.workspaceModel.WorkspaceUserModelUpdater

object CellaExplorerSettings : SolutionExplorerViewSettings {
	override fun isShowAllFiles(project: Project) = false
	override fun isShowProjectImports() = false
	override fun isFoldersAlwaysOnTop() = true
	override fun isDiagnosticMode() = false
}

class CellaExplorer(project: Project) : SolutionViewPaneBase(project, CellaExplorerRootNode(project)) {
	companion object {
		const val ID = "CellaExplorer"
		private const val Title = "Cella"
		private const val Weight = 1

		fun tryGetInstance(project: Project): CellaExplorer? =
			ProjectView.getInstance(project).getProjectViewPaneById(ID) as? CellaExplorer

		fun hasProjects(folder: VirtualFile?, depth: Int = 3): Boolean {
			if (folder == null || !folder.isDirectory)
				return false

			if (CellaProjects.projectFilesIn(folder).isNotEmpty())
				return true

			return depth > 0 && folder.children.any { it.isDirectory && !it.name.startsWith(".") && hasProjects(it, depth - 1) }
		}
	}

	override fun isInitiallyVisible() = hasProjects(project.guessProjectDir())

	override fun getTitle() = Title

	override fun getIcon() = CellaIcons.FILE

	override fun getId() = ID

	override fun getWeight() = Weight

	override fun createSelectInTarget() = object : SolutionViewSelectInTargetBase(project) {
		override fun canSelect(context: SelectInContext) = true

		override fun selectIn(context: SelectInContext?, requestFocus: Boolean) {
			context?.let { select(it, null, requestFocus) }
		}

		override fun toString() = Title

		override fun getMinorViewId() = ID

		override fun getWeight() = Weight.toFloat()
	}
}

class CellaExplorerRootNode(project: Project) : SolutionViewRootNodeBase(project) {
	override fun calculateChildren(): MutableList<AbstractTreeNode<*>> {
		val nodes = mutableListOf<AbstractTreeNode<*>>()
		val roots = listOfNotNull(myProject.guessProjectDir()) + WorkspaceUserModelUpdater.getInstance(myProject)
			.getAttachedFolders()
			.mapNotNull { LocalFileSystem.getInstance().findFileByIoFile(it) }

		for (root in roots.distinct()) {
			val projects = CellaProjects.projectFilesIn(root)
			if (projects.isNotEmpty()) {
				projects.mapTo(nodes) { CellaProjectNode(myProject, it, CellaExplorerSettings) }
				continue
			}

			val (folders, files) = root.getChildrenForSolutionView().partition { it.isDirectory }
			for (folder in folders.filter { !it.name.startsWith(".") }.sortedBy { it.name.lowercase() }) {
				val inner = CellaProjects.projectFilesIn(folder)
				if (inner.isEmpty())
					nodes += SolutionExplorerFileNode(myProject, folder, emptyList(), CellaExplorerSettings, false, false)
				else
					inner.mapTo(nodes) { CellaProjectNode(myProject, it, CellaExplorerSettings) }
			}

			for (file in files.sortedBy { it.name.lowercase() })
				nodes += SolutionExplorerFileNode(myProject, file, emptyList(), CellaExplorerSettings, false, false)
		}

		return nodes
	}
}
