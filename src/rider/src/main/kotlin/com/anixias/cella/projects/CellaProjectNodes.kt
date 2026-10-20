package com.anixias.cella.projects

import com.anixias.cella.CellaIcons
import com.anixias.cella.run.CellaProjects
import com.intellij.icons.AllIcons
import com.intellij.ide.projectView.PresentationData
import com.intellij.ide.projectView.ProjectView
import com.intellij.ide.util.treeView.AbstractTreeNode
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VfsUtil
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.SimpleTextAttributes
import com.jetbrains.rider.projectView.views.SolutionViewNode
import com.jetbrains.rider.projectView.views.getChildrenForSolutionView
import com.jetbrains.rider.projectView.views.navigateToSolutionView
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerCustomization
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerViewSettings
import com.jetbrains.rider.projectView.views.solutionExplorer.nodes.SolutionExplorerFileNode
import com.jetbrains.rider.projectView.workspace.ProjectModelEntity
import java.nio.file.Path

class CellaSolutionExplorerCustomization(project: Project) : SolutionExplorerCustomization(project) {
	override fun modifyChildren(
		entity: ProjectModelEntity,
		settings: SolutionExplorerViewSettings,
		children: MutableList<AbstractTreeNode<*>>) =
		wrapProjects(children.mapNotNull { fileOf(it)?.parent }.distinct().singleOrNull(), settings, children)

	override fun modifyChildren(
		file: VirtualFile,
		settings: SolutionExplorerViewSettings,
		children: MutableList<AbstractTreeNode<*>>) = wrapProjects(file.takeIf { it.isDirectory }, settings, children)

	private fun wrapProjects(
		folder: VirtualFile?,
		settings: SolutionExplorerViewSettings,
		children: MutableList<AbstractTreeNode<*>>) {
		val projects = folder?.let(CellaProjects::projectFilesIn).orEmpty()
		if (projects.isEmpty()) {
			replaceProjectFolders(settings, children)
			return
		}

		val files = children.filter { fileOf(it)?.parent == folder }
		val index = files.firstOrNull()?.let(children::indexOf) ?: children.size
		children.removeAll(files.toSet())
		children.addAll(index.coerceAtMost(children.size), projects.map { CellaProjectNode(project, it, settings) })
	}

	private fun replaceProjectFolders(settings: SolutionExplorerViewSettings, children: MutableList<AbstractTreeNode<*>>) {
		val iterator = children.listIterator()
		while (iterator.hasNext()) {
			val folder = fileOf(iterator.next())?.takeIf { it.isDirectory } ?: continue
			val projects = CellaProjects.projectFilesIn(folder)
			if (projects.isEmpty())
				continue

			iterator.remove()
			for (celp in projects)
				iterator.add(CellaProjectNode(project, celp, settings))
		}
	}

	private fun fileOf(node: AbstractTreeNode<*>): VirtualFile? = when (node) {
		is CellaProjectNode, is CellaReferencesNode, is CellaReferenceNode -> null
		is SolutionViewNode<*> -> node.virtualFile?.takeIf { it.isValid }
		else -> null
	}
}

class CellaProjectNode(project: Project, celp: VirtualFile, private val settings: SolutionExplorerViewSettings) :
	SolutionViewNode<VirtualFile>(project, celp) {
	companion object {
		private val hiddenFolders = setOf("bin", "obj", ".idea")
	}

	val celp: VirtualFile get() = value

	val path: Path get() = celp.toNioPath()

	override fun calculateChildren(): MutableList<AbstractTreeNode<*>> {
		val folder = celp.parent ?: return mutableListOf()
		val nodes = mutableListOf<AbstractTreeNode<*>>()
		val info = CellaProjectModel.get(myProject).find(path)
		if (info != null && info.references.isNotEmpty())
			nodes += CellaReferencesNode(myProject, info)

		val showAll = settings.isShowAllFiles(myProject)
		val (folders, files) = folder.getChildrenForSolutionView().partition { it.isDirectory }
		for (child in folders.sortedBy { it.name.lowercase() }) {
			if (!showAll && child.name in hiddenFolders)
				continue

			val projects = CellaProjects.projectFilesIn(child)
			if (projects.isEmpty())
				nodes += SolutionExplorerFileNode(myProject, child, emptyList(), settings, false, false)
			else
				projects.mapTo(nodes) { CellaProjectNode(myProject, it, settings) }
		}

		for (child in files.sortedBy { it.name.lowercase() }) {
			if (child.extension != "celp")
				nodes += SolutionExplorerFileNode(myProject, child, emptyList(), settings, false, false)
		}

		return nodes
	}

	override fun update(presentation: PresentationData) {
		presentation.setIcon(CellaIcons.FILE)
		presentation.addText(celp.nameWithoutExtension, SimpleTextAttributes.REGULAR_ATTRIBUTES)
	}

	override fun getVirtualFile(): VirtualFile? = celp.parent

	override fun contains(file: VirtualFile): Boolean = celp.parent?.let { VfsUtil.isAncestor(it, file, false) } == true

	override fun canNavigate() = true

	override fun canNavigateToSource() = true

	override fun navigate(requestFocus: Boolean) = OpenFileDescriptor(myProject, celp).navigate(requestFocus)
}

data class CellaReferencesKey(val owner: Path)

class CellaReferencesNode(project: Project, val info: CellaProjectInfo) :
	SolutionViewNode<CellaReferencesKey>(project, CellaReferencesKey(info.path)) {
	override fun calculateChildren(): MutableList<AbstractTreeNode<*>> =
		info.references.mapTo(mutableListOf()) { CellaReferenceNode(myProject, it) }

	override fun update(presentation: PresentationData) {
		presentation.setIcon(AllIcons.Nodes.PpLibFolder)
		presentation.addText("References", SimpleTextAttributes.REGULAR_ATTRIBUTES)
	}

	override fun contains(file: VirtualFile) = false
}

class CellaReferenceNode(project: Project, val reference: CellaReference) :
	SolutionViewNode<CellaReference>(project, reference) {
	override fun calculateChildren(): MutableList<AbstractTreeNode<*>> = mutableListOf()

	override fun isAlwaysLeaf() = true

	override fun update(presentation: PresentationData) {
		presentation.setIcon(CellaIcons.FILE)
		presentation.addText(reference.name,
			if (reference.exists) SimpleTextAttributes.REGULAR_ATTRIBUTES else SimpleTextAttributes.ERROR_ATTRIBUTES)

		reference.condition?.let { presentation.addText("  $it", SimpleTextAttributes.GRAYED_ATTRIBUTES) }
	}

	override fun contains(file: VirtualFile) = false

	override fun canNavigate() = reference.exists

	override fun canNavigateToSource() = false

	override fun navigate(requestFocus: Boolean) {
		val model = CellaProjectModel.get(myProject)
		val explorer = CellaExplorer.tryGetInstance(myProject)
		if (explorer != null && ProjectView.getInstance(myProject).currentViewId == CellaExplorer.ID) {
			explorer.select(null, model.virtualFile(reference.target) ?: return, requestFocus)
			return
		}

		myProject.navigateToSolutionView(model.virtualFile(reference.target.parent) ?: return, requestFocus)
	}
}
