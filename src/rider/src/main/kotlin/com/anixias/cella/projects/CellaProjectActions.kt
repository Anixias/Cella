package com.anixias.cella.projects

import com.anixias.cella.CellaIcons
import com.anixias.cella.run.CellaProjects
import com.google.gson.JsonArray
import com.intellij.ide.util.treeView.AbstractTreeNode
import com.intellij.openapi.actionSystem.ActionPlaces
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.CommonDataKeys
import com.intellij.openapi.actionSystem.PlatformCoreDataKeys
import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.fileEditor.FileEditorManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.DialogWrapper
import com.intellij.openapi.ui.InputValidator
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.openapi.vfs.VfsUtil
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.util.ui.JBUI
import com.jetbrains.rider.projectView.views.SolutionViewNode
import com.jetbrains.rider.projectView.views.solutionExplorer.SolutionExplorerViewPane
import javax.swing.JComponent
import javax.swing.SwingUtilities

object CellaSelection {
	fun nodes(event: AnActionEvent): List<AbstractTreeNode<*>> {
		val project = event.project ?: return emptyList()
		val component = event.getData(PlatformCoreDataKeys.CONTEXT_COMPONENT) ?: return emptyList()
		val pane = listOfNotNull(CellaExplorer.tryGetInstance(project), SolutionExplorerViewPane.tryGetInstance(project))
			.firstOrNull { pane -> pane.tree?.let { component === it || SwingUtilities.isDescendingFrom(component, it) } == true }
			?: return emptyList()

		return pane.getSelectedNodes()
	}

	fun project(event: AnActionEvent): VirtualFile? {
		when (val node = nodes(event).singleOrNull()) {
			is CellaProjectNode -> return node.celp
			is CellaReferencesNode -> return CellaProjectModel.get(node.project ?: return null).virtualFile(node.info.path)
		}

		if (!ActionPlaces.isPopupPlace(event.place))
			return null

		return event.getData(CommonDataKeys.VIRTUAL_FILE)?.takeIf { it.extension == "celp" }
	}
}

abstract class CellaProjectAction : AnAction() {
	override fun getActionUpdateThread() = ActionUpdateThread.EDT
}

class CellaPropertiesAction : CellaProjectAction() {
	override fun update(event: AnActionEvent) {
		event.presentation.isEnabledAndVisible = event.project != null && CellaSelection.project(event) != null
	}

	override fun actionPerformed(event: AnActionEvent) {
		val project = event.project ?: return
		val celp = CellaSelection.project(event) ?: return
		val loaded = CellaProjectEdits.load(project, celp, true) ?: return
		CellaPropertiesDialog(project, loaded).show()
	}
}

class CellaAddReferenceAction : CellaProjectAction() {
	override fun update(event: AnActionEvent) {
		event.presentation.isEnabledAndVisible = event.project != null && CellaSelection.project(event) != null
	}

	override fun actionPerformed(event: AnActionEvent) {
		val project = event.project ?: return
		val celp = CellaSelection.project(event) ?: return
		val loaded = CellaProjectEdits.load(project, celp, false) ?: return
		CellaAddReferenceDialog(project, loaded).show()
	}
}

class CellaRemoveReferenceAction : CellaProjectAction() {
	override fun update(event: AnActionEvent) {
		val nodes = CellaSelection.nodes(event)
		event.presentation.isEnabledAndVisible = nodes.isNotEmpty() && nodes.all { it is CellaReferenceNode }
	}

	override fun actionPerformed(event: AnActionEvent) {
		val project = event.project ?: return
		val model = CellaProjectModel.get(project)
		val references = CellaSelection.nodes(event).filterIsInstance<CellaReferenceNode>().map { it.reference }
		for ((owner, owned) in references.groupBy { it.owner }) {
			val celp = model.virtualFile(owner) ?: continue
			val edits = JsonArray()
			for (reference in owned.sortedByDescending { it.index })
				edits.add(CellaProjectEdits.edit(reference.condition) { addProperty("removeReference", reference.index) })

			if (!CellaProjectEdits.apply(project, celp, edits))
				return
		}
	}
}

class CellaNewFileAction : CellaProjectAction() {
	override fun update(event: AnActionEvent) {
		event.presentation.isEnabledAndVisible = event.project != null && folderOf(event) != null
		event.presentation.icon = CellaIcons.FILE
	}

	override fun actionPerformed(event: AnActionEvent) {
		val project = event.project ?: return
		val folder = folderOf(event) ?: return
		val name = Messages.showInputDialog(project, "Name:", "New Cella File", null, "", object : InputValidator {
			override fun checkInput(inputString: String) = inputString.isNotBlank() &&
				folder.findChild("${inputString.trim().removeSuffix(".ce")}.ce") == null

			override fun canClose(inputString: String) = checkInput(inputString)
		})?.trim()?.removeSuffix(".ce") ?: return

		val text = moduleOf(folder)?.let { "mod $it\n\n" } ?: ""
		val file = WriteCommandAction.writeCommandAction(project).withName("New Cella File").compute<VirtualFile, Exception> {
			folder.createChildData(this, "$name.ce").also { VfsUtil.saveText(it, text) }
		}

		FileEditorManager.getInstance(project).openFile(file, true)
	}

	private fun folderOf(event: AnActionEvent): VirtualFile? {
		val selected = CellaSelection.nodes(event).singleOrNull()
		val file = when (selected) {
			is CellaProjectNode -> selected.celp.parent
			null -> event.getData(CommonDataKeys.VIRTUAL_FILE)
			else -> (selected as? SolutionViewNode<*>)?.virtualFile
		} ?: return null

		val folder = if (file.isDirectory) file else file.parent ?: return null
		return folder.takeIf { CellaProjects.find(it) != null }
	}

	private fun moduleOf(folder: VirtualFile): String? {
		val project = CellaProjects.find(folder) ?: return null
		val projectFolder = project.parent?.let { LocalFileSystem.getInstance().findFileByNioFile(it) }
		for (candidate in listOfNotNull(folder, projectFolder)) {
			val module = candidate.children.filter { it.extension == "ce" }.sortedBy { it.name }
				.firstNotNullOfOrNull { file -> VfsUtil.loadText(file).lineSequence()
					.map { it.trim() }
					.firstOrNull { it.startsWith("mod ") }
					?.removePrefix("mod ")
					?.trim() }

			if (module != null)
				return module
		}

		return CellaProjects.name(project)
	}
}

class CellaAddReferenceDialog(private val project: Project, private val loaded: LoadedProject) : DialogWrapper(project) {
	private val editor = CellaReferencesEditor(project, loaded.celp.toNioPath().normalize(), loaded.projects)

	init {
		title = "Add Reference"
		editor.show(loaded.settings.references.filter { it.condition == null }.map { ReferenceChoice(it.target, it.alias) },
			true)

		init()
	}

	override fun createCenterPanel(): JComponent = editor.component.apply {
		preferredSize = JBUI.size(560, 320)
	}

	override fun doOKAction() {
		val edits = JsonArray()
		CellaProjectEdits.referenceEdits(loaded.settings.references, editor.read(), null).forEach(edits::add)
		if (CellaProjectEdits.apply(project, loaded.celp, edits))
			super.doOKAction()
	}
}
