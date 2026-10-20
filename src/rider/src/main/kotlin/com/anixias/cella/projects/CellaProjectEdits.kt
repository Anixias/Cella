package com.anixias.cella.projects

import com.anixias.cella.projects.CellaProjectModel.Companion.text
import com.anixias.cella.run.CellaBuild
import com.anixias.cella.server.CellaServer
import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonPrimitive
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.util.ExecUtil
import com.intellij.openapi.command.WriteCommandAction
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.util.ThrowableComputable
import com.intellij.openapi.vfs.VirtualFile
import java.nio.file.Path

data class SettingSchema(
	val name: String,
	val label: String,
	val kind: String,
	val conditional: Boolean,
	val default: String?,
	val choices: List<String>)

class ProjectSettingsData(
	val name: String,
	val errors: List<String>,
	val settings: List<SettingSchema>,
	val values: Map<String?, Map<String, Any>>,
	val flags: Map<String, Boolean>,
	val references: List<CellaReference>)

class LoadedProject(
	val celp: VirtualFile,
	val settings: ProjectSettingsData,
	val projects: List<CellaProjectInfo>,
	val versions: List<String>)

data class ReferenceChoice(val target: Path, val alias: String?)

object CellaProjectEdits {
	private const val Title = "Cella"

	fun load(project: Project, celp: VirtualFile, withVersions: Boolean): LoadedProject? {
		val document = FileDocumentManager.getInstance().getDocument(celp)
		val params = JsonObject().apply {
			add("textDocument", JsonObject().apply { addProperty("uri", CellaServer.uriOf(celp)) })
			document?.let { addProperty("text", it.text) }
		}

		val server = CellaServer.get(project)
		val loaded = runWithProgress(project, "Reading project settings") {
			val settings = server.await(server.requestProject { it.projectSettings(params) }, 15000)
			val projects = server.await(server.requestProject { it.projects(JsonObject()) }, 30000)
			Triple(settings, projects, if (withVersions) installedVersions() else emptyList())
		}

		val (settings, projects, versions) = loaded ?: return null
		val result = settings?.first
		if (result == null) {
			Messages.showErrorDialog(project, settings?.second ?: "Cella's language server isn't available.", Title)
			return null
		}

		return LoadedProject(celp, parseSettings(celp.toNioPath(), result),
			projects?.first?.let(CellaProjectModel::parse).orEmpty(), versions)
	}

	fun apply(project: Project, celp: VirtualFile, edits: JsonArray): Boolean {
		if (edits.isEmpty)
			return true

		val document = FileDocumentManager.getInstance().getDocument(celp) ?: return false
		val params = JsonObject().apply {
			add("textDocument", JsonObject().apply { addProperty("uri", CellaServer.uriOf(celp)) })
			addProperty("text", document.text)
			add("edits", edits)
		}

		val server = CellaServer.get(project)
		val response = runWithProgress(project, "Saving project settings") {
			server.await(server.requestProject { it.editProject(params) }, 15000)
		}

		val result = response?.first
		if (result == null) {
			Messages.showErrorDialog(project, response?.second ?: "Cella's language server isn't available.", Title)
			return false
		}

		val changes = result.getAsJsonArray("edits")?.mapNotNull { it as? JsonObject }.orEmpty()
		WriteCommandAction.runWriteCommandAction(project, "Change Project Settings", null, {
			for (change in changes.reversed()) {
				val range = change.getAsJsonObject("range") ?: continue
				val start = offsetOf(document, range.getAsJsonObject("start"))
				val end = offsetOf(document, range.getAsJsonObject("end"))
				document.replaceString(start, end, change.text("newText") ?: "")
			}
		})

		FileDocumentManager.getInstance().saveDocument(document)
		CellaProjectModel.get(project).refresh()
		return true
	}

	fun referenceEdits(
		original: List<CellaReference>,
		chosen: List<ReferenceChoice>,
		condition: String?): List<JsonObject> {
		val existing = original.filter { it.condition == condition }
		val chosenTargets = chosen.associateBy { it.target }
		val edits = mutableListOf<JsonObject>()
		for (reference in existing) {
			val choice = chosenTargets[reference.target] ?: continue
			if (choice.alias.orEmpty() != reference.alias.orEmpty())
				edits += edit(condition) {
					addProperty("reference", reference.index)
					addProperty("alias", choice.alias)
				}
		}

		for (reference in existing.filter { it.target !in chosenTargets }.sortedByDescending { it.index })
			edits += edit(condition) { addProperty("removeReference", reference.index) }

		val existingTargets = existing.map { it.target }.toSet()
		for (choice in chosen.filter { it.target !in existingTargets })
			edits += edit(condition) {
				addProperty("addReference", choice.target.toUri().toString())
				choice.alias?.let { addProperty("alias", it) }
			}

		return edits
	}

	fun edit(condition: String?, build: JsonObject.() -> Unit): JsonObject = JsonObject().apply {
		condition?.let { addProperty("condition", it) }
		build()
	}

	fun toJson(value: Any?): JsonElement? = when (value) {
		is Boolean -> JsonPrimitive(value)
		is String -> JsonPrimitive(value)
		is List<*> -> JsonArray().apply { value.forEach { add(it.toString()) } }
		else -> null
	}

	private fun offsetOf(document: com.intellij.openapi.editor.Document, position: JsonObject?): Int {
		val line = position?.get("line")?.asInt ?: 0
		if (line >= document.lineCount)
			return document.textLength

		return (document.getLineStartOffset(line) + (position?.get("character")?.asInt ?: 0))
			.coerceAtMost(document.textLength)
	}

	private fun installedVersions(): List<String> {
		val cella = CellaBuild.findCella() ?: return emptyList()
		return runCatching {
			val output = ExecUtil.execAndGetOutput(GeneralCommandLine(cella, "list", "--installed"), 10000)
			if (output.exitCode == 0) output.stdoutLines.map { it.trim() }.filter { it.isNotEmpty() } else emptyList()
		}.getOrDefault(emptyList())
	}

	private fun <T> runWithProgress(project: Project, title: String, compute: () -> T): T? =
		ProgressManager.getInstance().runProcessWithProgressSynchronously(
			ThrowableComputable<T, RuntimeException> { compute() }, title, true, project)

	private fun parseSettings(path: Path, result: JsonObject): ProjectSettingsData {
		val settings = result.getAsJsonArray("settings")?.mapNotNull { element ->
			val entry = element as? JsonObject ?: return@mapNotNull null
			SettingSchema(entry.text("name") ?: return@mapNotNull null, entry.text("label") ?: "",
				entry.text("kind") ?: "", entry.get("conditional")?.asBoolean == true, entry.text("default"),
				entry.getAsJsonArray("choices")?.mapNotNull { it.takeIf { it.isJsonPrimitive }?.asString }.orEmpty())
		}.orEmpty()

		val values = linkedMapOf<String?, Map<String, Any>>(null to parseValues(result.getAsJsonObject("values")))
		result.getAsJsonArray("conditions")?.forEach { element ->
			val entry = element as? JsonObject ?: return@forEach
			values[entry.text("condition") ?: return@forEach] = parseValues(entry.getAsJsonObject("values"))
		}

		val flags = result.getAsJsonObject("flags")?.entrySet()
			?.filter { it.value.isJsonPrimitive }
			?.associate { it.key to it.value.asBoolean }
			.orEmpty()

		val references = CellaProjectModel.parse(JsonArray().apply {
			add(JsonObject().apply {
				addProperty("uri", path.toUri().toString())
				add("references", result.getAsJsonArray("references") ?: JsonArray())
			})
		}).firstOrNull()?.references.orEmpty()

		return ProjectSettingsData(result.text("name") ?: "",
			result.getAsJsonArray("errors")?.mapNotNull { it.takeIf { it.isJsonPrimitive }?.asString }.orEmpty(),
			settings, values, flags, references)
	}

	private fun parseValues(values: JsonObject?): Map<String, Any> = values?.entrySet()?.mapNotNull { (key, value) ->
		when {
			value.isJsonArray -> key to value.asJsonArray.mapNotNull { it.takeIf { it.isJsonPrimitive }?.asString }
			value.isJsonPrimitive && value.asJsonPrimitive.isBoolean -> key to value.asBoolean
			value.isJsonPrimitive -> key to value.asString
			else -> null
		}
	}?.toMap().orEmpty()
}
