package com.anixias.cella.run

import com.anixias.cella.CellaFileType
import com.anixias.cella.server.CellaSemanticHighlighter
import com.intellij.execution.filters.TextConsoleBuilder
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerUtil
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import com.intellij.xdebugger.breakpoints.XLineBreakpointType
import com.jetbrains.cidr.execution.RunParameters
import com.jetbrains.cidr.execution.debugger.CidrLocalDebugProcess
import com.jetbrains.cidr.execution.debugger.breakpoints.CidrBreakpointHandler
import com.jetbrains.cidr.execution.debugger.breakpoints.CidrLineBreakpointType

class CellaLineBreakpointType :
	XLineBreakpointType<CidrLineBreakpointType.Properties>("cella-line", "Cella Line Breakpoints") {
	override fun canPutAt(file: VirtualFile, line: Int, project: Project): Boolean {
		if (file.fileType != CellaFileType)
			return false

		val document = FileDocumentManager.getInstance().getDocument(file) ?: return true
		return CellaSemanticHighlighter.breakableLines(document)?.contains(line) ?: true
	}

	override fun createBreakpointProperties(file: VirtualFile, line: Int) = CidrLineBreakpointType.Properties()

	@Suppress("UNCHECKED_CAST")
	override fun getEditorsProvider(breakpoint: XLineBreakpoint<CidrLineBreakpointType.Properties>, project: Project) =
		XDebuggerUtil.getInstance().findBreakpointType(CidrLineBreakpointType::class.java)
			.getEditorsProvider(breakpoint as XLineBreakpoint<CidrLineBreakpointType.Properties?>, project)
}

class CellaDebugProcess(parameters: RunParameters, session: XDebugSession, consoleBuilder: TextConsoleBuilder) :
	CidrLocalDebugProcess(parameters, session, consoleBuilder) {
	override fun createBreakpointHandler() = CidrBreakpointHandler(this, CellaLineBreakpointType::class.java)
}
