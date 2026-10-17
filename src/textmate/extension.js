const { window, workspace } = require("vscode");
const { LanguageClient, TransportKind } = require("vscode-languageclient/node");

let client;

async function activate() {
	const command = workspace.getConfiguration("cella").get("server.path") || "cella-lsp";
	client = new LanguageClient("cella", "Cella", { command, transport: TransportKind.stdio }, {
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
		window.showErrorMessage(`Couldn't start the Cella language server '${command}': ${error.message}`);
	}
}

function deactivate() {
	return client?.stop();
}

module.exports = { activate, deactivate };
