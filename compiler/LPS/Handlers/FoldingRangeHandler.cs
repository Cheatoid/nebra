using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Folds every block that spans lines - declarations, members, control flow, function literals,
/// table constructors - leaving the closing line visible, plus runs of imports and comments.
/// </summary>
public sealed class FoldingRangeHandler(NebraWorkspace workspace) : FoldingRangeHandlerBase
{
    public override async Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request, CancellationToken ct)
    {
        var result = await workspace.GetResultAsync(request.TextDocument.Uri.ToString(), ct);
        if (result == null) return null;

        var ranges = new Dictionary<(int, int), FoldingRange>();

        foreach (var (node, span) in SourceRegions.Collect(result.Hir))
        {
            if (node is ExprStmt or AssignStmt or LocalDecl or ReturnStmt or ExportStmt or IRScript or ImportStmt) continue;
            Add(ranges, span.StartLn - 1, span.EndLn - 2, null);
        }

        foreach (var run in Runs(result.Hir.Body.OfType<ImportStmt>().Select(import => import.Span.StartLn - 1)))
        {
            Add(ranges, run.Start, run.End, FoldingRangeKind.Imports);
        }

        AddComments(result, ranges);

        return new Container<FoldingRange>(ranges.Values.OrderBy(range => range.StartLine));
    }

    private static void AddComments(AnalysisResult result, Dictionary<(int, int), FoldingRange> ranges)
    {
        var lineComments = new List<int>();

        foreach (var token in result.TokenStream.GetTokens())
        {
            if (token.Type == NebraLexer.LONG_COMMENT)
            {
                var lines = token.Text.Count(character => character == '\n');
                Add(ranges, token.Line - 1, token.Line - 1 + lines, FoldingRangeKind.Comment);
            }
            else if (token.Type == NebraLexer.LINE_COMMENT)
            {
                lineComments.Add(token.Line - 1);
            }
        }

        foreach (var run in Runs(lineComments))
        {
            Add(ranges, run.Start, run.End, FoldingRangeKind.Comment);
        }
    }

    /// <summary>Groups line numbers into runs of consecutive lines, keeping runs of two or more.</summary>
    private static IEnumerable<(int Start, int End)> Runs(IEnumerable<int> lines)
    {
        var ordered = lines.Distinct().Order().ToList();
        var index = 0;

        while (index < ordered.Count)
        {
            var end = index;
            while (end + 1 < ordered.Count && ordered[end + 1] == ordered[end] + 1) end++;
            if (end > index) yield return (ordered[index], ordered[end]);
            index = end + 1;
        }
    }

    private static void Add(Dictionary<(int, int), FoldingRange> ranges, int startLine, int endLine, FoldingRangeKind? kind)
    {
        if (endLine <= startLine) return;

        ranges.TryAdd((startLine, endLine), new FoldingRange { StartLine = startLine, EndLine = endLine, Kind = kind });
    }

    protected override FoldingRangeRegistrationOptions CreateRegistrationOptions(
        FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("nebra")
        };
    }
}
