package com.anixias.cella.projects

import com.google.gson.JsonArray
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.ComboBox
import com.intellij.openapi.ui.DialogWrapper
import com.intellij.openapi.ui.Messages
import com.intellij.ui.SimpleListCellRenderer
import com.intellij.ui.ToolbarDecorator
import com.intellij.ui.components.JBLabel
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.components.JBTabbedPane
import com.intellij.ui.components.JBTextArea
import com.intellij.ui.components.JBTextField
import com.intellij.ui.table.JBTable
import com.intellij.util.ui.FormBuilder
import com.intellij.util.ui.JBUI
import java.awt.BorderLayout
import java.awt.Component
import java.awt.FlowLayout
import javax.swing.DefaultCellEditor
import javax.swing.DefaultComboBoxModel
import javax.swing.JButton
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.JTable
import javax.swing.table.DefaultTableModel

class CellaPropertiesDialog(private val project: Project, private val loaded: LoadedProject) : DialogWrapper(project) {
	private sealed class Scope(val text: String) {
		object All : Scope("All builds")
		class Condition(val condition: String) : Scope(condition)
		object Add : Scope("Add condition…")
	}

	private interface Editor {
		val component: JComponent
		fun show(value: Any?, fallback: String?, enabled: Boolean)
		fun read(): Any?
	}

	private class Choice(val value: Any?, val text: String)

	private class ChoiceEditor(private val choices: List<Choice>) : Editor {
		private val box = ComboBox<Choice>().apply { renderer = SimpleListCellRenderer.create("") { it.text } }
		override val component: JComponent = box

		override fun show(value: Any?, fallback: String?, enabled: Boolean) {
			val items = listOfNotNull(fallback?.let { Choice(null, it) }) + choices
			box.model = DefaultComboBoxModel(items.toTypedArray())
			box.selectedItem = items.firstOrNull { it.value == value } ?: items.firstOrNull { it.value == null }
			box.isEnabled = enabled
		}

		override fun read(): Any? = (box.selectedItem as? Choice)?.value
	}

	private class TextEditor : Editor {
		private val field = JBTextField(30)
		override val component: JComponent = field

		override fun show(value: Any?, fallback: String?, enabled: Boolean) {
			field.text = value as? String ?: ""
			field.emptyText.text = fallback.orEmpty()
			field.isEnabled = enabled
		}

		override fun read(): Any? = field.text.trim().ifEmpty { null }
	}

	private class VersionEditor(versions: List<String>) : Editor {
		private val box = ComboBox(versions.toTypedArray()).apply { isEditable = true }
		override val component: JComponent = box

		override fun show(value: Any?, fallback: String?, enabled: Boolean) {
			box.selectedItem = value as? String ?: ""
			(box.editor.editorComponent as? JBTextField)?.emptyText?.text = fallback.orEmpty()
			box.isEnabled = enabled
		}

		override fun read(): Any? = box.editor.item?.toString()?.trim()?.ifEmpty { null }
	}

	private class ListEditor : Editor {
		private val area = JBTextArea(4, 30)
		override val component: JComponent = JBScrollPane(area)

		override fun show(value: Any?, fallback: String?, enabled: Boolean) {
			area.text = (value as? List<*>)?.joinToString("\n").orEmpty()
			area.emptyText.text = fallback.orEmpty()
			area.isEnabled = enabled
		}

		override fun read(): Any? = area.text.lines().map { it.trim() }.filter { it.isNotEmpty() }.ifEmpty { null }
	}

	private val settings = loaded.settings
	private val schema = settings.settings.filter { it.kind in setOf("choice", "boolean", "version", "textList", "text") }
	private val grouped = setOf("OutputType", "AssemblyName", "CellaVersion", "StandardLibrary", "Referenceable",
		"BoundsChecks", "OverflowChecks", "DebugInfo", "SystemLinkPreference", "Imports", "Prelude")
	private val values = linkedMapOf<String?, MutableMap<String, Any>>()
	private val references = linkedMapOf<String?, List<ReferenceChoice>>()
	private val removedConditions = mutableSetOf<String>()
	private val editors = linkedMapOf<String, Editor>()
	private val scopes = ComboBox<Scope>().apply { renderer = SimpleListCellRenderer.create("") { it.text } }
	private val removeCondition = JButton("Remove")
	private val referencesEditor = CellaReferencesEditor(project, loaded.celp.toNioPath().normalize(), loaded.projects)
	private val flagsModel = object : DefaultTableModel(arrayOf<Any>("Flag", "On"), 0) {
		override fun getColumnClass(column: Int) = if (column == 1) java.lang.Boolean::class.java else String::class.java

		override fun isCellEditable(row: Int, column: Int) = current == null
	}

	private val flagsTable = JBTable(flagsModel)
	private val levelChoices = settings.settings.firstOrNull { it.kind == "lints" }?.choices.orEmpty().toTypedArray()
	private val lintLevels = linkedMapOf<String?, MutableMap<String, String>>()
	private val levelBox = ComboBox<String>()
	private val lintsModel = object : DefaultTableModel(arrayOf<Any>("Lint", "Level"), 0) {
		override fun isCellEditable(row: Int, column: Int) = column == 1
	}
	
	private val lintsTable = JBTable(lintsModel)
	private var current: String? = null
	private var isSwitching = false

	init {
		title = "${settings.name} Properties"
		for ((condition, scopeValues) in settings.values)
			values[condition] = scopeValues.toMutableMap()

		for (condition in values.keys)
			references[condition] = settings.references.filter { it.condition == condition }
				.map { ReferenceChoice(it.target, it.alias) }

		for ((name, value) in settings.flags)
			flagsModel.addRow(arrayOf<Any>(name, value))
		
		for ((condition, levels) in settings.lintLevels)
			lintLevels[condition] = levels.toMutableMap()
		
		for (lint in settings.lints)
			lintsModel.addRow(arrayOf<Any>(lint.label, ""))
		
		lintsTable.columnModel.getColumn(1).cellEditor = object : DefaultCellEditor(levelBox) {
			override fun getTableCellEditorComponent(table: JTable, value: Any?, isSelected: Boolean, row: Int,
				column: Int): Component {
				levelBox.model = DefaultComboBoxModel(arrayOf(inheritedLevel(settings.lints[row])) + levelChoices)
				return super.getTableCellEditorComponent(table, value, isSelected, row, column)
			}
		}

		for (setting in schema)
			editors[setting.name] = when (setting.kind) {
				"choice" -> ChoiceEditor(setting.choices.map { Choice(it, it) })
				"boolean" -> ChoiceEditor(listOf(Choice(true, "On"), Choice(false, "Off")))
				"version" -> VersionEditor(loaded.versions)
				"textList" -> ListEditor()
				else -> TextEditor()
			}

		rebuildScopes(Scope.All)
		scopes.addActionListener { switchScope() }
		removeCondition.addActionListener { removeCurrentCondition() }
		init()
		showScope()
	}

	override fun createCenterPanel(): JComponent {
		val header = JPanel(FlowLayout(FlowLayout.LEFT, JBUI.scale(8), 0)).apply {
			add(JBLabel("Applies to:"))
			add(scopes)
			add(removeCondition)
		}

		val tabs = JBTabbedPane()
		tabs.addTab("Application", page(listOf("OutputType", "AssemblyName", "CellaVersion", "StandardLibrary",
			"Referenceable") + schema.map { it.name }.filter { it !in grouped }))

		tabs.addTab("Build", page(listOf("BoundsChecks", "OverflowChecks", "DebugInfo", "SystemLinkPreference")))
		tabs.addTab("Flags", ToolbarDecorator.createDecorator(flagsTable)
			.setAddAction { flagsModel.addRow(arrayOf<Any>("", true)) }
			.setRemoveAction { flagsTable.selectedRows.sortedDescending().forEach(flagsModel::removeRow) }
			.disableUpDownActions()
			.createPanel())

		tabs.addTab("Lints", JBScrollPane(lintsTable))
		tabs.addTab("Imports", page(listOf("Imports", "Prelude")))
		tabs.addTab("References", referencesEditor.component)

		return JPanel(BorderLayout(0, JBUI.scale(8))).apply {
			add(header, BorderLayout.NORTH)
			add(tabs, BorderLayout.CENTER)
			preferredSize = JBUI.size(640, 420)
		}
	}

	private fun page(names: List<String>): JComponent {
		val builder = FormBuilder.createFormBuilder()
		for (name in names) {
			val setting = schema.firstOrNull { it.name == name } ?: continue
			builder.addLabeledComponent("${setting.label}:", editors.getValue(name).component)
		}

		return JPanel(BorderLayout()).apply {
			border = JBUI.Borders.empty(8)
			add(builder.panel, BorderLayout.NORTH)
		}
	}

	private fun rebuildScopes(selected: Scope) {
		isSwitching = true
		val items = listOf(Scope.All) +
			values.keys.filterNotNull().filter { it !in removedConditions }.map { Scope.Condition(it) } +
			Scope.Add

		scopes.model = DefaultComboBoxModel(items.toTypedArray())
		scopes.selectedItem = items.firstOrNull {
			it == selected || it is Scope.Condition && selected is Scope.Condition && it.condition == selected.condition
		} ?: Scope.All

		isSwitching = false
	}

	private fun switchScope() {
		if (isSwitching)
			return

		when (val scope = scopes.selectedItem) {
			is Scope.Add -> addCondition()
			is Scope.Condition -> moveTo(scope.condition)
			else -> moveTo(null)
		}
	}

	private fun moveTo(condition: String?) {
		if (condition == current)
			return

		save()
		current = condition
		showScope()
	}

	private fun addCondition() {
		val condition = Messages.showInputDialog(project, "Condition:", "Add Condition", null, "", null)?.trim()
		if (condition.isNullOrEmpty() || condition in values.keys && condition !in removedConditions) {
			rebuildScopes(current?.let { Scope.Condition(it) } ?: Scope.All)
			return
		}

		save()
		removedConditions.remove(condition)
		values.getOrPut(condition) { mutableMapOf() }
		lintLevels.getOrPut(condition) { mutableMapOf() }
		references.putIfAbsent(condition, emptyList())
		current = condition
		rebuildScopes(Scope.Condition(condition))
		showScope()
	}

	private fun removeCurrentCondition() {
		val condition = current ?: return
		removedConditions += condition
		current = null
		rebuildScopes(Scope.All)
		showScope()
	}

	private fun save() {
		val scopeValues = values.getOrPut(current) { mutableMapOf() }
		for (setting in schema) {
			if (current != null && !setting.conditional)
				continue

			when (val value = editors.getValue(setting.name).read()) {
				null -> scopeValues.remove(setting.name)
				else -> scopeValues[setting.name] = value
			}
		}

		references[current] = referencesEditor.read()
		lintsTable.cellEditor?.stopCellEditing()
		val levels = lintLevels.getOrPut(current) { mutableMapOf() }
		for ((row, lint) in settings.lints.withIndex()) {
			val level = lintsModel.getValueAt(row, 1) as? String
			if (level in levelChoices)
				levels[lint.name] = level!!
			else
				levels.remove(lint.name)
		}
	}
	
	private fun inheritedLevel(lint: LintSchema) = if (current == null) "Default (${lint.default})" else
		"Inherited (${lintLevels[null]?.get(lint.name) ?: lint.default})"
	
	private fun showLints() {
		for ((row, lint) in settings.lints.withIndex())
			lintsModel.setValueAt(lintLevels[current]?.get(lint.name) ?: inheritedLevel(lint), row, 1)
	}

	private fun showScope() {
		val condition = current
		val topLevel = values[null].orEmpty()
		for (setting in schema) {
			val editor = editors.getValue(setting.name)
			if (condition == null) {
				editor.show(topLevel[setting.name], setting.default?.let { "Default ($it)" }, true)
			} else {
				val inherited = describe(topLevel[setting.name]) ?: setting.default
				val value = if (setting.conditional) values[condition]?.get(setting.name) else topLevel[setting.name]
				editor.show(value, inherited?.let { "Inherited ($it)" } ?: "Inherited", setting.conditional)
			}
		}

		removeCondition.isEnabled = condition != null
		flagsTable.isEnabled = condition == null
		showLints()
		referencesEditor.show(references[condition].orEmpty(), true)
	}

	private fun describe(value: Any?): String? = when (value) {
		true -> "On"
		false -> "Off"
		is List<*> -> value.joinToString(", ")
		null -> null
		else -> value.toString()
	}

	override fun doOKAction() {
		flagsTable.cellEditor?.stopCellEditing()
		save()
		if (CellaProjectEdits.apply(project, loaded.celp, edits()))
			super.doOKAction()
	}

	private fun edits(): JsonArray {
		val edits = JsonArray()
		for ((condition, scopeValues) in values) {
			if (condition != null && condition in removedConditions)
				continue

			val before = settings.values[condition].orEmpty()
			for (setting in schema) {
				if (condition != null && !setting.conditional)
					continue

				val after = scopeValues[setting.name]
				if (after != before[setting.name])
					edits.add(CellaProjectEdits.edit(condition) {
						addProperty("setting", setting.name)
						add("value", CellaProjectEdits.toJson(after))
					})
			}
		}

		val flags = (0 until flagsModel.rowCount)
			.mapNotNull { row -> (flagsModel.getValueAt(row, 0) as? String)?.trim()?.takeIf { it.isNotEmpty() }
				?.let { it to (flagsModel.getValueAt(row, 1) == true) } }
			.toMap()

		for (name in settings.flags.keys + flags.keys) {
			if (flags[name] != settings.flags[name])
				edits.add(CellaProjectEdits.edit(null) {
					addProperty("flag", name)
					flags[name]?.let { addProperty("value", it) }
				})
		}

		for ((condition, chosen) in references) {
			if (condition == null || condition !in removedConditions)
				CellaProjectEdits.referenceEdits(settings.references, chosen, condition).forEach(edits::add)
		}

		for ((condition, levels) in lintLevels) {
			if (condition != null && condition in removedConditions)
				continue
			
			val before = settings.lintLevels[condition].orEmpty()
			for (lint in settings.lints) {
				val after = levels[lint.name]
				if (after != before[lint.name])
					edits.add(CellaProjectEdits.edit(condition) {
						addProperty("lint", lint.name)
						after?.let { addProperty("value", it) }
					})
			}
		}
		
		for (condition in removedConditions.filter { it in settings.values.keys })
			edits.add(CellaProjectEdits.edit(null) { addProperty("removeCondition", condition) })

		return edits
	}
}
