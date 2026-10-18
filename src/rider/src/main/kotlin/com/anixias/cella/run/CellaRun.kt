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
import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.process.ProcessTerminatedListener
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.execution.runners.GenericProgramRunner
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.openapi.fileChooser.FileChooserDescriptorFactory
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Expirable
import com.intellij.openapi.util.NotNullLazyValue
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
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap

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
	private val globalPattern = Regex("""^(0x[0-9a-fA-F]+): .*? (?:::)?([^\s=]+) = """, RegexOption.MULTILINE)
	private val globalNames = ConcurrentHashMap<Long, String>()
	
	override fun filterLocalVariables(position: XSourcePosition?, variables: List<LLValue>, expirable: Expirable):
		CompletableFuture<List<LLValue>> = CompletableFuture.completedFuture(variables)
	
	override fun filterLocalVariables(frame: CidrStackFrame, position: XSourcePosition?, variables: List<LLValue>,
		expirable: Expirable): CompletableFuture<List<LLValue>> = myProcess.postCommand(expirable,
		CidrDebugProcess.DebuggerCommand { driver -> nameGlobals(driver, frame, variables) })

	private fun nameGlobals(driver: DebuggerDriver, frame: CidrStackFrame, variables: List<LLValue>): List<LLValue> {
		val locals = (driver as LLDBDriver).getVariables(frame.threadId, frame.frameIndex, false, false)
			.mapTo(HashSet()) { it.name }

		val globals = variables.filter { it.name !in locals }
		com.intellij.openapi.diagnostic.Logger.getInstance(CellaDebuggerTypesHelper::class.java).warn("CELLA-PROBE locals=$locals globals=${globals.map { it.name + "@" + it.address?.toString(16) }}")
		if (globals.any { global -> global.address?.let { !globalNames.containsKey(it) } == true })
			readGlobalNames(driver, frame)
		
		val function = frame.frame.function
		return variables.mapNotNull { variable ->
			if (variable.name in locals)
				return@mapNotNull variable
			
			val name = variable.address?.let { globalNames[it] } ?: return@mapNotNull null
			if (!function.startsWith(name.substringBeforeLast('.') + "."))
				return@mapNotNull null
			
			LLValue(name, variable.type, variable.displayType, variable.address, variable.typeClass,
				variable.referenceExpression, variable.fullExpression).also { variable.copyUserDataTo(it) }
		}
	}
	
	private fun readGlobalNames(driver: DebuggerDriver, frame: CidrStackFrame) {
		val output = driver.executeInterpreterCommand(frame.threadId, frame.frameIndex, "target variable -L")
		com.intellij.openapi.diagnostic.Logger.getInstance(CellaDebuggerTypesHelper::class.java).warn("CELLA-PROBE output (${output.length}): ${output.lines().filter { it.contains("counter") || it.contains("error") }.take(10)} first: ${output.take(600)}")
		for (match in globalPattern.findAll(output)) {
			val name = match.groupValues[2]
			if (name.contains('.'))
				globalNames[match.groupValues[1].removePrefix("0x").toLong(16)] = name
		}
	}
}
