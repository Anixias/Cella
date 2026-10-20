package com.anixias.cella.projects

import com.anixias.cella.CellaIcons
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.projectImport.ProjectOpenProcessor
import com.jetbrains.rider.projectView.SolutionManager

class CellaProjectOpenProcessor : ProjectOpenProcessor() {
	override val name = "Cella"

	override val icon = CellaIcons.FILE

	override fun canOpenProject(file: VirtualFile) = !file.isDirectory && file.extension == "celp"

	override fun isProjectFile(file: VirtualFile) = canOpenProject(file)

	override fun lookForProjectsInDirectory() = false

	override suspend fun openProjectAsync(
		virtualFile: VirtualFile,
		projectToClose: Project?,
		forceOpenInNewFrame: Boolean): Project? {
		val folder = virtualFile.parent ?: return null
		return SolutionManager.openDirectoryInteractive(projectToClose, forceOpenInNewFrame, false, folder)
	}
}
