using System.Text;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Moves the selected statements into a new local function placed before the top-level
/// statement that contains them. Locals from around the selection become parameters, and a local
/// declared in it and still needed afterwards is returned. Offered only when the move keeps the
/// behaviour: the selection is a run of whole statements in one block, it does not leave its
/// surroundings through <c>return</c>, <c>break</c>, <c>continue</c> or <c>goto</c>, does not use
/// <c>self</c>, <c>...</c> or <c>await</c>, does not reassign a variable it would receive as a
/// parameter, and leaves at most one local behind for the code after it.
/// </summary>
public static class ExtractFunction
{
    private const string IndentUnit = "    ";

    public static CodeAction? Offer(NebraWorkspace workspace, AnalysisResult result, Range selection)
    {
        var text = result.SourceText;
        var start = SourceText.Offset(text, selection.Start.Line + 1, selection.Start.Character + 1);
        var end = SourceText.Offset(text, selection.End.Line + 1, selection.End.Character + 1);
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start >= end) return null;

        var selected = SelectedStatements(result, start, end);
        if (selected == null || !IsSelfContained(selected)) return null;

        var insertion = result.Hir.Body.Contains(selected[0])
            ? selected[0]
            : result.Hir.Body.FirstOrDefault(statement => ExtractVariable.Encloses(statement.Span, selected[0].Span));
        if (insertion == null) return null;

        var insertionOffset = SourceText.Offset(text, insertion.Span.StartLn, 1);
        var (parameters, declaredInside) = Classify(result, selected, insertionOffset, start, end);
        if (parameters == null) return null;

        var outputs = declaredInside
            .Where(symbol => NodeFinder.CollectAllNames(result.Hir).Any(name => name.Sym == symbol.Id && Start(text, name) >= end))
            .ToList();
        if (outputs.Count > 1) return null;

        var name = ExtractVariable.UniqueName(result, "extracted");
        var output = outputs.FirstOrDefault();
        var function = BuildFunction(workspace, result, selected, insertion, name, parameters, output);
        var call = $"{name}({string.Join(", ", parameters.Select(parameter => parameter.Name))})";
        var callLine = SourceText.Indent(text, selected[0].Span.StartLn - 1)
            + (output == null ? call : $"local {(output.IsMutable ? "mut " : "")}{output.Name} = {call}") + "\n";

        var selectedLines = new Range(selected[0].Span.StartLn - 1, 0, selected[^1].Span.EndLn, 0);
        var edits = ReferenceEquals(insertion, selected[0])
            ? new List<TextEdit> { new() { Range = selectedLines, NewText = function + callLine } }
            : [SourceText.Insert(new Position(insertion.Span.StartLn - 1, 0), function), new TextEdit { Range = selectedLines, NewText = callLine }];

        return new CodeAction
        {
            Title = "Extract to a local function",
            Kind = CodeActionKind.RefactorExtract,
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [DocumentUri.Parse(result.Uri)] = edits }
            }
        };
    }

    private sealed record Variable(SymID Id, string Name, TypID Type, bool IsMutable);

    /// <summary>The run of whole statements of one block that the selection covers exactly, or null.</summary>
    private static List<Stmt>? SelectedStatements(AnalysisResult result, int start, int end)
    {
        var text = result.SourceText;
        foreach (var list in StatementLists.All(result.Hir))
        {
            var first = list.FindIndex(statement => Start(text, statement) == start);
            var last = list.FindIndex(statement => End(text, statement) == end);
            if (first >= 0 && last >= first) return list.GetRange(first, last - first + 1);
        }

        return null;
    }

    /// <summary>Whether the statements can run as a function of their own without changing where control goes.</summary>
    private static bool IsSelfContained(List<Stmt> selected)
    {
        var nodes = selected.SelectMany(NodeFinder.Subtree).ToList();
        var nestedFunctions = nodes.Where(node => node is FunctionDefExpr or LocalFunctionDecl or FunctionDecl).Select(node => node.Span).ToList();
        bool InNestedFunction(Node node) => nestedFunctions.Any(span => !ReferenceEquals(span, node.Span) && ExtractVariable.Encloses(span, node.Span));

        var leaves = nodes.Any(node => node is ReturnStmt or BreakStmt or ContinueStmt or GotoStmt or LabelStmt && !InNestedFunction(node));
        var forbidden = nodes.Any(node => node is VarargExpr or AwaitExpr || node is NameExpr { Name.Name: "self" });
        return !leaves && !forbidden;
    }

    /// <summary>
    /// Splits the variables the selection uses into those it needs passed in - declared between
    /// the insertion point and the selection - and those it declares itself. Returns no
    /// parameters when the selection reassigns one of them.
    /// </summary>
    private static (List<Variable>? Parameters, List<Variable> DeclaredInside) Classify(AnalysisResult result, List<Stmt> selected,
        int insertionOffset, int start, int end)
    {
        var text = result.SourceText;
        var snapshot = result.Snapshot;
        var parameters = new List<Variable>();
        var declaredInside = new List<Variable>();

        foreach (var name in selected.SelectMany(NodeFinder.CollectNames))
        {
            if (name.Sym == SymID.Invalid || parameters.Concat(declaredInside).Any(known => known.Id == name.Sym)) continue;
            if (!snapshot.TryGetSymbol(name.Sym, out var symbol) || !snapshot.TryGetNode(symbol.DeclaringNode, out var node, out var file)) continue;
            if (!string.Equals(file, result.FilePath, StringComparison.OrdinalIgnoreCase)) continue;

            var declaration = NodeFinder.DeclaredNameSpan(node, symbol.Name);
            var declarationOffset = SourceText.Offset(text, declaration.StartLn, declaration.StartCol);
            var variable = new Variable(symbol.ID, symbol.Name, symbol.Type, node is LocalDecl { IsMutable: true });

            if (declarationOffset >= start && declarationOffset < end) declaredInside.Add(variable);
            else if (declarationOffset >= insertionOffset && declarationOffset < start) parameters.Add(variable);
        }

        var reassigned = selected.SelectMany(NodeFinder.Subtree).OfType<AssignStmt>()
            .SelectMany(assignment => assignment.Targets).OfType<NameExpr>()
            .Any(target => parameters.Any(parameter => parameter.Id == target.Name.Sym));

        return (reassigned ? null : parameters, declaredInside);
    }

    private static string BuildFunction(NebraWorkspace workspace, AnalysisResult result, List<Stmt> selected, Stmt insertion,
        string name, List<Variable> parameters, Variable? output)
    {
        var text = result.SourceText;
        var indent = SourceText.Indent(text, insertion.Span.StartLn - 1);
        var signature = string.Join(", ", parameters.Select(parameter => $"{parameter.Name}: {workspace.FormatType(result.Types, parameter.Type)}"));
        var returns = output == null ? string.Empty : ": " + workspace.FormatType(result.Types, output.Type);

        var builder = new StringBuilder();
        builder.Append($"{indent}local function {name}({signature}){returns}\n");
        foreach (var line in Reindented(text, selected, indent + IndentUnit)) builder.Append(line).Append('\n');
        if (output != null) builder.Append($"{indent}{IndentUnit}return {output.Name}\n");
        builder.Append($"{indent}end\n\n");
        return builder.ToString();
    }

    /// <summary>The lines of the selected statements, with their common indentation replaced by <paramref name="indent"/>.</summary>
    private static IEnumerable<string> Reindented(string text, List<Stmt> selected, string indent)
    {
        var lines = text.Split('\n')[(selected[0].Span.StartLn - 1)..selected[^1].Span.EndLn].Select(line => line.TrimEnd('\r')).ToList();
        var common = lines.Where(line => line.Trim().Length > 0).Select(line => line.Length - line.TrimStart().Length).DefaultIfEmpty(0).Min();
        return lines.Select(line => line.Trim().Length == 0 ? string.Empty : indent + line[common..]);
    }

    private static int Start(string text, Node node) => SourceText.Offset(text, node.Span.StartLn, node.Span.StartCol);

    private static int Start(string text, NameRef name) => SourceText.Offset(text, name.Span.StartLn, name.Span.StartCol);

    private static int End(string text, Node node) => SourceText.Offset(text, node.Span.EndLn, node.Span.EndCol);
}
