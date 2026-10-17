package com.anixias.cella.editor

import com.anixias.cella.CellaLanguage
import com.intellij.application.options.IndentOptionsEditor
import com.intellij.application.options.SmartIndentOptionsEditor
import com.intellij.psi.codeStyle.CommonCodeStyleSettings
import com.intellij.psi.codeStyle.LanguageCodeStyleSettingsProvider

class CellaCodeStyleSettingsProvider : LanguageCodeStyleSettingsProvider() {
	override fun getLanguage() = CellaLanguage
	
	override fun customizeDefaults(
		commonSettings: CommonCodeStyleSettings,
		indentOptions: CommonCodeStyleSettings.IndentOptions
	) {
		indentOptions.USE_TAB_CHARACTER = true
		indentOptions.TAB_SIZE = 4
		indentOptions.INDENT_SIZE = 4
		indentOptions.CONTINUATION_INDENT_SIZE = 4
	}
	
	override fun getIndentOptionsEditor(): IndentOptionsEditor = SmartIndentOptionsEditor()
	
	override fun getCodeSample(settingsType: SettingsType) = """
		main: fun -> i32
		{
			if true
			{
				ret 1
			}

			ret 0
		}
	""".trimIndent()
}
