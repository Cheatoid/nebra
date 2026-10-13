using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS;

/// <summary>
/// Replaces every use of a local with its initializer and removes the declaration. Offered only
/// when that cannot change behaviour: the local is never reassigned, nothing its initializer reads
/// is reassigned anywhere in the file, and an initializer that calls something is inlined into a
/// single use only, so its effects still happen exactly once.
/// </summary>
public static class InlineVariable
{
    public static CodeAction? Offer(AnalysisResult result, Position position)
    {
        var target = CursorTarget.At(result, position.Line + 1, position.Character + 1);
        if (target == null || target.IsMember) return null;

        var snapshot = result.Snapshot;
        if (!snapshot.TryGetSymbol(target.Symbol, out var symbol)) return null;
        if (!snapshot.TryGetNode(symbol.DeclaringNode, out var node, out var file) || node is not LocalDecl local) return null;
        if (!string.Equals(file, result.FilePath, StringComparison.OrdinalIgnoreCase)) return null;
        if (local.Variables.Count != 1 || local.Values.Count != 1 || IsExported(result, local)) return null;

        var declared = local.Variables[0].Name;
        var initializer = local.Values[0];
        var nodes = NodeFinder.BuildNodeRegistry(result.Hir).Values.ToList();
        var assigned = AssignedSymbols(nodes);

        var uses = NodeFinder.CollectAllNameRefs(result.Hir)
            .Where(name => name.Sym == declared.Sym && !ReferenceEquals(name, declared))
            .ToList();
        if (uses.Count == 0 || assigned.Contains(declared.Sym)) return null;
        if (uses.Count > 1 && Calls(initializer)) return null;
        if (ReadSymbols(initializer).Any(assigned.Contains)) return null;
        if (!OccupiesWholeLines(result.SourceText, local)) return null;

        var text = SourceText.Slice(result.SourceText, initializer.Span);
        var edits = uses
            .Select(use => SourceText.Replace(use.Span, NeedsParentheses(initializer, IsPrefixPosition(nodes, use)) ? $"({text})" : text))
            .Append(new TextEdit { Range = ImportEdits.WholeLines(local), NewText = string.Empty })
            .ToList();

        return new CodeAction
        {
            Title = $"Inline '{declared.Name}'",
            Kind = CodeActionKind.RefactorInline,
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [DocumentUri.Parse(result.Uri)] = edits }
            }
        };
    }

    /// <summary>
    /// Whether the initializer has to be parenthesised at a use: operators always, so precedence
    /// stays as written, and a literal or table where Lua only accepts a prefix expression.
    /// </summary>
    private static bool NeedsParentheses(Expr initializer, bool prefixPosition)
    {
        return initializer switch
        {
            NameExpr or FunctionCallExpr or MethodCallExpr or DotAccessExpr or IndexAccessExpr or ParenExpr => false,
            NumberLiteralExpr or StringLiteralExpr or BoolLiteralExpr or NilLiteralExpr or TableConstructorExpr => prefixPosition,
            _ => true
        };
    }

    private static bool IsPrefixPosition(IEnumerable<Node> nodes, NameRef use)
    {
        return nodes.Any(node => node switch
        {
            DotAccessExpr dot => dot.Object is NameExpr name && ReferenceEquals(name.Name, use),
            MethodCallExpr call => call.Object is NameExpr name && ReferenceEquals(name.Name, use),
            IndexAccessExpr index => index.Object is NameExpr name && ReferenceEquals(name.Name, use),
            FunctionCallExpr call => call.Callee is NameExpr name && ReferenceEquals(name.Name, use),
            _ => false
        });
    }

    private static bool Calls(Expr expression)
    {
        return Subtree(expression).Any(node => node is FunctionCallExpr or MethodCallExpr or NewExpr or AwaitExpr or SuperCallExpr);
    }

    private static IEnumerable<SymID> ReadSymbols(Expr expression)
    {
        return Subtree(expression).OfType<NameExpr>().Select(name => name.Name.Sym).Where(symbol => symbol != SymID.Invalid);
    }

    private static IEnumerable<Node> Subtree(Expr expression) => NodeFinder.Subtree(expression);

    private static HashSet<SymID> AssignedSymbols(IEnumerable<Node> nodes)
    {
        return nodes.OfType<AssignStmt>()
            .SelectMany(assignment => assignment.Targets)
            .OfType<NameExpr>()
            .Select(name => name.Name.Sym)
            .ToHashSet();
    }

    private static bool IsExported(AnalysisResult result, LocalDecl local)
    {
        return result.Hir.Body.OfType<ExportStmt>().Any(export => ReferenceEquals(export.Declaration, local));
    }

    private static bool OccupiesWholeLines(string text, LocalDecl local)
    {
        var lineStart = SourceText.Offset(text, local.Span.StartLn, 1);
        var start = SourceText.Offset(text, local.Span.StartLn, local.Span.StartCol);
        var end = SourceText.Offset(text, local.Span.EndLn, local.Span.EndCol);
        var lineEnd = text.IndexOf('\n', end);
        var rest = lineEnd < 0 ? text[end..] : text[end..lineEnd];
        return string.IsNullOrWhiteSpace(text[lineStart..start]) && string.IsNullOrWhiteSpace(rest);
    }
}
