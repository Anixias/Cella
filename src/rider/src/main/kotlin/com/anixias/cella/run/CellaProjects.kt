package com.anixias.cella.run

import com.intellij.openapi.vfs.VirtualFile
import java.nio.file.Files
import java.nio.file.Path

object CellaProjects {
	private val outputType = Regex("""^\s*OutputType\s*=\s*["']([A-Za-z]+)["']""", RegexOption.MULTILINE)
	private val table = Regex("""^\s*\[""", RegexOption.MULTILINE)

	fun find(file: VirtualFile): Path? {
		var directory = if (file.isDirectory) file else file.parent
		if (file.extension == "celp")
			return file.toNioPath()

		while (directory != null) {
			directory.children.filter { it.extension == "celp" }.minByOrNull { it.name }?.let { return it.toNioPath() }
			directory = directory.parent
		}

		return null
	}

	fun projectFilesIn(directory: VirtualFile): List<VirtualFile> =
		directory.children.filter { !it.isDirectory && it.extension == "celp" }.sortedBy { it.name }

	fun name(project: Path) = project.fileName.toString().removeSuffix(".celp")

	fun isExecutable(project: Path): Boolean {
		val text = runCatching { Files.readString(project) }.getOrNull() ?: return false
		val topLevel = table.find(text)?.let { text.substring(0, it.range.first) } ?: text
		return outputType.find(topLevel)?.groupValues?.get(1) in setOf("Executable", "WindowedExecutable")
	}
}
