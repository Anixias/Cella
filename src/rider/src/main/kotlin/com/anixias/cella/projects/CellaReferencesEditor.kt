package com.anixias.cella.projects

import com.intellij.openapi.fileChooser.FileChooser
import com.intellij.openapi.fileChooser.FileChooserDescriptor
import com.intellij.openapi.project.Project
import com.intellij.ui.ColoredTableCellRenderer
import com.intellij.ui.SimpleTextAttributes
import com.intellij.ui.ToolbarDecorator
import com.intellij.ui.table.JBTable
import java.nio.file.Path
import javax.swing.JComponent
import javax.swing.JTable
import javax.swing.table.AbstractTableModel

class CellaReferencesEditor(
	private val project: Project,
	private val owner: Path,
	private val projects: List<CellaProjectInfo>) {
	private class Row(
		val target: Path,
		val name: String,
		val location: String,
		val isSelectable: Boolean,
		var isIncluded: Boolean = false,
		var alias: String = "")

	private val rows = mutableListOf<Row>()
	private var isEditable = true

	private val model = object : AbstractTableModel() {
		override fun getRowCount() = rows.size

		override fun getColumnCount() = 4

		override fun getColumnName(column: Int) = when (column) {
			1 -> "Project"
			2 -> "Location"
			3 -> "Alias"
			else -> ""
		}

		override fun getColumnClass(column: Int) = if (column == 0) java.lang.Boolean::class.java else String::class.java

		override fun getValueAt(row: Int, column: Int): Any = when (column) {
			0 -> rows[row].isIncluded
			1 -> rows[row].name
			2 -> rows[row].location
			else -> rows[row].alias
		}

		override fun isCellEditable(row: Int, column: Int) = isEditable && when (column) {
			0 -> rows[row].isSelectable || rows[row].isIncluded
			3 -> rows[row].isIncluded
			else -> false
		}

		override fun setValueAt(value: Any?, row: Int, column: Int) {
			when (column) {
				0 -> rows[row].isIncluded = value == true
				3 -> rows[row].alias = value?.toString()?.trim().orEmpty()
			}

			fireTableRowsUpdated(row, row)
		}
	}

	private val table = JBTable(model).apply {
		setShowGrid(false)
		columnModel.getColumn(0).apply {
			maxWidth = 40
			minWidth = 40
		}

		setDefaultRenderer(String::class.java, object : ColoredTableCellRenderer() {
			override fun customizeCellRenderer(
				table: JTable,
				value: Any?,
				selected: Boolean,
				hasFocus: Boolean,
				row: Int,
				column: Int) {
				val attributes = if (rows[row].isSelectable || rows[row].isIncluded)
					SimpleTextAttributes.REGULAR_ATTRIBUTES
				else
					SimpleTextAttributes.GRAYED_ATTRIBUTES

				append(value?.toString().orEmpty(), attributes)
			}
		})
	}

	val component: JComponent = ToolbarDecorator.createDecorator(table)
		.setAddAction { browse() }
		.setAddActionName("Browse")
		.disableRemoveAction()
		.disableUpDownActions()
		.createPanel()

	init {
		val reachable = reaching(owner)
		for (info in projects.filter { it.path != owner }.sortedBy { it.name.lowercase() })
			rows += Row(info.path, info.name, locationOf(info.path),
				info.path !in reachable && (!info.isExecutable || info.referenceable))
	}

	fun show(chosen: List<ReferenceChoice>, editable: Boolean) {
		table.cellEditor?.stopCellEditing()
		isEditable = editable
		table.isEnabled = editable
		for (row in rows) {
			row.isIncluded = false
			row.alias = ""
		}

		for (choice in chosen) {
			val row = rowFor(choice.target)
			row.isIncluded = true
			row.alias = choice.alias.orEmpty()
		}

		model.fireTableDataChanged()
	}

	fun read(): List<ReferenceChoice> {
		table.cellEditor?.stopCellEditing()
		return rows.filter { it.isIncluded }.map { ReferenceChoice(it.target, it.alias.ifEmpty { null }) }
	}

	private fun rowFor(target: Path): Row = rows.firstOrNull { it.target == target } ?: Row(target,
		target.fileName.toString().removeSuffix(".celp"), locationOf(target), true).also {
		rows += it
		model.fireTableDataChanged()
	}

	private fun browse() {
		val descriptor = FileChooserDescriptor(true, false, false, false, false, false)
			.withFileFilter { it.extension == "celp" }

		val file = FileChooser.chooseFile(descriptor, project, null) ?: return
		val target = file.toNioPath().normalize()
		if (target == owner)
			return

		rowFor(target).isIncluded = true
		model.fireTableDataChanged()
	}

	private fun locationOf(target: Path): String = runCatching {
		owner.parent.relativize(target).toString().replace('\\', '/')
	}.getOrDefault(target.toString())

	private fun reaching(target: Path): Set<Path> {
		val referencedBy = projects.flatMap { info -> info.references.map { it.target to info.path } }
			.groupBy({ it.first }, { it.second })

		val found = mutableSetOf<Path>()
		val pending = ArrayDeque(listOf(target))
		while (pending.isNotEmpty()) {
			for (next in referencedBy[pending.removeFirst()].orEmpty()) {
				if (found.add(next))
					pending.add(next)
			}
		}

		return found
	}
}
