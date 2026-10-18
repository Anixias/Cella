package com.anixias.cella.server

import com.intellij.openapi.components.BaseState
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.SimplePersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.service
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.BoundConfigurable
import com.intellij.openapi.project.ProjectManager
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel

@Service(Service.Level.APP)
@State(name = "CellaSettings", storages = [Storage("cella.xml")])
class CellaSettings : SimplePersistentStateComponent<CellaSettings.Options>(Options()) {
	class Options : BaseState() {
		var serverPath by string()
		var cellaPath by string()
	}
	
	companion object {
		val instance: CellaSettings
			get() = service()
	}
}

class CellaConfigurable : BoundConfigurable("Cella") {
	override fun createPanel() = panel {
		val options = CellaSettings.instance.state
		row("Cella:") {
			textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileNoJarsDescriptor())
				.bindText({ options.cellaPath.orEmpty() }, { options.cellaPath = it.ifBlank { null } })
				.align(AlignX.FILL)
		}
		row("Language server:") {
			textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileNoJarsDescriptor())
				.bindText({ options.serverPath.orEmpty() }, { options.serverPath = it.ifBlank { null } })
				.align(AlignX.FILL)
		}
	}
	
	override fun apply() {
		super.apply()
		for (project in ProjectManager.getInstance().openProjects)
			CellaServer.get(project).restart()
	}
}
