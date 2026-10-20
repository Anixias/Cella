const vscode = require("vscode");
const childProcess = require("child_process");
const path = require("path");

const buildFolders = new Set(["bin", "obj"]);
const settingsTitle = "Cella";

function key(file) {
	const normalized = path.normalize(file);
	return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function isExecutable(project) {
	return project.outputType === "Executable" || project.outputType === "WindowedExecutable";
}

class ProjectModel {
	constructor(getClient) {
		this.getClient = getClient;
		this.projects = [];
		this.changed = new vscode.EventEmitter();
	}

	supportsProjects() {
		const client = this.getClient();
		return client?.isRunning() && client.initializeResult?.capabilities?.experimental?.projects === true;
	}

	async request(method, parameters) {
		if (!this.supportsProjects())
			throw new Error("Cella's language server isn't available.");

		return this.getClient().sendRequest(method, parameters);
	}

	async refresh() {
		if (!this.supportsProjects())
			return;

		try {
			this.projects = (await this.request("cella/projects", {})).map(project => ({
				...project,
				path: vscode.Uri.parse(project.uri).fsPath,
				references: project.references.map(reference => ({
					...reference,
					ownerUri: project.uri,
					target: vscode.Uri.parse(reference.uri).fsPath
				}))
			}));
		} catch {
			return;
		}

		await vscode.commands.executeCommand("setContext", "cella.hasProjects", this.projects.length > 0);
		this.changed.fire();
	}

	find(file) {
		return this.projects.find(project => key(project.path) === key(file));
	}

	settings(document) {
		return this.request("cella/projectSettings", {
			textDocument: { uri: document.uri.toString() },
			text: document.getText()
		});
	}

	async edit(document, edits) {
		if (edits.length === 0)
			return true;

		try {
			const result = await this.request("cella/editProject", {
				textDocument: { uri: document.uri.toString() },
				text: document.getText(),
				edits
			});

			const change = new vscode.WorkspaceEdit();
			for (const edit of result.edits)
				change.replace(document.uri, new vscode.Range(edit.range.start.line, edit.range.start.character,
					edit.range.end.line, edit.range.end.character), edit.newText);

			return await vscode.workspace.applyEdit(change);
		} catch (error) {
			vscode.window.showErrorMessage(error.message);
			return false;
		}
	}
}

function referenceEdits(original, chosen, condition) {
	const existing = original.filter(reference => (reference.condition ?? null) === condition);
	const chosenTargets = new Map(chosen.map(choice => [key(choice.target), choice]));
	const existingTargets = new Set(existing.map(reference => key(reference.target)));
	const scoped = edit => condition === null ? edit : { ...edit, condition };
	const edits = [];
	for (const reference of existing) {
		const choice = chosenTargets.get(key(reference.target));
		if (choice && (choice.alias ?? "") !== (reference.alias ?? ""))
			edits.push(scoped({ reference: reference.index, alias: choice.alias ?? null }));
	}

	for (const reference of existing.filter(reference => !chosenTargets.has(key(reference.target)))
		.sort((left, right) => right.index - left.index))
		edits.push(scoped({ removeReference: reference.index }));

	for (const choice of chosen.filter(choice => !existingTargets.has(key(choice.target))))
		edits.push(scoped({ addReference: vscode.Uri.file(choice.target).toString(), ...(choice.alias ? { alias: choice.alias } : {}) }));

	return edits;
}

function blockedTargets(owner, projects) {
	const referencedBy = new Map();
	for (const project of projects)
		for (const reference of project.references) {
			const list = referencedBy.get(key(reference.target)) ?? [];
			list.push(key(project.path));
			referencedBy.set(key(reference.target), list);
		}

	const found = new Set();
	const pending = [key(owner)];
	while (pending.length > 0)
		for (const next of referencedBy.get(pending.shift()) ?? [])
			if (!found.has(next)) {
				found.add(next);
				pending.push(next);
			}

	return found;
}

function referencesFrom(settings, document) {
	const directory = path.dirname(document.uri.fsPath);
	return settings.references.map(reference => ({
		...reference,
		target: path.resolve(directory, reference.path)
	}));
}

async function pickProjects(model, owner, current, canPickMany, title) {
	await model.refresh();
	const blocked = blockedTargets(owner, model.projects);
	const directory = path.dirname(owner);
	const items = model.projects
		.filter(project => key(project.path) !== key(owner) && !blocked.has(key(project.path)) &&
			(!isExecutable(project) || project.referenceable))
		.sort((left, right) => left.name.localeCompare(right.name))
		.map(project => ({
			label: project.name,
			description: path.relative(directory, project.path).replace(/\\/g, "/"),
			picked: current.some(reference => key(reference.target) === key(project.path)),
			target: project.path
		}));

	for (const reference of current)
		if (!items.some(item => key(item.target) === key(reference.target)))
			items.push({ label: path.basename(reference.target, ".celp"), description: reference.path, picked: true, target: reference.target });

	return vscode.window.showQuickPick(items, { canPickMany, title });
}

class ProjectTree {
	constructor(model, icon) {
		this.model = model;
		this.icon = icon;
		this.changed = new vscode.EventEmitter();
		this.onDidChangeTreeData = this.changed.event;
		model.changed.event(() => this.changed.fire());
	}

	refresh() {
		this.changed.fire();
	}

	getTreeItem(element) {
		return element.item;
	}

	async getChildren(element) {
		if (!element) {
			const folders = this.model.projects.map(project => key(path.dirname(project.path)));
			return this.model.projects
				.filter(project => !folders.some(folder => key(path.dirname(project.path)).startsWith(folder + path.sep)))
				.sort((left, right) => left.name.localeCompare(right.name))
				.map(project => this.projectNode(project.path));
		}

		if (element.kind === "project") {
			const project = this.model.find(element.path);
			const nodes = project?.references.length ? [this.referencesNode(project)] : [];
			return nodes.concat(await this.folderChildren(path.dirname(element.path)));
		}

		if (element.kind === "references")
			return element.project.references.map(reference => this.referenceNode(reference));

		if (element.kind === "folder")
			return this.folderChildren(element.path);

		return [];
	}

	projectNode(file) {
		const project = this.model.find(file);
		const item = new vscode.TreeItem(path.basename(file, ".celp"), vscode.TreeItemCollapsibleState.Collapsed);
		item.iconPath = this.icon;
		item.id = `project:${key(file)}`;
		item.contextValue = project && isExecutable(project) ? "cellaProjectExecutable" : "cellaProject";
		return { kind: "project", path: file, item };
	}

	referencesNode(project) {
		const item = new vscode.TreeItem("References", vscode.TreeItemCollapsibleState.Collapsed);
		item.iconPath = new vscode.ThemeIcon("references");
		item.id = `references:${key(project.path)}`;
		item.contextValue = "cellaReferences";
		return { kind: "references", path: project.path, project, item };
	}

	referenceNode(reference) {
		const item = new vscode.TreeItem(path.basename(reference.target, ".celp"), vscode.TreeItemCollapsibleState.None);
		item.iconPath = reference.exists ? this.icon : new vscode.ThemeIcon("warning");
		item.description = reference.condition ?? undefined;
		item.id = `reference:${reference.ownerUri}:${reference.condition ?? ""}:${reference.index}:${key(reference.target)}`;
		item.contextValue = "cellaReference";
		if (reference.exists)
			item.command = { command: "vscode.open", title: "Open", arguments: [vscode.Uri.file(reference.target)] };

		return { kind: "reference", reference, item };
	}

	async folderChildren(folder) {
		let entries;
		try {
			entries = await vscode.workspace.fs.readDirectory(vscode.Uri.file(folder));
		} catch {
			return [];
		}

		const excluded = Object.entries(vscode.workspace.getConfiguration("files").get("exclude") ?? {})
			.filter(([, value]) => value === true)
			.map(([pattern]) => pattern.replace(/^\*\*\//, ""));

		const visible = entries
			.filter(([name]) => !excluded.includes(name))
			.sort(([left], [right]) => left.toLowerCase().localeCompare(right.toLowerCase()));

		const nodes = [];
		for (const [name, type] of visible.filter(([, type]) => type === vscode.FileType.Directory)) {
			if (buildFolders.has(name))
				continue;

			const child = path.join(folder, name);
			const projects = await this.projectFiles(child);
			if (projects.length > 0)
				nodes.push(...projects.map(project => this.projectNode(project)));
			else
				nodes.push(this.fileNode(child, true));
		}

		for (const [name] of visible.filter(([, type]) => type !== vscode.FileType.Directory))
			if (!name.endsWith(".celp"))
				nodes.push(this.fileNode(path.join(folder, name), false));

		return nodes;
	}

	async projectFiles(folder) {
		try {
			return (await vscode.workspace.fs.readDirectory(vscode.Uri.file(folder)))
				.filter(([name, type]) => type !== vscode.FileType.Directory && name.endsWith(".celp"))
				.map(([name]) => path.join(folder, name))
				.sort();
		} catch {
			return [];
		}
	}

	fileNode(file, isFolder) {
		const item = new vscode.TreeItem(vscode.Uri.file(file),
			isFolder ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);

		item.id = `file:${key(file)}`;
		item.contextValue = isFolder ? "cellaFolder" : "cellaFile";
		if (!isFolder)
			item.command = { command: "vscode.open", title: "Open", arguments: [vscode.Uri.file(file)] };

		return { kind: isFolder ? "folder" : "file", path: file, item };
	}
}

class PropertiesEditor {
	constructor(context, model, cellaCommand) {
		this.context = context;
		this.model = model;
		this.cellaCommand = cellaCommand;
	}

	installedVersions() {
		return new Promise(resolve => childProcess.execFile(this.cellaCommand(), ["list", "--installed"],
			(error, stdout) => resolve(error ? [] : stdout.split(/\r?\n/).map(line => line.trim()).filter(Boolean))));
	}

	resolveCustomTextEditor(document, panel) {
		const media = vscode.Uri.joinPath(this.context.extensionUri, "media");
		panel.webview.options = { enableScripts: true, localResourceRoots: [media] };
		panel.webview.html = this.html(panel.webview, media);
		const versions = this.installedVersions();

		const update = async () => {
			try {
				panel.webview.postMessage({ type: "settings", settings: await this.model.settings(document), versions: await versions });
			} catch (error) {
				panel.webview.postMessage({ type: "error", message: error.message });
			}
		};

		const changes = vscode.workspace.onDidChangeTextDocument(event => {
			if (event.document.uri.toString() === document.uri.toString())
				update();
		});

		panel.onDidDispose(() => changes.dispose());
		panel.webview.onDidReceiveMessage(async message => {
			switch (message.type) {
				case "ready":
					await update();
					break;

				case "edit":
					await this.model.edit(document, message.edits);
					break;

				case "addCondition": {
					const condition = (await vscode.window.showInputBox({ title: "Add Condition", prompt: "Condition" }))?.trim();
					if (condition)
						panel.webview.postMessage({ type: "condition", condition });

					break;
				}

				case "addReference": {
					const settings = await this.model.settings(document);
					const current = referencesFrom(settings, document).filter(reference => (reference.condition ?? null) === message.condition);
					const picked = await pickProjects(this.model, document.uri.fsPath, current, false, "Add Reference");
					if (picked && !current.some(reference => key(reference.target) === key(picked.target)))
						await this.model.edit(document, referenceEdits(current, [...current, { target: picked.target }], message.condition));

					break;
				}

				case "openText":
					await vscode.commands.executeCommand("vscode.openWith", document.uri, "default");
					break;
			}
		});
	}

	html(webview, media) {
		const nonce = [...Array(32)].map(() => Math.floor(Math.random() * 36).toString(36)).join("");
		const script = webview.asWebviewUri(vscode.Uri.joinPath(media, "properties.js"));
		const style = webview.asWebviewUri(vscode.Uri.joinPath(media, "properties.css"));
		return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${style}" rel="stylesheet">
<title>${settingsTitle}</title>
</head>
<body>
<div id="root"></div>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
	}
}

function register(context, getClient, tools) {
	const model = new ProjectModel(getClient);
	const icon = vscode.Uri.joinPath(context.extensionUri, "icons", "cella.svg");
	const tree = new ProjectTree(model, icon);
	const view = vscode.window.createTreeView("cella.projects", { treeDataProvider: tree, showCollapseAll: true });
	const projectWatcher = vscode.workspace.createFileSystemWatcher("**/*.celp");
	const fileWatcher = vscode.workspace.createFileSystemWatcher("**/*", false, true, false);
	let pending;
	const refreshTree = () => {
		clearTimeout(pending);
		pending = setTimeout(() => tree.refresh(), 300);
	};

	const projectOf = item => {
		if (item instanceof vscode.Uri)
			return item.fsPath.endsWith(".celp") ? item.fsPath : tools.findProject(item.fsPath);

		if (item?.kind === "project" || item?.kind === "references")
			return item.path;

		const file = vscode.window.activeTextEditor?.document.uri.fsPath;
		return file && tools.findProject(file);
	};

	const openProject = async item => {
		const project = projectOf(item);
		return project && vscode.workspace.openTextDocument(vscode.Uri.file(project));
	};

	context.subscriptions.push(
		view,
		projectWatcher,
		fileWatcher,
		projectWatcher.onDidChange(() => model.refresh()),
		projectWatcher.onDidCreate(() => model.refresh()),
		projectWatcher.onDidDelete(() => model.refresh()),
		fileWatcher.onDidCreate(refreshTree),
		fileWatcher.onDidDelete(refreshTree),
		vscode.window.registerCustomEditorProvider("cella.projectProperties",
			new PropertiesEditor(context, model, tools.cellaCommand)),
		vscode.commands.registerCommand("cella.refreshProjects", () => model.refresh()),
		vscode.commands.registerCommand("cella.newProject", () => newProject(tools.cellaCommand)),
		vscode.commands.registerCommand("cella.editProjectFile", async item => {
			const project = projectOf(item);
			if (project)
				await vscode.commands.executeCommand("vscode.openWith", vscode.Uri.file(project), "default");
		}),
		vscode.commands.registerCommand("cella.properties", async item => {
			const project = projectOf(item);
			if (project)
				await vscode.commands.executeCommand("vscode.openWith", vscode.Uri.file(project), "cella.projectProperties");
		}),
		vscode.commands.registerCommand("cella.addReference", async item => {
			const document = await openProject(item);
			if (!document)
				return;

			try {
				const current = referencesFrom(await model.settings(document), document).filter(reference => reference.condition === null);
				const picked = await pickProjects(model, document.uri.fsPath, current, true, "References");
				if (picked && await model.edit(document, referenceEdits(current, picked.map(choice => ({
					target: choice.target,
					alias: current.find(reference => key(reference.target) === key(choice.target))?.alias
				})), null)))
					await document.save();
			} catch (error) {
				vscode.window.showErrorMessage(error.message);
			}
		}),
		vscode.commands.registerCommand("cella.removeReference", async item => {
			if (item?.kind !== "reference")
				return;

			const reference = item.reference;
			const document = await vscode.workspace.openTextDocument(vscode.Uri.parse(item.reference.ownerUri));
			const edit = { removeReference: reference.index, ...(reference.condition ? { condition: reference.condition } : {}) };
			if (await model.edit(document, [edit]))
				await document.save();
		}),
		vscode.commands.registerCommand("cella.buildProject", async item => {
			const project = projectOf(item);
			if (project)
				await vscode.tasks.executeTask(tools.buildTask(project));
		}),
		vscode.commands.registerCommand("cella.runProject", item => {
			const project = projectOf(item);
			return project && tools.startProgram(project, undefined, true);
		}),
		vscode.commands.registerCommand("cella.debugProject", item => {
			const project = projectOf(item);
			return project && tools.startProgram(project, undefined, false);
		}),
		vscode.commands.registerCommand("cella.newFile", item => newFile(item, projectOf(item)))
	);

	return model;
}

async function newProject(cellaCommand) {
	const title = "New Cella Project";
	const kind = await vscode.window.showQuickPick(["Executable", "Library"], { title, placeHolder: "Type" });
	if (!kind)
		return;

	const location = await vscode.window.showOpenDialog({
		title,
		canSelectFiles: false,
		canSelectFolders: true,
		openLabel: "Select Location",
		defaultUri: vscode.workspace.workspaceFolders?.[0]?.uri
	});

	if (!location)
		return;

	const name = (await vscode.window.showInputBox({ title, prompt: "Name" }))?.trim();
	if (!name)
		return;

	const options = await vscode.window.showQuickPick([{ label: "Add .gitignore" }], { title, canPickMany: true, placeHolder: "Options" });
	if (!options)
		return;

	const args = ["new", name, ...(kind === "Library" ? ["--lib"] : []), ...(options.length > 0 ? ["--gitignore"] : [])];
	const created = await new Promise(resolve => childProcess.execFile(cellaCommand(), args, { cwd: location[0].fsPath },
		(error, stdout, stderr) => {
			if (error)
				vscode.window.showErrorMessage((stdout || stderr).trim() || error.message);

			resolve(!error);
		}));

	if (!created)
		return;

	const folder = vscode.Uri.joinPath(location[0], name);
	if (vscode.workspace.getWorkspaceFolder(folder))
		await vscode.window.showTextDocument(vscode.Uri.joinPath(folder, kind === "Library" ? `${name}.ce` : "main.ce"));
	else
		await vscode.commands.executeCommand("vscode.openFolder", folder, { forceNewWindow: !!vscode.workspace.workspaceFolders });
}

async function newFile(item, project) {
	const folder = item?.kind === "folder" ? item.path
		: item instanceof vscode.Uri ? item.fsPath
		: project && path.dirname(project);

	if (!folder || !project)
		return;

	const name = (await vscode.window.showInputBox({ title: "New Cella File", prompt: "Name" }))?.trim()?.replace(/\.ce$/, "");
	if (!name)
		return;

	const file = vscode.Uri.file(path.join(folder, `${name}.ce`));
	const module = await moduleOf(folder, project);
	await vscode.workspace.fs.writeFile(file, Buffer.from(module ? `mod ${module}\n\n` : "", "utf8"));
	await vscode.window.showTextDocument(file);
}

async function moduleOf(folder, project) {
	for (const candidate of [folder, path.dirname(project)]) {
		let entries;
		try {
			entries = await vscode.workspace.fs.readDirectory(vscode.Uri.file(candidate));
		} catch {
			continue;
		}

		for (const [name] of entries.filter(([name]) => name.endsWith(".ce")).sort()) {
			const text = Buffer.from(await vscode.workspace.fs.readFile(vscode.Uri.file(path.join(candidate, name)))).toString("utf8");
			const line = text.split(/\r?\n/).map(line => line.trim()).find(line => line.startsWith("mod "));
			if (line)
				return line.slice(4).trim();
		}
	}

	return path.basename(project, ".celp");
}

module.exports = { register };
