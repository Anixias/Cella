package com.anixias.cella.run

import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.intellij.execution.actions.ConfigurationContext
import com.intellij.execution.actions.LazyRunConfigurationProducer
import com.intellij.execution.configurations.runConfigurationType
import com.intellij.execution.lineMarker.RunLineMarkerContributor
import com.intellij.icons.AllIcons
import com.intellij.openapi.util.Ref
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiWhiteSpace
import com.intellij.psi.util.PsiTreeUtil
import java.nio.file.Path

class CellaRunConfigurationProducer : LazyRunConfigurationProducer<CellaRunConfiguration>() {
	override fun getConfigurationFactory() = runConfigurationType<CellaConfigurationType>().configurationFactories[0]

	override fun setupConfigurationFromContext(configuration: CellaRunConfiguration, context: ConfigurationContext,
		sourceElement: Ref<PsiElement>): Boolean {
		val celp = findExecutable(context) ?: return false
		configuration.options.project = celp.toString()
		configuration.name = CellaProjects.name(celp)
		return true
	}

	override fun isConfigurationFromContext(configuration: CellaRunConfiguration, context: ConfigurationContext) =
		findExecutable(context)?.let { it == configuration.projectPath } == true

	private fun findExecutable(context: ConfigurationContext): Path? {
		val file = context.location?.virtualFile ?: return null
		return CellaProjects.find(file)?.takeIf { CellaProjects.isExecutable(it) }
	}
}

class CellaRunLineMarker : RunLineMarkerContributor() {
	override fun getInfo(element: PsiElement): Info? {
		if (element.text != "main" || tokenOf(element) != CellaToken.IDENTIFIER || !isTopLevelDeclaration(element))
			return null

		val file = element.containingFile?.virtualFile ?: return null
		CellaProjects.find(file)?.takeIf { CellaProjects.isExecutable(it) } ?: return null
		return withExecutorActions(AllIcons.RunConfigurations.TestState.Run)
	}

	private fun isTopLevelDeclaration(element: PsiElement): Boolean {
		val next = generateSequence(PsiTreeUtil.nextLeaf(element)) { PsiTreeUtil.nextLeaf(it) }
			.firstOrNull { it !is PsiWhiteSpace }
		if (next == null || tokenOf(next) != CellaToken.COLON)
			return false

		var depth = 0
		for (leaf in generateSequence(PsiTreeUtil.prevLeaf(element)) { PsiTreeUtil.prevLeaf(it) }) {
			when (tokenOf(leaf)) {
				CellaToken.RIGHT_BRACE -> depth++
				CellaToken.LEFT_BRACE -> if (--depth < 0) return false
				else -> {}
			}
		}

		return true
	}

	private fun tokenOf(element: PsiElement) = CellaTokenTypes.tokenOf(element.node?.elementType)
}
