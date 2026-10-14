using System.Text.RegularExpressions;
using Nebra.Compiler;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Nebra.LPS;

/// <summary>
/// What the language server tells about runtime reflection: which ids exist for completing and
/// explaining the strings passed to <c>reflect.get</c>, <c>reflect.ref</c> and
/// <c>reflect.implementorsOf</c>, and what <c>reflect.Of(X)</c> resolves to.
/// </summary>
public static partial class ReflectionInfo
{
    public static readonly string[] IdMethods = ["get", "ref", "implementorsOf"];

    [GeneratedRegex("""reflect\s*\.\s*(get|ref|implementorsOf)\s*\(\s*"([^"]*)$""")]
    private static partial Regex OpenIdArgument();

    /// <summary>
    /// The registry of the whole project: the latest workspace build's, since a build of the open
    /// document alone only knows the declarations of that one file.
    /// </summary>
    public static ReflectionRegistry Registry(NebraWorkspace workspace, AnalysisResult result)
    {
        return result.Snapshot.IsWorkspace ? result.Snapshot.Reflection : workspace.LatestSnapshotFor(result.FilePath)?.Reflection ?? result.Snapshot.Reflection;
    }

    /// <summary>
    /// The method and the id typed so far when the cursor is inside the string argument of
    /// <c>reflect.get</c>, <c>reflect.ref</c> or <c>reflect.implementorsOf</c>.
    /// </summary>
    public static (string Method, string Typed)? IdArgumentAt(string lineBeforeCursor)
    {
        var match = OpenIdArgument().Match(lineBeforeCursor);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : null;
    }

    /// <summary>The entries an id argument of <paramref name="method"/> may name; <c>implementorsOf</c> takes interfaces only.</summary>
    public static IEnumerable<ReflectionEntry> Candidates(ReflectionRegistry registry, string method)
    {
        return registry.Entries.Where(entry => entry.Recorded && (method != "implementorsOf" || entry.Kind == "interface"));
    }

    /// <summary>The registry entry a call of <c>reflect</c> refers to, when the cursor is on its id or on <c>Of</c>.</summary>
    public static ReflectionEntry? EntryAt(ReflectionRegistry registry, AnalysisResult result, int line, int col)
    {
        if (NodeFinder.FindEnclosingCall(result.Hir, line, col) is not FunctionCallExpr
            {
                Callee: DotAccessExpr { Object: NameExpr { Name.Name: "reflect" }, FieldName: var method }
            } call)
        {
            return null;
        }

        if (IdMethods.Contains(method.Name) && call.Arguments is [StringLiteralExpr id, ..] && Contains(id, line, col))
        {
            return registry.Find(id.Value);
        }

        if (method.Name == "Of" && call.Arguments is [NameExpr subject] && Contains(method, line, col)
            && result.Syms.GetByID(subject.Name.Sym, out var symbol) && result.Types.GetByID(symbol.Type, out var type)
            && type.ReflectionId != null)
        {
            return registry.Find(type.ReflectionId);
        }

        return null;
    }

    public static string Describe(ReflectionEntry entry)
    {
        var text = $"```nebra\n{entry.Kind} {entry.Name}\n```\n\nRegistered as `{entry.Id}`";
        if (entry.File != null && Path.IsPathRooted(entry.File))
        {
            var span = entry.Declaration.Span;
            text += $" · declared in [{Path.GetFileName(entry.File)}]({DocumentUri.FromFileSystemPath(entry.File)}#L{span.StartLn},{span.StartCol})";
        }

        if (!entry.Recorded)
        {
            text += "\n\nNot recorded: the [reflection] mode is \"annotated\" and it is not marked `@reflectable`, so `reflect` finds nothing under this id.";
        }

        return text;
    }

    private static bool Contains(Node node, int line, int col) => Contains(node.Span, line, col);

    private static bool Contains(NameRef name, int line, int col) => Contains(name.Span, line, col);

    private static bool Contains(Diagnostics.TextSpan span, int line, int col)
    {
        return span.StartLn == line && span.StartCol <= col && col <= span.EndCol;
    }
}
