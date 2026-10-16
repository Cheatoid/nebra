import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

interface SchemaNode {
  type?: string | string[];
  description?: string;
  enum?: (string | number)[];
  enumDescriptions?: string[];
  default?: unknown;
  properties?: Record<string, SchemaNode>;
  additionalProperties?: SchemaNode | boolean;
  items?: SchemaNode;
}

const taploExtension = "tamasfe.even-better-toml";
const selector: vscode.DocumentSelector = { scheme: "file", pattern: "**/nebra.toml" };

/**
 * Completion and hover for nebra.toml, read from the schema the extension ships. Registered only
 * when Even Better TOML is not installed: that extension reads the same schema through the
 * `tomlValidation` contribution and would otherwise offer everything twice.
 */
export function registerConfigSupport(context: vscode.ExtensionContext): void {
  if (vscode.extensions.getExtension(taploExtension)) return;

  const schemaPath = path.join(context.extensionPath, "schemas", "nebra.schema.json");
  const schema = JSON.parse(fs.readFileSync(schemaPath, "utf-8")) as SchemaNode;

  context.subscriptions.push(
    vscode.languages.registerCompletionItemProvider(selector, new ConfigCompletion(schema), "[", "=", "\"", " "),
    vscode.languages.registerHoverProvider(selector, new ConfigHover(schema)),
  );
}

/** The table a line belongs to: the last `[header]` above it, or the root. */
function tableAt(document: vscode.TextDocument, line: number): string[] {
  for (let index = line; index >= 0; index--) {
    const header = /^\s*\[([^\]]+)\]/.exec(document.lineAt(index).text);
    if (header) return header[1].split(".").map((part) => part.trim().replace(/^"|"$/g, ""));
  }
  return [];
}

function nodeAt(schema: SchemaNode, keys: string[]): SchemaNode | undefined {
  let node: SchemaNode | undefined = schema;
  for (const key of keys) {
    if (!node) return undefined;
    const additional: SchemaNode | undefined = typeof node.additionalProperties === "object" ? node.additionalProperties : undefined;
    node = node.properties?.[key] ?? additional;
  }
  return node;
}

function documentation(node: SchemaNode): vscode.MarkdownString {
  const text = new vscode.MarkdownString(node.description ?? "");
  if (node.enum) text.appendMarkdown(`\n\nOne of ${node.enum.map((value) => `\`${JSON.stringify(value)}\``).join(", ")}.`);
  if (node.default !== undefined) text.appendMarkdown(`\n\nDefault: \`${JSON.stringify(node.default)}\``);
  return text;
}

function isTable(node: SchemaNode): boolean {
  return node.type === "object";
}

/** The TOML text of a value, used as what a new key is filled in with. */
function valueText(node: SchemaNode): string {
  if (node.default !== undefined) return JSON.stringify(node.default).replace(/,/g, ", ");
  if (node.enum) return JSON.stringify(node.enum[0]);
  switch (node.type) {
    case "boolean":
      return "false";
    case "array":
      return "[]";
    default:
      return '""';
  }
}

class ConfigCompletion implements vscode.CompletionItemProvider {
  constructor(private readonly schema: SchemaNode) {}

  provideCompletionItems(document: vscode.TextDocument, position: vscode.Position): vscode.CompletionItem[] {
    const before = document.lineAt(position.line).text.slice(0, position.character);

    if (/^\s*\[[\w.]*$/.test(before)) return this.tables();

    const assignment = /^\s*([\w-]+|"[^"]*")\s*=(.*)$/.exec(before);
    if (assignment) {
      const key = assignment[1].replace(/^"|"$/g, "");
      return this.values(nodeAt(this.schema, [...tableAt(document, position.line), key]), assignment[2]);
    }

    if (/^\s*[\w-]*$/.test(before)) return this.keys(document, position.line);
    return [];
  }

  private tables(): vscode.CompletionItem[] {
    return Object.entries(this.schema.properties ?? {})
      .filter(([, node]) => isTable(node))
      .map(([name, node]) => {
        const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Module);
        item.documentation = documentation(node);
        item.insertText = new vscode.SnippetString(`${name}]\n$0`);
        return item;
      });
  }

  /** The keys of the current table not written in it yet; at the root only those that are not tables. */
  private keys(document: vscode.TextDocument, line: number): vscode.CompletionItem[] {
    const table = tableAt(document, line);
    const node = nodeAt(this.schema, table);
    if (!node?.properties) return [];

    const present = this.keysWritten(document, line);
    return Object.entries(node.properties)
      .filter(([name, property]) => !present.has(name) && !(table.length === 0 && isTable(property)))
      .map(([name, property]) => {
        const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Property);
        item.documentation = documentation(property);
        item.insertText = new vscode.SnippetString(`${name} = \${1:${valueText(property).replace(/[$}\\]/g, "\\$&")}}`);
        return item;
      });
  }

  private keysWritten(document: vscode.TextDocument, line: number): Set<string> {
    const keys = new Set<string>();
    let start = line;
    while (start > 0 && !/^\s*\[/.test(document.lineAt(start - 1).text)) start--;
    let end = line;
    while (end < document.lineCount - 1 && !/^\s*\[/.test(document.lineAt(end + 1).text)) end++;

    for (let index = start; index <= end; index++) {
      const key = /^\s*([\w-]+)\s*=/.exec(document.lineAt(index).text);
      if (key && index !== line) keys.add(key[1]);
    }
    return keys;
  }

  private values(node: SchemaNode | undefined, written: string): vscode.CompletionItem[] {
    if (!node) return [];

    const insideList = written.includes("[") && !written.includes("]");
    const target = insideList && node.items ? node.items : node;
    const quoted = /"[^"]*$/.test(written);

    if (target.enum) {
      return target.enum.map((value, index) => {
        const label = typeof value === "string" ? value : String(value);
        const item = new vscode.CompletionItem(label, vscode.CompletionItemKind.EnumMember);
        item.insertText = typeof value === "string" && !quoted ? `"${value}"` : label;
        item.detail = target.enumDescriptions?.[index];
        return item;
      });
    }

    if (target.type === "boolean") {
      return ["true", "false"].map((value) => new vscode.CompletionItem(value, vscode.CompletionItemKind.Value));
    }

    return [];
  }
}

class ConfigHover implements vscode.HoverProvider {
  constructor(private readonly schema: SchemaNode) {}

  provideHover(document: vscode.TextDocument, position: vscode.Position): vscode.Hover | undefined {
    const range = document.getWordRangeAtPosition(position, /[\w-]+/);
    if (!range) return undefined;

    const text = document.lineAt(position.line).text;
    const word = document.getText(range);
    const header = /^\s*\[([^\]]+)\]/.exec(text);
    const keys = header ? header[1].split(".").map((part) => part.trim()) : [...tableAt(document, position.line), word];

    const isKey = header !== null || text.slice(0, range.start.character).trim() === "";
    const node = isKey ? nodeAt(this.schema, keys) : undefined;
    return node?.description ? new vscode.Hover(documentation(node), range) : undefined;
  }
}
