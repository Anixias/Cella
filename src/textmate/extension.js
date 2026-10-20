const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");
const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");
const { register } = require("./projects");

const cppTools = "ms-vscode.cpptools";
const configurationKey = "cella.configuration";
let client;
let output;
let state;
let configurationItem;

async function activate(context) {
	output = vscode.window.createOutputChannel("Cella");
	state = context.workspaceState;
	configurationItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
	configurationItem.command = "cella.selectConfiguration";
	configurationItem.tooltip = "Cella build configuration";
	showConfiguration();
	context.subscriptions.push(
		output,
		configurationItem,
		vscode.debug.registerDebugConfigurationProvider("cella", new DebugConfigurationProvider(context.extensionPath)),
		vscode.languages.registerCodeLensProvider({ language: "cella" }, new RunLensProvider()),
		vscode.tasks.registerTaskProvider("cella", new BuildTaskProvider()),
		vscode.commands.registerCommand("cella.start", startProgram),
		vscode.commands.registerCommand("cella.selectConfiguration", selectConfiguration),
		vscode.workspace.onDidChangeConfiguration(event => {
			if (event.affectsConfiguration("launch"))
				configurationChanged();
		})
	);

	const projects = register(context, () => client, {
		cellaCommand,
		findProject,
		startProgram,
		buildTask: project => new BuildTaskProvider().createTask({ type: "cella", project },
			vscode.workspace.getWorkspaceFolder(vscode.Uri.file(project)) ?? vscode.TaskScope.Workspace)
	});

	const configured = setting("server.path");
	const options = { cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath };
	const server = configured
		? { command: configured, transport: TransportKind.stdio, options }
		: { command: cellaCommand(), args: ["lsp"], transport: TransportKind.stdio, options };

	client = new LanguageClient("cella", "Cella", server, {
		initializationOptions: () => ({ configuration: serverConfiguration(activeConfiguration()) }),
		documentSelector: [
			{ scheme: "file", language: "cella" },
			{ scheme: "file", language: "cella-project" }
		],
		synchronize: {
			fileEvents: vscode.workspace.createFileSystemWatcher("**/*.{ce,celp}")
		}
	});

	try {
		await client.start();
		await projects.refresh();
	} catch (error) {
		vscode.window.showErrorMessage(`Couldn't start the Cella language server '${server.command}': ${error.message}`);
	}
}

function deactivate() {
	return client?.stop();
}

function setting(name) {
	return vscode.workspace.getConfiguration("cella").get(name);
}

function cellaCommand() {
	return setting("path") || "cella";
}

function findProject(file) {
	if (file.endsWith(".celp"))
		return file;

	let directory = path.dirname(file);
	while (true) {
		const projects = fs.readdirSync(directory).filter(name => name.endsWith(".celp")).sort();
		if (projects.length > 0)
			return path.join(directory, projects[0]);

		const parent = path.dirname(directory);
		if (parent === directory)
			return undefined;

		directory = parent;
	}
}

function projectName(project) {
	return path.basename(project, ".celp");
}

function isExecutable(project) {
	const text = fs.readFileSync(project, "utf8");
	const table = text.search(/^\s*\[/m);
	const topLevel = table < 0 ? text : text.slice(0, table);
	const match = /^\s*OutputType\s*=\s*["']([A-Za-z]+)["']/m.exec(topLevel);
	return match !== null && (match[1] === "Executable" || match[1] === "WindowedExecutable");
}

function buildArguments(project, release, entry, flags) {
	return [
		"build",
		project,
		...(release ? ["--release"] : []),
		...(entry ? ["--entry", entry] : []),
		...Object.entries(flags ?? {}).flatMap(([name, value]) => ["--flag", `${name}=${value === true}`])
	];
}

function build(project, release, entry, flags) {
	const directory = path.dirname(project);
	const name = projectName(project);
	const args = buildArguments(project, release, entry, flags);
	output.appendLine(`> ${cellaCommand()} ${args.join(" ")}`);
	return new Promise(resolve => {
		const child = childProcess.spawn(cellaCommand(), args, { cwd: directory });
		let text = "";
		const collect = chunk => {
			const piece = chunk.toString("utf8");
			text += piece;
			output.append(piece);
		};

		child.stdout.on("data", collect);
		child.stderr.on("data", collect);
		child.on("error", error => {
			output.appendLine(error.message);
			resolve(undefined);
		});
		child.on("close", code => {
			const prefix = `${name} -> `;
			const line = text.split(/\r?\n/).find(candidate => candidate.startsWith(prefix));
			resolve(code === 0 && line ? path.resolve(directory, line.slice(prefix.length).trim()) : undefined);
		});
	});
}

class DebugConfigurationProvider {
	constructor(extensionPath) {
		this.extensionPath = extensionPath;
	}
	
	resolveDebugConfiguration(folder, configuration) {
		if (configuration.type)
			return configuration;

		const file = vscode.window.activeTextEditor?.document.uri.fsPath;
		const project = file && findProject(file);
		if (!project)
			return undefined;

		const active = activeConfiguration();
		return {
			type: "cella",
			request: "launch",
			name: projectName(project),
			project,
			release: active.release,
			flags: active.flags
		};
	}

	async resolveDebugConfigurationWithSubstitutedVariables(folder, configuration) {
		const debuggers = vscode.extensions.getExtension(cppTools);
		if (!debuggers) {
			const install = "Install";
			if (await vscode.window.showErrorMessage(
				"Running Cella programs needs Microsoft's C/C++ extension.", install) === install)
				await vscode.commands.executeCommand("workbench.extensions.installExtension", cppTools);

			return undefined;
		}

		const project = configuration.project && path.resolve(folder?.uri.fsPath ?? "", configuration.project);
		if (!project || !fs.existsSync(project)) {
			vscode.window.showErrorMessage(`Cella project '${configuration.project ?? ""}' not found`);
			return undefined;
		}

		const program = await vscode.window.withProgress(
			{ location: vscode.ProgressLocation.Window, title: `Building ${projectName(project)}` },
			() => build(project, configuration.release, configuration.function, configuration.flags));

		if (!program) {
			output.show(true);
			vscode.window.showErrorMessage(`Building ${projectName(project)} failed`);
			return undefined;
		}

		await debuggers.activate();
		const common = {
			name: configuration.name,
			request: "launch",
			program,
			args: configuration.args ?? [],
			cwd: path.dirname(project),
			noDebug: configuration.noDebug
		};

		return process.platform === "win32"
			? { ...common, type: "cppvsdbg", console: "integratedTerminal" }
			: {
				...common,
				type: "cppdbg",
				MIMode: "gdb",
				externalConsole: false,
				setupCommands: [
					{ text: "-enable-pretty-printing", ignoreFailures: true },
					{ text: `source ${path.join(this.extensionPath, "debug", "cella_gdb.py")}`, ignoreFailures: true }
				]
			};
	}
}

function startProgram(project, entry, noDebug) {
	const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(project));
	const active = activeConfiguration();
	return vscode.debug.startDebugging(folder, {
		type: "cella",
		request: "launch",
		name: entry ?? projectName(project),
		project,
		release: active.release,
		flags: active.flags,
		...(entry ? { function: entry } : {})
	}, { noDebug });
}

function launchConfigurations() {
	return (vscode.workspace.workspaceFolders ?? []).flatMap(folder =>
		(vscode.workspace.getConfiguration("launch", folder.uri).get("configurations") ?? [])
			.filter(configuration => configuration.type === "cella" && typeof configuration.name === "string"));
}

function activeConfiguration() {
	const name = state?.get(configurationKey) ?? "Debug";
	if (name === "Debug" || name === "Release")
		return { name, release: name === "Release", flags: {} };

	const launch = launchConfigurations().find(configuration => configuration.name === name);
	return launch
		? { name, release: launch.release === true, flags: launch.flags ?? {} }
		: { name: "Debug", release: false, flags: {} };
}

function serverConfiguration(configuration) {
	return { release: configuration.release, flags: configuration.flags };
}

function describeConfiguration(configuration) {
	return [
		...(configuration.release === true ? ["release"] : []),
		...Object.entries(configuration.flags ?? {}).map(([name, value]) => `${name}=${value === true}`)
	].join(" ");
}

function showConfiguration() {
	configurationItem.text = `$(tools) ${activeConfiguration().name}`;
	configurationItem.show();
}

function configurationChanged() {
	showConfiguration();
	client?.sendNotification("cella/configuration", serverConfiguration(activeConfiguration()));
}

async function selectConfiguration() {
	const choices = [
		{ label: "Debug" },
		{ label: "Release" },
		...launchConfigurations().map(configuration => ({
			label: configuration.name,
			description: describeConfiguration(configuration)
		}))
	];

	const picked = await vscode.window.showQuickPick(choices, { placeHolder: "Build configuration" });
	if (!picked)
		return;

	await state.update(configurationKey, picked.label);
	configurationChanged();
}

class RunLensProvider {
	async provideCodeLenses(document) {
		const project = findProject(document.uri.fsPath);
		if (!project || !client)
			return [];

		const runnables = await client.sendRequest("cella/runnables", {
			textDocument: { uri: document.uri.toString() }
		}).catch(() => []);

		const executable = isExecutable(project);
		return runnables.filter(runnable => !runnable.main || executable).flatMap(runnable => {
			const range = client.protocol2CodeConverter.asRange(runnable.range);
			const entry = runnable.main ? undefined : runnable.name;
			return [
				new vscode.CodeLens(range, { title: "Run", command: "cella.start", arguments: [project, entry, true] }),
				new vscode.CodeLens(range, { title: "Debug", command: "cella.start", arguments: [project, entry, false] })
			];
		});
	}
}

class BuildTaskProvider {
	async provideTasks() {
		const projects = await vscode.workspace.findFiles("**/*.celp", "**/obj/**");
		return projects.map(uri => this.createTask({ type: "cella", project: uri.fsPath },
			vscode.workspace.getWorkspaceFolder(uri) ?? vscode.TaskScope.Workspace));
	}

	resolveTask(task) {
		return task.definition.project ? this.createTask(task.definition, task.scope) : undefined;
	}

	createTask(definition, scope) {
		const project = path.resolve(scope?.uri?.fsPath ?? "", definition.project);
		const task = new vscode.Task(definition, scope, `build ${projectName(project)}`, "cella",
			new vscode.ProcessExecution(cellaCommand(), buildArguments(project, definition.release),
				{ cwd: path.dirname(project) }));

		task.group = vscode.TaskGroup.Build;
		return task;
	}
}

module.exports = { activate, deactivate };
