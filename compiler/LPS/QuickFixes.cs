using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Diagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;
using DiagnosticCode = Nebra.Diagnostics.DiagnosticCode;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Fixes for diagnostics whose correction is unambiguous: a missing <c>mut</c>, <c>override</c>
/// or <c>abstract</c>, a misplaced one, a <c>.</c> that has to be a <c>:</c>, a possibly-nil
/// value, a name another module does not export, an annotation that disagrees with its value.
/// </summary>
public static class QuickFixes
{
    public static IEnumerable<CodeAction> For(AnalysisResult result, WorkspaceSnapshot? workspace, Diagnostic diagnostic)
    {
        return diagnostic.Code?.String switch
        {
            nameof(DiagnosticCode.ErrAssignToImmutable) => MakeMutable(result, diagnostic),
            nameof(DiagnosticCode.WarnMissingShadowOverride) => AddOverride(result, diagnostic),
            nameof(DiagnosticCode.ErrOverrideNoParent) => RemoveOverride(result, diagnostic),
            nameof(DiagnosticCode.ErrAbstractInNonAbstractClass) => MakeClassAbstract(result, diagnostic),
            nameof(DiagnosticCode.ErrInstanceMethodNeedsColon) => UseColon(result, diagnostic),
            nameof(DiagnosticCode.ErrInterfaceMethodNeedsColon) => UseColon(result, diagnostic),
            nameof(DiagnosticCode.ErrPossiblyNil) => GuardNil(result, diagnostic),
            nameof(DiagnosticCode.ErrSymbolNotExported) => ExportDeclaration(result, workspace, diagnostic),
            nameof(DiagnosticCode.ErrTypeMismatch) => ChangeAnnotation(result, diagnostic),
            _ => []
        };
    }

    private static IEnumerable<CodeAction> MakeMutable(AnalysisResult result, Diagnostic diagnostic)
    {
        var name = NodeFinder.FindNameRef(result.Hir, diagnostic.Range.Start.Line + 1, diagnostic.Range.Start.Character + 1);
        if (name == null || !result.Snapshot.TryGetSymbol(name.Sym, out var symbol)) yield break;
        if (!result.Snapshot.TryGetNode(symbol.DeclaringNode, out var node, out var file) || node is not LocalDecl { IsMutable: false } local) yield break;
        if (!string.Equals(file, result.FilePath, StringComparison.OrdinalIgnoreCase)) yield break;

        var keyword = SourceText.FindWord(result.SourceText, local.Span, "local");
        if (keyword == null) yield break;

        yield return Fix($"Make '{symbol.Name}' mutable", result, diagnostic,
            SourceText.Insert(new Position(keyword.Line, keyword.Character + "local".Length), " mut"));
    }

    private static IEnumerable<CodeAction> AddOverride(AnalysisResult result, Diagnostic diagnostic)
    {
        var method = MemberAt(result, diagnostic);
        var keyword = method == null ? null : SourceText.FindWord(result.SourceText, method.Value.Span, "function");
        if (keyword == null) yield break;

        yield return Fix("Mark the method 'override'", result, diagnostic, SourceText.Insert(keyword, "override "));
    }

    private static IEnumerable<CodeAction> RemoveOverride(AnalysisResult result, Diagnostic diagnostic)
    {
        var member = MemberAt(result, diagnostic);
        var keyword = member == null ? null : SourceText.FindWord(result.SourceText, member.Value.Span, "override");
        if (keyword == null) yield break;

        yield return Fix("Remove 'override'", result, diagnostic, new TextEdit
        {
            Range = new Range(keyword, new Position(keyword.Line, keyword.Character + "override ".Length)),
            NewText = string.Empty
        });
    }

    private static IEnumerable<CodeAction> MakeClassAbstract(AnalysisResult result, Diagnostic diagnostic)
    {
        var member = MemberAt(result, diagnostic);
        var keyword = member == null ? null : SourceText.FindWord(result.SourceText, member.Value.Owner.Span, "class");
        if (keyword == null) yield break;

        yield return Fix($"Make '{member!.Value.Owner.Name.Name}' abstract", result, diagnostic, SourceText.Insert(keyword, "abstract "));
    }

    private static IEnumerable<CodeAction> UseColon(AnalysisResult result, Diagnostic diagnostic)
    {
        var access = Nodes(result).OfType<DotAccessExpr>().FirstOrDefault(dot => StartsAt(dot.Span, diagnostic.Range.Start));
        if (access == null) yield break;

        var dot = new Position(access.FieldName.Span.StartLn - 1, access.FieldName.Span.StartCol - 2);
        yield return Fix("Call the method with ':'", result, diagnostic, new TextEdit
        {
            Range = new Range(dot, new Position(dot.Line, dot.Character + 1)),
            NewText = ":"
        });
    }

    /// <summary>
    /// Offers optional access before a <c>.</c>, and a non-nil assertion. The assertion is an
    /// expression rather than a prefix, so it has to be parenthesised before a member access.
    /// </summary>
    private static IEnumerable<CodeAction> GuardNil(AnalysisResult result, Diagnostic diagnostic)
    {
        var end = diagnostic.Range.End;
        var offset = SourceText.Offset(result.SourceText, end.Line + 1, end.Character + 1);
        var next = offset < result.SourceText.Length ? result.SourceText[offset] : '\0';

        if (next == '.')
        {
            yield return Fix("Access it with '?.'", result, diagnostic, SourceText.Insert(end, "?"));
        }

        if (next is '.' or ':' or '[')
        {
            yield return Fix("Assert that it is not nil with '!'", result, diagnostic, new TextEdit
            {
                Range = diagnostic.Range,
                NewText = "(" + Text(result.SourceText, diagnostic.Range) + "!)"
            });
            yield break;
        }

        yield return Fix("Assert that it is not nil with '!'", result, diagnostic, SourceText.Insert(end, "!"));
    }

    private static string Text(string source, Range range)
    {
        var start = SourceText.Offset(source, range.Start.Line + 1, range.Start.Character + 1);
        var end = SourceText.Offset(source, range.End.Line + 1, range.End.Character + 1);
        return source[start..end];
    }

    private static IEnumerable<CodeAction> ExportDeclaration(AnalysisResult result, WorkspaceSnapshot? workspace, Diagnostic diagnostic)
    {
        var import = result.Hir.Body.OfType<ImportStmt>().FirstOrDefault(statement => SourceText.Contains(statement.Span, diagnostic.Range.Start));
        var specifier = import?.Specifiers.FirstOrDefault(candidate => SourceText.Contains(candidate.Span, diagnostic.Range.Start));
        if (import == null || specifier == null || workspace == null) yield break;

        var target = ImportEdits.Resolve(result.FilePath, import.Module.Name, ImportEdits.SourceRoot(result));
        var module = target == null ? null : workspace.GetResult(target);
        var declaration = module?.Hir.Body.FirstOrDefault(statement => Declares(statement, specifier.Name.Name));
        if (module == null || declaration == null) yield break;

        yield return new CodeAction
        {
            Title = $"Export '{specifier.Name.Name}' from '{import.Module.Name}'",
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            IsPreferred = true,
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [DocumentUri.Parse(module.Uri)] = [SourceText.Insert(new Position(declaration.Span.StartLn - 1, declaration.Span.StartCol - 1), "export ")]
                }
            }
        };
    }

    private static IEnumerable<CodeAction> ChangeAnnotation(AnalysisResult result, Diagnostic diagnostic)
    {
        var actual = QuotedValues(diagnostic.Message).ElementAtOrDefault(1);
        if (actual == null) yield break;

        var variable = Nodes(result).OfType<LocalDecl>()
            .SelectMany(local => local.Variables)
            .FirstOrDefault(candidate => candidate.TypeAnnotation != null && StartsAt(candidate.Span, diagnostic.Range.Start));
        if (variable == null) yield break;

        yield return Fix($"Change the annotation to '{actual}'", result, diagnostic,
            SourceText.Replace(variable.TypeAnnotation!.Span, actual));
    }

    private static bool Declares(Stmt statement, string name)
    {
        return statement switch
        {
            LocalDecl local => local.Variables.Any(variable => variable.Name.Name == name),
            LocalFunctionDecl function => function.Name.Name == name,
            FunctionDecl { NamePath.Count: 1, MethodName: null } function => function.NamePath[0].Name == name,
            ClassDecl classDecl => classDecl.Name.Name == name,
            InterfaceDecl interfaceDecl => interfaceDecl.Name.Name == name,
            EnumDecl enumDecl => enumDecl.Name.Name == name,
            _ => false
        };
    }

    /// <summary>The class member a diagnostic is reported on, with the class declaring it.</summary>
    private static (TextSpan Span, ClassDecl Owner)? MemberAt(AnalysisResult result, Diagnostic diagnostic)
    {
        foreach (var classDecl in Nodes(result).OfType<ClassDecl>())
        {
            foreach (var method in classDecl.Methods)
                if (StartsAt(method.Span, diagnostic.Range.Start)) return (method.Span, classDecl);

            foreach (var accessor in classDecl.Accessors)
                if (StartsAt(accessor.Span, diagnostic.Range.Start)) return (accessor.Span, classDecl);
        }

        return null;
    }

    private static IEnumerable<Node> Nodes(AnalysisResult result) => NodeFinder.BuildNodeRegistry(result.Hir).Values;

    private static bool StartsAt(TextSpan span, Position position)
    {
        return span.StartLn - 1 == position.Line && span.StartCol - 1 == position.Character;
    }

    private static IEnumerable<string> QuotedValues(string message)
    {
        var parts = message.Split('\'');
        for (var index = 1; index < parts.Length; index += 2)
        {
            yield return parts[index];
        }
    }

    private static CodeAction Fix(string title, AnalysisResult result, Diagnostic diagnostic, TextEdit edit)
    {
        return new CodeAction
        {
            Title = title,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [DocumentUri.Parse(result.Uri)] = [edit]
                }
            }
        };
    }
}
