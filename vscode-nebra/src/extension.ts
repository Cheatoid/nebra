import { execFileSync } from "child_process";
import * as vscode from "vscode";
import { registerConfigSupport } from "./configSupport";
import { registerProjectCommands, runNebra } from "./projectCommands";
import { SideMarkerDecorator } from "./sideMarkers";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";

let client: LanguageClient | undefined;

function resolveCommand(name: string): string {
  if (process.platform !== "win32") return name;
  try {
    const result = execFileSync("where", [name], { encoding: "utf-8" });
    const first = result.split(/\r?\n/).find((l) => l.trim().length > 0);
    if (first) return first.trim();
  } catch {
    // fall through
  }
  return name;
}

async function runTests(executable: string, uri: string, filter: string | null, root: string): Promise<void> {
  const file = vscode.Uri.parse(uri).fsPath;
  const args = filter ? ["test", file, filter] : ["test", file];
  await runNebra(executable, args, root, filter ?? "tests");
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const config = vscode.workspace.getConfiguration("nebra");
  const configured = config.get<string>("serverPath") || "";
  const serverPath = configured || resolveCommand("nebra") || resolveCommand("Nebra");

  const serverOptions: ServerOptions = {
    command: serverPath,
    args: ["lps"],
    transport: TransportKind.stdio,
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [{ scheme: "file", language: "nebra" }],
    initializationOptions: vscode.workspace.getConfiguration("nebra"),
    synchronize: {
      configurationSection: "nebra",
      fileEvents: vscode.workspace.createFileSystemWatcher("**/{*.neb,nebra.toml}"),
    },
    outputChannelName: "Nebra Language Server",
  };

  client = new LanguageClient("nebra", "Nebra Language Server", serverOptions, clientOptions);

  registerConfigSupport(context);
  registerProjectCommands(context, serverPath);

  context.subscriptions.push(
    vscode.commands.registerCommand("nebra.compileFile", async (uri: string | undefined) => {
      const targetUri = uri ?? vscode.window.activeTextEditor?.document.uri.toString();
      if (!targetUri) {
        vscode.window.showErrorMessage("No file selected to compile.");
        return;
      }
      if (!client) return;
      await client.sendRequest("workspace/executeCommand", {
        command: "nebra.compileFile",
        arguments: [targetUri],
      });
    }),
    vscode.commands.registerCommand("nebra.runTest", (uri: string, filter: string | null, root: string) =>
      runTests(serverPath, uri, filter, root),
    )
  );

  context.subscriptions.push(
    vscode.commands.registerCommand("nebra.restartServer", async () => {
      if (!client) return;
      await client.restart();
      vscode.window.setStatusBarMessage("Nebra language server restarted", 3000);
    }),
  );

  await client.start();
  context.subscriptions.push(new SideMarkerDecorator(client));
}

export async function deactivate(): Promise<void> {
  if (client) {
    await client.stop();
    client = undefined;
  }
}
