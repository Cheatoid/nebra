using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Marks every occurrence in the open file of the symbol or member under the cursor, telling
/// writes - its declaration and assignments to it - from reads.
/// </summary>
public sealed class DocumentHighlightHandler(NebraWorkspace workspace) : DocumentHighlightHandlerBase
{
    public override async Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var target = CursorTarget.At(result, request.Position.Line + 1, request.Position.Character + 1);
        if (target == null) return null;

        var assigned = AssignedNames(result.Hir);
        var occurrences = target.IsMember
            ? MemberOccurrences(result, target)
            : SymbolOccurrences(result, target);

        return new DocumentHighlightContainer(occurrences.Select(occurrence => new DocumentHighlight
        {
            Range = NebraWorkspace.SpanToRange(occurrence.Name.Span),
            Kind = occurrence.IsDeclaration || assigned.Contains(occurrence.Name)
                ? DocumentHighlightKind.Write
                : DocumentHighlightKind.Read
        }));
    }

    private static IEnumerable<(NameRef Name, bool IsDeclaration)> SymbolOccurrences(AnalysisResult result, CursorTarget target)
    {
        var snapshot = result.Snapshot;
        var origin = snapshot.Origin(target.Symbol);
        var declaration = DeclarationSpan(snapshot, origin);

        foreach (var nameRef in NodeFinder.CollectAllNameRefs(result.Hir))
        {
            if (nameRef.Sym == SymID.Invalid || snapshot.Origin(nameRef.Sym) != origin) continue;
            yield return (nameRef, declaration != null && SameStart(declaration, nameRef.Span));
        }
    }

    private static IEnumerable<(NameRef Name, bool IsDeclaration)> MemberOccurrences(AnalysisResult result, CursorTarget target)
    {
        foreach (var nameRef in NodeFinder.CollectAllNames(result.Hir))
        {
            if (nameRef.Sym != SymID.Invalid || nameRef.Name != target.Name.Name) continue;
            if (!ReferenceEquals(CursorTarget.MemberDeclarationOf(result, nameRef), target.MemberDeclaration)) continue;
            yield return (nameRef, ReferenceEquals(nameRef, target.MemberDeclaration));
        }
    }

    private static Diagnostics.TextSpan? DeclarationSpan(WorkspaceSnapshot snapshot, SymID symbol)
    {
        if (!snapshot.TryGetSymbol(symbol, out var declared) || declared.DeclaringNode == NodeID.Invalid) return null;
        if (!snapshot.TryGetNode(declared.DeclaringNode, out var node, out _)) return null;
        return NodeFinder.DeclaredNameSpan(node, declared.Name);
    }

    /// <summary>The names written to by an assignment anywhere in <paramref name="script"/>.</summary>
    private static HashSet<NameRef> AssignedNames(IRScript script)
    {
        var assigned = new HashSet<NameRef>(ReferenceEqualityComparer.Instance);

        foreach (var node in NodeFinder.BuildNodeRegistry(script).Values)
        {
            if (node is not AssignStmt assignment) continue;

            foreach (var assignedTarget in assignment.Targets)
            {
                switch (assignedTarget)
                {
                    case NameExpr name:
                        assigned.Add(name.Name);
                        break;
                    case DotAccessExpr dot:
                        assigned.Add(dot.FieldName);
                        break;
                }
            }
        }

        return assigned;
    }

    private static bool SameStart(Diagnostics.TextSpan left, Diagnostics.TextSpan right)
    {
        return left.StartLn == right.StartLn && left.StartCol == right.StartCol;
    }

    protected override DocumentHighlightRegistrationOptions CreateRegistrationOptions(
        DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentHighlightRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
