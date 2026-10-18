const { window, workspace } = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

async function activate() {
	const configured = workspace.getConfiguration("cella").get("server.path");
	const options = { cwd: workspace.workspaceFolders?.[0]?.uri.fsPath };
	const server = configured
		? { command: configured, transport: TransportKind.stdio, options }
		: { command: "cella", args: ["lsp"], transport: TransportKind.stdio, options };

	client = new LanguageClient("cella", "Cella", server, {
		documentSelector: [
			{ scheme: "file", language: "cella" },
			{ scheme: "file", language: "cella-project" }
		],
		synchronize: {
			fileEvents: workspace.createFileSystemWatcher("**/*.{ce,celp}")
		}
	});

	try {
		await client.start();
	} catch (error) {
		window.showErrorMessage(`Couldn't start the Cella language server '${server.command}': ${error.message}`);
	}
}

function deactivate() {
	return client?.stop();
}

module.exports = { activate, deactivate };
