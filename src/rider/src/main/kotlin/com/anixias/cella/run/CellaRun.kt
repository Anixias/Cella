package com.anixias.cella.run

import com.anixias.cella.CellaFileType
import com.anixias.cella.CellaIcons
import com.anixias.cella.lexer.CellaToken
import com.anixias.cella.lexer.CellaTokenTypes
import com.anixias.cella.server.CellaSemanticHighlighter
import com.anixias.cella.server.CellaServer
import com.intellij.execution.BeforeRunTask
import com.intellij.execution.BeforeRunTaskProvider
import com.intellij.execution.ExecutionException
import com.intellij.execution.Executor
import com.intellij.execution.configurations.CommandLineState
import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.execution.configurations.RunConfigurationBase
import com.intellij.execution.configurations.RunConfigurationOptions
import com.intellij.execution.configurations.RunProfile
import com.intellij.execution.configurations.RunProfileState
import com.intellij.execution.configurations.RunnerSettings
import com.intellij.execution.configurations.RuntimeConfigurationError
import com.intellij.execution.configurations.SimpleConfigurationType
import com.intellij.execution.executors.DefaultDebugExecutor
import com.intellij.execution.filters.TextConsoleBuilderFactory
import com.intellij.execution.process.KillableColoredProcessHandler
import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.process.ProcessListener
import com.intellij.execution.process.ProcessTerminatedListener
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.execution.runners.GenericProgramRunner
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.openapi.actionSystem.DataContext
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ReadAction
import com.intellij.openapi.application.runReadActionBlocking
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.editor.Document
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Expirable
import com.intellij.openapi.util.Key
import com.intellij.openapi.util.NotNullLazyValue
import com.intellij.openapi.util.SystemInfo
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiDocumentManager
import com.intellij.psi.PsiElement
import com.intellij.psi.util.PsiTreeUtil
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindSelected
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel
import com.intellij.util.execution.ParametersListUtil
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugProcessStarter
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.XExpression
import com.intellij.xdebugger.XSourcePosition
import com.intellij.xdebugger.evaluation.ExpressionInfo
import com.jetbrains.cidr.execution.TrivialRunParameters
import com.jetbrains.cidr.execution.debugger.CidrDebugProcess
import com.jetbrains.cidr.execution.debugger.CidrDebuggerLanguageSupport
import com.jetbrains.cidr.execution.debugger.CidrEvaluator
import com.jetbrains.cidr.execution.debugger.CidrLocalDebugProcess
import com.jetbrains.cidr.execution.debugger.CidrStackFrame
import com.jetbrains.cidr.execution.debugger.backend.DebuggerDriver
import com.jetbrains.cidr.execution.debugger.backend.LLValue
import com.jetbrains.cidr.execution.debugger.backend.lldb.LLDBDriver
import com.jetbrains.cidr.execution.debugger.backend.lldb.LLDBDriverConfiguration
import com.jetbrains.cidr.execution.debugger.breakpoints.CidrLineBreakpointFileTypesProvider
import com.jetbrains.cidr.execution.debugger.evaluation.CidrDebuggerTypesHelper
import com.jetbrains.cidr.execution.debugger.evaluation.CidrEvaluatedValue
import java.io.File
import java.nio.file.Files
import java.nio.file.Path
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import org.eclipse.lsp4j.Position
import org.eclipse.lsp4j.TextDocumentIdentifier
import org.eclipse.lsp4j.TextDocumentPositionParams
import org.jetbrains.concurrency.AsyncPromise
import org.jetbrains.concurrency.Promise
import org.jetbrains.concurrency.resolvedPromise

class CellaConfigurationType : SimpleConfigurationType(
	"CellaProgram", "Cella", "Runs a Cella program", NotNullLazyValue.createValue { CellaIcons.FILE }) {
	override fun createTemplateConfiguration(project: Project): RunConfiguration =
		CellaRunConfiguration(project, this)
}

class CellaRunOptions : RunConfigurationOptions() {
	var project by string("")
	var entry by string("")
	var arguments by string("")
	var release by property(false)
}

class CellaRunConfiguration(project: Project, factory: ConfigurationFactory) :
	RunConfigurationBase<CellaRunOptions>(project, factory, "Cella") {
	public override fun getOptions() = super.getOptions() as CellaRunOptions
	
	override fun getOptionsClass() = CellaRunOptions::class.java
	
	override fun getConfigurationEditor(): SettingsEditor<out RunConfiguration> = CellaRunEditor()
	
	val projectPath: Path?
		get() = options.project?.takeIf { it.isNotBlank() }?.let { Path.of(it) }
	
	val entry: String?
		get() = options.entry?.takeIf { it.isNotBlank() }
	
	override fun checkConfiguration() {
		val celp = projectPath ?: throw RuntimeConfigurationError("Choose a project to run")
		if (!Files.isRegularFile(celp))
			throw RuntimeConfigurationError("'$celp' doesn't exist")
		
		if (entry == null && !CellaProjects.isExecutable(celp))
			throw RuntimeConfigurationError("'${CellaProjects.name(celp)}' isn't an executable project")
	}
	
	fun createCommandLine(): GeneralCommandLine {
		val celp = projectPath ?: throw ExecutionException("Choose a project to run")
		val program = CellaBuild.lastOutput(celp, entry)
			?: throw ExecutionException("Build '${CellaProjects.name(celp)}' before running it")
		
		return GeneralCommandLine(program.toString())
			.withParameters(ParametersListUtil.parse(options.arguments.orEmpty()))
			.withWorkDirectory(celp.parent.toFile())
			.withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
	}
	
	override fun getState(executor: Executor, environment: ExecutionEnvironment): RunProfileState =
		object : CommandLineState(environment) {
			override fun startProcess(): ProcessHandler {
				val handler = KillableColoredProcessHandler(createCommandLine())
				ProcessTerminatedListener.attach(handler)
				return handler
			}
		}
}

class CellaRunEditor : SettingsEditor<CellaRunConfiguration>() {
	private var project = ""
	private var entry = ""
	private var arguments = ""
	private var release = false
	
	private val panel = panel {
		row("Project:") {
			textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileDescriptor("celp"))
				.bindText(::project)
				.align(AlignX.FILL)
		}
		row("Function:") {
			textField()
				.bindText(::entry)
				.align(AlignX.FILL)
		}
		row("Arguments:") {
			textField()
				.bindText(::arguments)
				.align(AlignX.FILL)
		}
		row {
			checkBox("Release build")
				.bindSelected(::release)
		}
	}
	
	override fun resetEditorFrom(configuration: CellaRunConfiguration) {
		project = configuration.options.project.orEmpty()
		entry = configuration.options.entry.orEmpty()
		arguments = configuration.options.arguments.orEmpty()
		release = configuration.options.release
		panel.reset()
	}
	
	override fun applyEditorTo(configuration: CellaRunConfiguration) {
		panel.apply()
		configuration.options.project = project
		configuration.options.entry = entry
		configuration.options.arguments = arguments
		configuration.options.release = release
	}
	
	override fun createEditor() = panel
}

class CellaBuildTask : BeforeRunTask<CellaBuildTask>(CellaBuildTaskProvider.ID)

class CellaBuildTaskProvider : BeforeRunTaskProvider<CellaBuildTask>() {
	companion object {
		val ID = Key.create<CellaBuildTask>("Cella.Build")
	}
	
	override fun getId() = ID
	
	override fun getName() = "Build Cella project"
	
	override fun getIcon() = CellaIcons.FILE
	
	override fun createTask(configuration: RunConfiguration) =
		if (configuration is CellaRunConfiguration) CellaBuildTask().apply { isEnabled = true } else null
	
	override fun executeTask(context: DataContext, configuration: RunConfiguration, environment: ExecutionEnvironment,
		task: CellaBuildTask): Boolean {
		val cella = configuration as? CellaRunConfiguration ?: return false
		val celp = cella.projectPath ?: return false
		ApplicationManager.getApplication().invokeAndWait { FileDocumentManager.getInstance().saveAllDocuments() }
		return CellaBuild.build(environment.project, celp, cella.options.release, cella.entry).succeeded
	}
}

class CellaDebugRunner : GenericProgramRunner<RunnerSettings>() {
	override fun getRunnerId() = "CellaDebugRunner"
	
	override fun canRun(executorId: String, profile: RunProfile) =
		executorId == DefaultDebugExecutor.EXECUTOR_ID && profile is CellaRunConfiguration
	
	override fun doExecute(state: RunProfileState, environment: ExecutionEnvironment): RunContentDescriptor {
		val configuration = environment.runProfile as CellaRunConfiguration
		val parameters = TrivialRunParameters(CellaDriverConfiguration(), configuration.createCommandLine())
		val consoleBuilder = TextConsoleBuilderFactory.getInstance().createBuilder(environment.project)
		return XDebuggerManager.getInstance(environment.project).startSession(environment, object : XDebugProcessStarter() {
			override fun start(session: XDebugSession): XDebugProcess {
				val process = CidrLocalDebugProcess(parameters, session, consoleBuilder)
				ProcessTerminatedListener.attach(process.processHandler, environment.project)
				process.start()
				return process
			}
		}).runContentDescriptor
	}
}

class CellaBreakpointFileTypes : CidrLineBreakpointFileTypesProvider {
	override fun getFileTypes() = setOf(CellaFileType)
}

class CellaDebuggerLanguageSupport : CidrDebuggerLanguageSupport() {
	override fun createTypesHelper(process: CidrDebugProcess) = CellaDebuggerTypesHelper(process)
	
	override fun createDebuggerTypesHelper(process: CidrDebugProcess) = CellaDebuggerTypesHelper(process)
	
	override fun createEvaluator(frame: CidrStackFrame) = CellaEvaluator(frame)
}

class CellaEvaluator(frame: CidrStackFrame) : CidrEvaluator(frame) {
	private val hoveredGlobals = ConcurrentHashMap<String, String>()
	
	override fun getExpressionInfoAtOffsetAsync(project: Project, document: Document, offset: Int,
		sideEffectsAllowed: Boolean): Promise<ExpressionInfo?> {
		val info = ReadAction.compute<ExpressionInfo?, RuntimeException> { findExpression(project, document, offset) }
		val file = FileDocumentManager.getInstance().getFile(document)
		if (info == null || file == null)
			return resolvedPromise(info)
		
		val server = CellaServer.get(project)
		val parameters = TextDocumentPositionParams(TextDocumentIdentifier(CellaServer.uriOf(file)),
			server.position(document, offset))
		
		val promise = AsyncPromise<ExpressionInfo?>()
		server.request { it.globalAt(parameters) }.whenComplete { global, _ ->
			if (global == null) {
				promise.setResult(info)
				return@whenComplete
			}
			
			val text = runReadActionBlocking { document.getText(info.textRange) }
			hoveredGlobals[global.debugName] = text
			promise.setResult(ExpressionInfo(info.textRange, global.debugName, text))
		}
		
		return promise
	}
	
	override fun doEvaluate(driver: DebuggerDriver, position: XSourcePosition?, expression: XExpression):
		CidrEvaluatedValue {
		val helper = myFrame.process.getTypesHelper(myFrame) as? CellaDebuggerTypesHelper
		val global = helper?.findGlobal(driver, myFrame, expression.expression, hoveredGlobals[expression.expression])
			?: return super.doEvaluate(driver, position, expression)
		
		return CidrEvaluatedValue(global, myFrame.process, position, myFrame, expression.expression)
	}
	
	private fun findExpression(project: Project, document: Document, offset: Int): ExpressionInfo? {
		val file = PsiDocumentManager.getInstance(project).getPsiFile(document) ?: return null
		val element = file.findElementAt(offset) ?: return null
		if (!isSelf(element) && !CellaSemanticHighlighter.isValue(document, element.textRange))
			return null
		
		var start = element
		while (true) {
			val dot = PsiTreeUtil.prevLeaf(start) ?: break
			val owner = PsiTreeUtil.prevLeaf(dot) ?: break
			if (CellaTokenTypes.tokenOf(dot.node.elementType) != CellaToken.DOT ||
			    !isSelf(owner) && !CellaSemanticHighlighter.isValue(document, owner.textRange))
				break
			
			start = owner
		}
		
		return ExpressionInfo(TextRange(start.textRange.startOffset, element.textRange.endOffset))
	}
	
	private fun isSelf(element: PsiElement) = element.text == "self" &&
		CellaTokenTypes.tokenOf(element.node.elementType) != CellaToken.IDENTIFIER
}

class CellaDriverConfiguration : LLDBDriverConfiguration() {
	override fun isStaticVarsLoadingEnabled() = true
}

class CellaDebuggerTypesHelper(process: CidrDebugProcess) : CidrDebuggerTypesHelper(process) {
	private var windowsSymbols: WindowsSymbols? = null
	private var hasOpenedSymbols = false
	
	override fun filterLocalVariables(position: XSourcePosition?, variables: List<LLValue>, expirable: Expirable):
		CompletableFuture<List<LLValue>> = CompletableFuture.completedFuture(variables)
	
	override fun filterLocalVariables(frame: CidrStackFrame, position: XSourcePosition?, variables: List<LLValue>,
		expirable: Expirable): CompletableFuture<List<LLValue>> = myProcess.postCommand(expirable,
		CidrDebugProcess.DebuggerCommand { driver -> nameGlobals(driver, frame, variables) })
	
	private fun nameGlobals(driver: DebuggerDriver, frame: CidrStackFrame, variables: List<LLValue>): List<LLValue> {
		val locals = (driver as LLDBDriver).getVariables(frame.threadId, frame.frameIndex, false, false)
			.mapTo(HashSet()) { it.name }
		
		val visible = findVisibleGlobals(frame)
		return variables.mapNotNull { variable ->
			if (variable.name in locals)
				return@mapNotNull variable
			
			findDebugName(variable)?.let(visible::get)?.let { rename(variable, it) }
		}
	}
	
	fun findGlobal(driver: DebuggerDriver, frame: CidrStackFrame, expression: String, hovered: String?): LLValue? {
		val lldb = driver as LLDBDriver
		val locals = lldb.getVariables(frame.threadId, frame.frameIndex, false, false).mapTo(HashSet()) { it.name }
		val (debugName, name) = when {
			hovered != null -> expression to hovered
			expression in locals -> return null
			else -> findVisibleGlobals(frame).entries.firstOrNull { it.value == expression }?.toPair() ?: return null
		}
		
		return lldb.getVariables(frame.threadId, frame.frameIndex, true, true)
			.filter { it.name !in locals }
			.firstOrNull { findDebugName(it) == debugName }
			?.let { rename(it, name) }
	}
	
	private fun findVisibleGlobals(frame: CidrStackFrame): Map<String, String> {
		val position = frame.sourcePosition ?: return emptyMap()
		val server = CellaServer.get(myProcess.project)
		val parameters = TextDocumentPositionParams(TextDocumentIdentifier(CellaServer.uriOf(position.file)),
			Position(position.line, 0))
		
		return server.await(server.request { it.visibleGlobals(parameters) }).orEmpty()
			.associate { it.debugName to it.name }
	}
	
	private fun rename(variable: LLValue, name: String) = LLValue(name, variable.type, variable.displayType,
		variable.address, variable.typeClass, variable.referenceExpression, variable.fullExpression)
		.also { variable.copyUserDataTo(it) }
	
	private fun findDebugName(variable: LLValue): String? {
		if (!SystemInfo.isWindows)
			return variable.name.removePrefix("::")
		
		val address = variable.address ?: return null
		return openWindowsSymbols()?.dataNameAt(address)
	}
	
	private fun openWindowsSymbols(): WindowsSymbols? {
		if (hasOpenedSymbols)
			return windowsSymbols
		
		hasOpenedSymbols = true
		val executable = myProcess.runParameters.installer.executableFile.toPath().toAbsolutePath()
		val debugger = ProcessHandle.of(myProcess.localDebuggerPid ?: return null).orElse(null) ?: return null
		val program = debugger.descendants()
			.filter { child -> child.info().command().map { Path.of(it) == executable }.orElse(false) }
			.findFirst().orElse(null)
		
		if (program == null) {
			logger<CellaDebuggerTypesHelper>().info("No '$executable' process found under debugger ${debugger.pid()}")
			return null
		}
		
		windowsSymbols = WindowsSymbols.open(program.pid(), executable.parent.toString())?.also { symbols ->
			myProcess.processHandler.addProcessListener(object : ProcessListener {
				override fun processTerminated(event: ProcessEvent) = symbols.close()
			})
		}
		
		return windowsSymbols
	}
}
