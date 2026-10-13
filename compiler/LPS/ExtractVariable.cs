using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Moves the selected expression into a local declared right before the statement that uses it.
/// Offered only where evaluating the expression earlier cannot change what the program does: not
/// in loop or <c>elseif</c> conditions, which run repeatedly or conditionally, nor in defaults and
/// deferred blocks, and not on a line the statement shares with code before it.
/// </summary>
public static class ExtractVariable
{
    public static CodeAction? Offer(AnalysisResult result, Range selection)
    {
        var text = result.SourceText;
        var (start, end) = Trimmed(text, selection);
        if (start >= end) return null;

        var nodes = NodeFinder.BuildNodeRegistry(result.Hir).Values.ToList();
        var expression = nodes.OfType<Expr>().FirstOrDefault(candidate => Covers(text, candidate.Span, start, end));
        if (expression is null or NameExpr or VarargExpr || IsAssignmentTarget(nodes, expression)) return null;

        var statement = nodes.OfType<Stmt>()
            .Where(candidate => Encloses(candidate.Span, expression.Span))
            .OrderBy(candidate => candidate.Span.EndLn - candidate.Span.StartLn)
            .ThenBy(candidate => candidate.Span.EndCol - candidate.Span.StartCol)
            .FirstOrDefault();
        if (statement == null || !CanHoist(statement, expression) || !StartsItsLine(text, statement)) return null;

        var name = UniqueName(result, "value");
        var line = statement.Span.StartLn - 1;
        var declaration = $"{SourceText.Indent(text, line)}local {name} = {SourceText.Slice(text, expression.Span)}\n";

        return new CodeAction
        {
            Title = "Extract to a local variable",
            Kind = CodeActionKind.RefactorExtract,
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [DocumentUri.Parse(result.Uri)] =
                    [
                        SourceText.Insert(new Position(line, 0), declaration),
                        SourceText.Replace(expression.Span, name)
                    ]
                }
            }
        };
    }

    /// <summary>A name built from <paramref name="stem"/> that no name in the file uses yet.</summary>
    public static string UniqueName(AnalysisResult result, string stem)
    {
        var taken = NodeFinder.CollectAllNames(result.Hir).Select(name => name.Name).ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(stem)) return stem;

        var suffix = 2;
        while (taken.Contains(stem + suffix)) suffix++;
        return stem + suffix;
    }

    private static bool CanHoist(Stmt statement, Expr expression)
    {
        return statement switch
        {
            ExprStmt or LocalDecl or ReturnStmt or NumericForStmt or GenericForStmt => true,
            AssignStmt assignment => assignment.Values.Any(value => Encloses(value.Span, expression.Span)),
            IfStmt branch => Encloses(branch.Condition.Span, expression.Span),
            _ => false
        };
    }

    private static bool IsAssignmentTarget(IEnumerable<Node> nodes, Expr expression)
    {
        return nodes.OfType<AssignStmt>().Any(assignment => assignment.Targets.Contains(expression));
    }

    private static bool StartsItsLine(string text, Stmt statement)
    {
        var lineStart = SourceText.Offset(text, statement.Span.StartLn, 1);
        var start = SourceText.Offset(text, statement.Span.StartLn, statement.Span.StartCol);
        return string.IsNullOrWhiteSpace(text[lineStart..start]);
    }

    private static (int Start, int End) Trimmed(string text, Range selection)
    {
        var start = SourceText.Offset(text, selection.Start.Line + 1, selection.Start.Character + 1);
        var end = SourceText.Offset(text, selection.End.Line + 1, selection.End.Character + 1);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }

    private static bool Covers(string text, Diagnostics.TextSpan span, int start, int end)
    {
        return SourceText.Offset(text, span.StartLn, span.StartCol) == start && SourceText.Offset(text, span.EndLn, span.EndCol) == end;
    }

    public static bool Encloses(Diagnostics.TextSpan outer, Diagnostics.TextSpan inner)
    {
        var startsBefore = outer.StartLn < inner.StartLn || (outer.StartLn == inner.StartLn && outer.StartCol <= inner.StartCol);
        var endsAfter = outer.EndLn > inner.EndLn || (outer.EndLn == inner.EndLn && outer.EndCol >= inner.EndCol);
        return startsBefore && endsAfter;
    }
}
