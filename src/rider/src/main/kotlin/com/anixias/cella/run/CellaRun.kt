package com.anixias.cella.run

import com.anixias.cella.CellaFileType
import com.anixias.cella.CellaIcons
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
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Expirable
import com.intellij.openapi.util.NotNullLazyValue
import com.intellij.openapi.util.SystemInfo
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.bindText
import com.intellij.ui.dsl.builder.panel
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugProcessStarter
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.XSourcePosition
import com.jetbrains.cidr.execution.TrivialRunParameters
import com.jetbrains.cidr.execution.debugger.CidrDebugProcess
import com.jetbrains.cidr.execution.debugger.CidrDebuggerLanguageSupport
import com.jetbrains.cidr.execution.debugger.CidrLocalDebugProcess
import com.jetbrains.cidr.execution.debugger.CidrStackFrame
import com.jetbrains.cidr.execution.debugger.backend.DebuggerDriver
import com.jetbrains.cidr.execution.debugger.backend.LLValue
import com.jetbrains.cidr.execution.debugger.backend.lldb.LLDBDriver
import com.jetbrains.cidr.execution.debugger.backend.lldb.LLDBDriverConfiguration
import com.jetbrains.cidr.execution.debugger.breakpoints.CidrLineBreakpointFileTypesProvider
import com.jetbrains.cidr.execution.debugger.evaluation.CidrDebuggerTypesHelper
import java.io.File
import java.nio.file.Path
import java.util.concurrent.CompletableFuture

class CellaConfigurationType : SimpleConfigurationType(
	"CellaProgram", "Cella", "Runs a Cella program", NotNullLazyValue.createValue { CellaIcons.FILE }) {
	override fun createTemplateConfiguration(project: Project): RunConfiguration =
		CellaRunConfiguration(project, this)
}

class CellaRunOptions : RunConfigurationOptions() {
	var executable by string("")
}

class CellaRunConfiguration(project: Project, factory: ConfigurationFactory) :
	RunConfigurationBase<CellaRunOptions>(project, factory, "Cella") {
	public override fun getOptions() = super.getOptions() as CellaRunOptions

	override fun getOptionsClass() = CellaRunOptions::class.java

	override fun getConfigurationEditor(): SettingsEditor<out RunConfiguration> = CellaRunEditor()

	override fun checkConfiguration() {
		if (options.executable.isNullOrBlank())
			throw RuntimeConfigurationError("Choose a program to run")
	}

	fun createCommandLine(): GeneralCommandLine {
		val executable = File(options.executable.orEmpty())
		return GeneralCommandLine(executable.path)
			.withWorkDirectory(executable.parentFile)
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
	private var executable = ""

	private val panel = panel {
		row("Program:") {
			textFieldWithBrowseButton(FileChooserDescriptorFactory.createSingleFileNoJarsDescriptor())
				.bindText(::executable)
				.align(AlignX.FILL)
		}
	}

	override fun resetEditorFrom(configuration: CellaRunConfiguration) {
		executable = configuration.options.executable.orEmpty()
		panel.reset()
	}

	override fun applyEditorTo(configuration: CellaRunConfiguration) {
		panel.apply()
		configuration.options.executable = executable
	}

	override fun createEditor() = panel
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
		
		val function = frame.frame.function
		return variables.mapNotNull { variable ->
			if (variable.name in locals)
				return@mapNotNull variable
			
			val name = findSourceName(variable) ?: return@mapNotNull null
			if (!function.startsWith(name.substringBeforeLast('.') + "."))
				return@mapNotNull null
			
			LLValue(name, variable.type, variable.displayType, variable.address, variable.typeClass,
				variable.referenceExpression, variable.fullExpression).also { variable.copyUserDataTo(it) }
		}
	}
	
	private fun findSourceName(variable: LLValue): String? {
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
