package com.anixias.cella.run

import com.anixias.cella.server.CellaSemanticHighlighter
import com.intellij.execution.actions.ConfigurationContext
import com.intellij.execution.actions.LazyRunConfigurationProducer
import com.intellij.execution.configurations.runConfigurationType
import com.intellij.execution.lineMarker.RunLineMarkerContributor
import com.intellij.icons.AllIcons
import com.intellij.openapi.util.Ref
import com.intellij.psi.PsiDocumentManager
import com.intellij.psi.PsiElement
import java.nio.file.Path

private fun findRunnable(element: PsiElement): CellaSemanticHighlighter.RunnableFunction? {
	val file = element.containingFile ?: return null
	val document = PsiDocumentManager.getInstance(element.project).getDocument(file) ?: return null
	return CellaSemanticHighlighter.runnableAt(document, element.textRange)
}

private fun findProject(element: PsiElement): Path? =
	element.containingFile?.virtualFile?.let { CellaProjects.find(it) }

class CellaRunConfigurationProducer : LazyRunConfigurationProducer<CellaRunConfiguration>() {
	override fun getConfigurationFactory() = runConfigurationType<CellaConfigurationType>().configurationFactories[0]

	override fun setupConfigurationFromContext(configuration: CellaRunConfiguration, context: ConfigurationContext,
		sourceElement: Ref<PsiElement>): Boolean {
		val (celp, entry) = findTarget(context) ?: return false
		configuration.options.project = celp.toString()
		configuration.options.entry = entry.orEmpty()
		configuration.name = entry ?: CellaProjects.name(celp)
		return true
	}

	override fun isConfigurationFromContext(configuration: CellaRunConfiguration, context: ConfigurationContext) =
		findTarget(context) == (configuration.projectPath to configuration.entry)

	private fun findTarget(context: ConfigurationContext): Pair<Path, String?>? {
		val element = context.psiLocation
		val runnable = element?.let { findRunnable(it) }
		if (runnable != null && !runnable.isMain)
			return findProject(element)?.let { it to runnable.name }

		val file = context.location?.virtualFile ?: return null
		return CellaProjects.find(file)?.takeIf { CellaProjects.isExecutable(it) }?.let { it to null }
	}
}

class CellaRunLineMarker : RunLineMarkerContributor() {
	override fun getInfo(element: PsiElement): Info? {
		if (element.firstChild != null)
			return null

		val runnable = findRunnable(element) ?: return null
		if (runnable.isMain && findProject(element)?.let { CellaProjects.isExecutable(it) } != true)
			return null

		return withExecutorActions(AllIcons.RunConfigurations.TestState.Run)
	}
}
