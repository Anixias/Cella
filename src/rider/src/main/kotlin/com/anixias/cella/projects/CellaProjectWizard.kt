package com.anixias.cella.projects

import com.anixias.cella.CellaIcons
import com.anixias.cella.run.CellaBuild
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.util.ExecUtil
import com.intellij.ide.util.projectWizard.WizardContext
import com.intellij.ide.wizard.AbstractNewProjectWizardStep
import com.intellij.ide.wizard.GeneratorNewProjectWizard
import com.intellij.ide.wizard.NewProjectWizardBaseData.Companion.baseData
import com.intellij.ide.wizard.NewProjectWizardChainStep.Companion.nextStep
import com.intellij.ide.wizard.NewProjectWizardStep
import com.intellij.ide.wizard.RootNewProjectWizardStep
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.ComboBox
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.dsl.builder.Panel
import com.jetbrains.rider.projectView.projectTemplates.wizardTemplates.adapter.RiderProjectWizardMode
import com.jetbrains.rider.projectView.projectTemplates.wizardTemplates.adapter.WizardMode
import com.jetbrains.rider.projectView.projectTemplates.wizardTemplates.common.riderNewProjectWizardBaseStep
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import java.nio.charset.StandardCharsets

class CellaProjectWizard : GeneratorNewProjectWizard, RiderProjectWizardMode {
	override val id = "Cella"
	override val name = "Cella"
	override val icon = CellaIcons.FILE
	override val wizardMode = WizardMode.NEW_SOLUTION_ONLY

	override fun createStep(context: WizardContext): NewProjectWizardStep = RootNewProjectWizardStep(context)
		.nextStep(::riderNewProjectWizardBaseStep)
		.nextStep(::CellaTemplateStep)
}

class CellaTemplateStep(parent: NewProjectWizardStep) : AbstractNewProjectWizardStep(parent) {
	private val kind = ComboBox(arrayOf("Executable", "Library"))
	private val gitignore = JBCheckBox("Add .gitignore")

	override fun setupUI(builder: Panel) {
		builder.row("Type:") { cell(kind) }
		builder.row { cell(gitignore) }
	}

	override fun setupProject(project: Project) {
		val data = baseData ?: return
		val folder = Path.of(data.contentEntryPath)
		val cella = CellaBuild.findCella()
		if (cella == null) {
			report(project, "Install Cella, or set it in Settings | Languages & Frameworks | Cella.")
			return
		}

		val arguments = listOfNotNull(cella, "new", "--lib".takeIf { kind.selectedItem == "Library" },
			"--gitignore".takeIf { gitignore.isSelected })

		val output = runCatching {
			Files.createDirectories(folder)
			ExecUtil.execAndGetOutput(GeneralCommandLine(arguments)
				.withWorkDirectory(folder.toFile())
				.withCharset(StandardCharsets.UTF_8), 30000)
		}.getOrElse {
			report(project, it.message ?: "Couldn't run '$cella'.")
			return
		}

		if (output.exitCode != 0)
			report(project, output.stdout.ifBlank { output.stderr }.trim())

		LocalFileSystem.getInstance().refreshIoFiles(listOf(File(folder.toString())), true, true, null)
	}

	private fun report(project: Project, message: String) {
		NotificationGroupManager.getInstance().getNotificationGroup("Cella")
			.createNotification("Couldn't create the project", message, NotificationType.ERROR)
			.notify(project)
	}
}
