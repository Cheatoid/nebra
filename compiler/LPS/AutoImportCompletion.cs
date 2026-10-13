using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using NebraSymbolKind = Nebra.IR.SymbolKind;

namespace Nebra.LPS;

/// <summary>
/// Offers what other project files export but the current file has not imported yet. Accepting
/// an item writes the import along with the name. The exports come from the latest workspace
/// snapshot, which may be a few edits old, so completion never waits for the whole workspace.
/// </summary>
public static class AutoImportCompletion
{
    public static IEnumerable<CompletionItem> Items(AnalysisResult result, WorkspaceSnapshot? exporters, Position position,
        bool typeContext, IReadOnlySet<string> visible)
    {
        if (exporters == null) yield break;

        var prefix = WordBefore(result.SourceText, position);
        if (prefix.Length == 0) yield break;

        var sourceRoot = ImportEdits.SourceRoot(result);
        foreach (var exporter in exporters.SourceResults())
        {
            if (string.Equals(exporter.FilePath, result.FilePath, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var (name, export) in NebraWorkspace.CollectExports(exporter))
            {
                if (visible.Contains(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (typeContext && !IsTypeKind(export.SymKind)) continue;

                var edit = ImportEdits.AddImport(result, name, exporter.FilePath);
                if (edit == null) continue;

                var module = ImportEdits.ModuleSpecifier(result.FilePath, exporter.FilePath, sourceRoot);
                yield return new CompletionItem
                {
                    Label = name,
                    Kind = KindOf(export.SymKind),
                    Detail = $"import from \"{module}\"",
                    LabelDetails = new CompletionItemLabelDetails { Description = module },
                    SortText = "~" + name,
                    AdditionalTextEdits = new TextEditContainer(edit)
                };
            }
        }
    }

    public static CompletionItemKind KindOf(NebraSymbolKind kind)
    {
        return kind switch
        {
            NebraSymbolKind.Function => CompletionItemKind.Function,
            NebraSymbolKind.Enum => CompletionItemKind.Enum,
            NebraSymbolKind.Class => CompletionItemKind.Class,
            NebraSymbolKind.Interface => CompletionItemKind.Interface,
            NebraSymbolKind.TypeParam => CompletionItemKind.TypeParameter,
            _ => CompletionItemKind.Variable
        };
    }

    private static bool IsTypeKind(NebraSymbolKind kind)
    {
        return kind is NebraSymbolKind.Class or NebraSymbolKind.Interface or NebraSymbolKind.Enum or NebraSymbolKind.TypeParam;
    }

    /// <summary>The identifier characters directly before <paramref name="position"/>.</summary>
    private static string WordBefore(string text, Position position)
    {
        var lines = text.Split('\n');
        if (position.Line >= lines.Length) return string.Empty;

        var line = lines[position.Line];
        var end = Math.Min(position.Character, line.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_')) start--;

        return line[start..end];
    }
}
