package com.anixias.cella

import com.intellij.lang.Language
import com.intellij.openapi.fileTypes.LanguageFileType
import com.intellij.openapi.util.IconLoader
import javax.swing.Icon

object CellaLanguage : Language("Cella")

object CelpLanguage : Language("CellaProject")

object CellaIcons {
	val FILE: Icon = IconLoader.getIcon("/icons/cella.svg", CellaIcons::class.java)
}

object CellaFileType : LanguageFileType(CellaLanguage) {
	override fun getName() = "Cella"
	override fun getDescription() = "Cella source"
	override fun getDefaultExtension() = "ce"
	override fun getIcon() = CellaIcons.FILE
}

object CelpFileType : LanguageFileType(CelpLanguage) {
	override fun getName() = "Cella Project"
	override fun getDescription() = "Cella project"
	override fun getDefaultExtension() = "celp"
	override fun getIcon() = CellaIcons.FILE
}
