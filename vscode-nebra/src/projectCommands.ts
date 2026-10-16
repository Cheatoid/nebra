import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

const configName = "nebra.toml";

interface ProjectCommand {
  id: string;
  args: string[];
}

const projectCommands: ProjectCommand[] = [
  { id: "nebra.build", args: ["build"] },
  { id: "nebra.run", args: ["run"] },
  { id: "nebra.test", args: ["test"] },
  { id: "nebra.watch", args: ["watch"] },
  { id: "nebra.install", args: ["install"] },
  { id: "nebra.compile", args: ["compile"] },
  { id: "nebra.docs", args: ["docs"] },
];

/**
 * Runs `nebra <args>` as a task in `cwd`, so its output appears in the terminal panel and it can
 * be stopped or rerun like any other task.
 */
export async function runNebra(executable: string, args: string[], cwd: string, label: string): Promise<void> {
  const scope = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(cwd)) ?? vscode.TaskScope.Workspace;
  const task = new vscode.Task(
    { type: "nebra", task: args[0] },
    scope,
    label,
    "nebra",
    new vscode.ProcessExecution(executable, args, { cwd }),
  );
  task.presentationOptions = { reveal: vscode.TaskRevealKind.Always, clear: true };
  await vscode.tasks.executeTask(task);
}

/**
 * The directory of the nebra.toml nearest to the active file, searching upward from it. Without
 * an active file inside a project, every nebra.toml of the workspace is offered to choose from.
 */
async function findProject(): Promise<string | undefined> {
  const active = vscode.window.activeTextEditor?.document.uri;
  if (active?.scheme === "file") {
    const nearest = nearestConfigDirectory(path.dirname(active.fsPath));
    if (nearest) return nearest;
  }

  const configs = await vscode.workspace.findFiles(`**/${configName}`, "**/{nebra_modules,node_modules}/**");
  if (configs.length === 0) {
    vscode.window.showErrorMessage(`No ${configName} found in the workspace.`);
    return undefined;
  }
  if (configs.length === 1) return path.dirname(configs[0].fsPath);

  const picked = await vscode.window.showQuickPick(
    configs.map((config) => ({ label: vscode.workspace.asRelativePath(config), directory: path.dirname(config.fsPath) })),
    { placeHolder: "Choose the Nebra project" },
  );
  return picked?.directory;
}

function nearestConfigDirectory(start: string): string | undefined {
  let directory = start;
  while (true) {
    if (fs.existsSync(path.join(directory, configName))) return directory;
    const parent = path.dirname(directory);
    if (parent === directory) return undefined;
    directory = parent;
  }
}

function projectLabel(directory: string, args: string[]): string {
  return `${args.join(" ")} (${path.basename(directory)})`;
}

export function registerProjectCommands(context: vscode.ExtensionContext, executable: string): void {
  for (const command of projectCommands) {
    context.subscriptions.push(
      vscode.commands.registerCommand(command.id, async () => {
        const directory = await findProject();
        if (directory) await runNebra(executable, command.args, directory, projectLabel(directory, command.args));
      }),
    );
  }

  context.subscriptions.push(
    vscode.commands.registerCommand("nebra.runCommand", async () => {
      const directory = await findProject();
      if (!directory) return;

      const input = await vscode.window.showInputBox({
        prompt: `Run nebra in ${path.basename(directory)}`,
        placeHolder: "add github:owner/repo@v1",
      });
      const args = input?.trim().split(/\s+/).filter((arg) => arg.length > 0);
      if (args && args.length > 0) await runNebra(executable, args, directory, projectLabel(directory, args));
    }),
    vscode.commands.registerCommand("nebra.openConfig", async () => {
      const directory = await findProject();
      if (!directory) return;

      const document = await vscode.workspace.openTextDocument(path.join(directory, configName));
      await vscode.window.showTextDocument(document);
    }),
  );
}
