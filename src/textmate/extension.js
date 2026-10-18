const vscode = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");
const childProcess = require("child_process");
const fs = require("fs");
const path = require("path");

const cppTools = "ms-vscode.cpptools";
let client;
let output;

async function activate(context) {
	output = vscode.window.createOutputChannel("Cella");
	context.subscriptions.push(
		output,
		vscode.debug.registerDebugConfigurationProvider("cella", new DebugConfigurationProvider()),
		vscode.languages.registerCodeLensProvider({ language: "cella" }, new RunLensProvider()),
		vscode.tasks.registerTaskProvider("cella", new BuildTaskProvider()),
		vscode.commands.registerCommand("cella.start", startProgram)
	);

	const configured = setting("server.path");
	const options = { cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath };
	const server = configured
		? { command: configured, transport: TransportKind.stdio, options }
		: { command: cellaCommand(), args: ["lsp"], transport: TransportKind.stdio, options };

	client = new LanguageClient("cella", "Cella", server, {
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

function buildArguments(project, release, entry) {
	return ["build", project, ...(release ? ["--release"] : []), ...(entry ? ["--entry", entry] : [])];
}

function build(project, release, entry) {
	const directory = path.dirname(project);
	const name = projectName(project);
	output.appendLine(`> ${cellaCommand()} ${buildArguments(project, release, entry).join(" ")}`);
	return new Promise(resolve => {
		const child = childProcess.spawn(cellaCommand(), buildArguments(project, release, entry), { cwd: directory });
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
	resolveDebugConfiguration(folder, configuration) {
		if (configuration.type)
			return configuration;

		const file = vscode.window.activeTextEditor?.document.uri.fsPath;
		const project = file && findProject(file);
		if (!project)
			return undefined;

		return { type: "cella", request: "launch", name: projectName(project), project };
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
			() => build(project, configuration.release, configuration.function));

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
			: { ...common, type: "cppdbg", MIMode: "gdb", externalConsole: false };
	}
}

function startProgram(project, entry, noDebug) {
	const folder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(project));
	return vscode.debug.startDebugging(folder, {
		type: "cella",
		request: "launch",
		name: entry ?? projectName(project),
		project,
		...(entry ? { function: entry } : {})
	}, { noDebug });
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
