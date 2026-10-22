package com.anixias.cella.server

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.openapi.vfs.WritingAccessProvider

class CellaLibraryWritingAccess(private val project: Project) : WritingAccessProvider() {
	override fun requestWriting(files: Collection<VirtualFile>): Collection<VirtualFile> = files.filter(::isLibrary)

	override fun isPotentiallyWritable(file: VirtualFile) = !isLibrary(file)

	override fun getReadOnlyMessage() = "Standard library files are read-only"

	private fun isLibrary(file: VirtualFile) = CellaServer.get(project).isLibraryFile(file)
}
