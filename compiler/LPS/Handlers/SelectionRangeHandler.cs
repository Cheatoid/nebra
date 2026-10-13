using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Expands a selection step by step through the regions around the cursor: the name, the
/// expression, the statement, the enclosing member and declaration, up to the whole file.
/// </summary>
public sealed class SelectionRangeHandler(NebraWorkspace workspace) : SelectionRangeHandlerBase
{
    public override async Task<Container<SelectionRange>?> Handle(SelectionRangeParams request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var regions = SourceRegions.Collect(result.Hir).Select(region => region.Span).ToList();
        return new Container<SelectionRange>(request.Positions.Select(position => Chain(result, regions, position)));
    }

    private static SelectionRange Chain(AnalysisResult result, List<Diagnostics.TextSpan> regions, Position position)
    {
        var line = position.Line + 1;
        var col = position.Character + 1;

        var enclosing = regions.Where(span => SourceRegions.Contains(span, line, col)).ToList();
        var name = NodeFinder.FindNameRef(result.Hir, line, col);
        if (name != null) enclosing.Add(name.Span);

        var ranges = enclosing
            .Select(NebraWorkspace.SpanToRange)
            .DistinctBy(range => (range.Start.Line, range.Start.Character, range.End.Line, range.End.Character))
            .OrderByDescending(range => (range.End.Line - range.Start.Line, range.End.Character - range.Start.Character))
            .ToList();

        var lines = result.SourceText.Split('\n');
        SelectionRange? chain = new SelectionRange
        {
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, lines.Length - 1, lines[^1].Length)
        };

        foreach (var range in ranges)
        {
            chain = new SelectionRange { Range = range, Parent = chain };
        }

        return chain;
    }

    protected override SelectionRangeRegistrationOptions CreateRegistrationOptions(
        SelectionRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SelectionRangeRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
