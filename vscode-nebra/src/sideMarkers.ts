import * as vscode from "vscode";
import { LanguageClient } from "vscode-languageclient/node";

interface SideMarker {
  position: { line: number; character: number };
  side: "client" | "server" | "shared";
  kind: "declaration" | "reference";
}

type MarkerMode = "all" | "declarations" | "off";

const sides = ["client", "server", "shared"] as const;
const themeColors: Record<(typeof sides)[number], string> = {
  client: "nebra.sideClient",
  server: "nebra.sideServer",
  shared: "nebra.sideShared",
};

const refreshDelay = 250;

function createDecoration(side: (typeof sides)[number]): vscode.TextEditorDecorationType {
  return vscode.window.createTextEditorDecorationType({
    before: {
      contentText: " ",
      backgroundColor: new vscode.ThemeColor(themeColors[side]),
      width: "0.55em",
      height: "0.75em",
      margin: "0 0.35em 0 0",
      textDecoration: "none; display: inline-block; border-radius: 2px; vertical-align: middle",
    },
  });
}

/**
 * Draws a colored box in front of every declaration and side-restricted use in Nebra editors,
 * showing where the code runs: client, server or shared. The server reports the markers through
 * the `nebra/sideMarkers` request; they are refreshed when an editor is shown or edited.
 */
export class SideMarkerDecorator implements vscode.Disposable {
  private readonly decorations = new Map(sides.map((side) => [side, createDecoration(side)]));
  private readonly pending = new Map<string, NodeJS.Timeout>();
  private readonly subscriptions: vscode.Disposable[] = [];

  constructor(private readonly client: LanguageClient) {
    this.subscriptions.push(
      vscode.window.onDidChangeVisibleTextEditors((editors) => editors.forEach((editor) => this.schedule(editor.document))),
      vscode.workspace.onDidChangeTextDocument((event) => this.schedule(event.document)),
      vscode.workspace.onDidSaveTextDocument((document) => this.schedule(document)),
      vscode.workspace.onDidChangeConfiguration((event) => {
        if (event.affectsConfiguration("nebra.sideMarkers")) this.refreshAll();
      }),
    );
    this.refreshAll();
  }

  refreshAll(): void {
    vscode.window.visibleTextEditors.forEach((editor) => this.schedule(editor.document));
  }

  dispose(): void {
    this.pending.forEach((timer) => clearTimeout(timer));
    this.subscriptions.forEach((subscription) => subscription.dispose());
    this.decorations.forEach((decoration) => decoration.dispose());
  }

  private schedule(document: vscode.TextDocument): void {
    if (document.languageId !== "nebra") return;

    const key = document.uri.toString();
    clearTimeout(this.pending.get(key));
    this.pending.set(
      key,
      setTimeout(() => {
        this.pending.delete(key);
        void this.refresh(document);
      }, refreshDelay),
    );
  }

  private async refresh(document: vscode.TextDocument): Promise<void> {
    const editors = vscode.window.visibleTextEditors.filter((editor) => editor.document === document);
    if (editors.length === 0) return;

    const markers = await this.request(document);
    for (const side of sides) {
      const ranges = markers
        .filter((marker) => marker.side === side)
        .map((marker) => new vscode.Range(marker.position.line, marker.position.character, marker.position.line, marker.position.character));
      editors.forEach((editor) => editor.setDecorations(this.decorations.get(side)!, ranges));
    }
  }

  private async request(document: vscode.TextDocument): Promise<SideMarker[]> {
    const mode = vscode.workspace.getConfiguration("nebra").get<MarkerMode>("sideMarkers", "all");
    if (mode === "off") return [];

    try {
      const markers = await this.client.sendRequest<SideMarker[]>("nebra/sideMarkers", {
        textDocument: { uri: document.uri.toString() },
      });
      return mode === "declarations" ? markers.filter((marker) => marker.kind === "declaration") : markers;
    } catch {
      return [];
    }
  }
}
