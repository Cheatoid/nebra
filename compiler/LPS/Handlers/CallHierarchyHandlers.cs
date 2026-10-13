using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace Nebra.LPS.Handlers;

/// <summary>Turns callables into call hierarchy items and finds them again from an item.</summary>
internal static class CallHierarchyItems
{
    public static CallHierarchyItem From(Callable callable)
    {
        return new CallHierarchyItem
        {
            Name = callable.Name.Name,
            Kind = callable.Kind,
            Detail = callable.Container,
            Uri = DocumentUri.Parse(callable.File.Uri),
            Range = NebraWorkspace.SpanToRange(callable.Span),
            SelectionRange = NebraWorkspace.SpanToRange(callable.Name.Span)
        };
    }

    /// <summary>The caller item for calls made at the top level of a file, outside any function.</summary>
    public static CallHierarchyItem ForFile(AnalysisResult file)
    {
        var start = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 0);
        return new CallHierarchyItem
        {
            Name = Path.GetFileName(file.FilePath),
            Kind = LspSymbolKind.File,
            Uri = DocumentUri.Parse(file.Uri),
            Range = start,
            SelectionRange = start
        };
    }

    public static Callable? Find(CallGraph graph, CallHierarchyItem item)
    {
        var path = item.Uri.GetFileSystemPath();
        return path == null
            ? null
            : graph.FindAt(Path.GetFullPath(path), item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
    }

    public static CallHierarchyRegistrationOptions Options()
    {
        return new CallHierarchyRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}

public sealed class CallHierarchyPrepareHandler(NebraWorkspace workspace) : CallHierarchyPrepareHandlerBase
{
    public override async Task<Container<CallHierarchyItem>?> Handle(CallHierarchyPrepareParams request, CancellationToken ct)
    {
        var result = await workspace.GetWorkspaceResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var target = CursorTarget.At(result, request.Position.Line + 1, request.Position.Character + 1);
        if (target == null) return null;

        var graph = CallGraph.Build(result.Snapshot);
        var declaration = target.IsMember ? target.MemberDeclaration : DeclarationOf(result, target);
        var callable = declaration == null ? null : graph.Find(declaration);

        return callable == null ? null : new Container<CallHierarchyItem>(CallHierarchyItems.From(callable));
    }

    private static IR.NameRef? DeclarationOf(AnalysisResult result, CursorTarget target)
    {
        var snapshot = result.Snapshot;
        if (!snapshot.TryGetSymbol(snapshot.Origin(target.Symbol), out var symbol)) return null;
        return snapshot.TryGetNode(symbol.DeclaringNode, out var node, out _) ? NodeFinder.DeclaredName(node, symbol.Name) : null;
    }

    protected override CallHierarchyRegistrationOptions CreateRegistrationOptions(
        CallHierarchyCapability capability, ClientCapabilities clientCapabilities) => CallHierarchyItems.Options();
}

/// <summary>Who calls a function: every caller, with the ranges of its calls.</summary>
public sealed class CallHierarchyIncomingHandler(NebraWorkspace workspace) : CallHierarchyIncomingHandlerBase
{
    public override async Task<Container<CallHierarchyIncomingCall>?> Handle(CallHierarchyIncomingCallsParams request, CancellationToken ct)
    {
        var snapshot = await workspace.GetSnapshotAsync(request.Item.Uri.ToString(), ct);
        var graph = CallGraph.Build(snapshot);
        var callee = CallHierarchyItems.Find(graph, request.Item);
        if (callee == null) return null;

        var incoming = graph.Calls
            .Where(call => ReferenceEquals(call.Callee, callee.Name))
            .GroupBy(call => (object?)call.Caller ?? call.File)
            .Select(group => new CallHierarchyIncomingCall
            {
                From = group.First().Caller is { } caller ? CallHierarchyItems.From(caller) : CallHierarchyItems.ForFile(group.First().File),
                FromRanges = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>(
                    group.Select(call => NebraWorkspace.SpanToRange(call.Span)))
            });

        return new Container<CallHierarchyIncomingCall>(incoming);
    }

}

/// <summary>What a function calls: every callee reached from its body, with the ranges of the calls.</summary>
public sealed class CallHierarchyOutgoingHandler(NebraWorkspace workspace) : CallHierarchyOutgoingHandlerBase
{
    public override async Task<Container<CallHierarchyOutgoingCall>?> Handle(CallHierarchyOutgoingCallsParams request, CancellationToken ct)
    {
        var snapshot = await workspace.GetSnapshotAsync(request.Item.Uri.ToString(), ct);
        var graph = CallGraph.Build(snapshot);
        var caller = CallHierarchyItems.Find(graph, request.Item);
        if (caller == null) return null;

        var outgoing = graph.Calls
            .Where(call => ReferenceEquals(call.Caller, caller))
            .Select(call => (Call: call, Callee: graph.Find(call.Callee)))
            .Where(entry => entry.Callee != null)
            .GroupBy(entry => entry.Callee!)
            .Select(group => new CallHierarchyOutgoingCall
            {
                To = CallHierarchyItems.From(group.Key),
                FromRanges = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>(
                    group.Select(entry => NebraWorkspace.SpanToRange(entry.Call.Span)))
            });

        return new Container<CallHierarchyOutgoingCall>(outgoing);
    }

}
