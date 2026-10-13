using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Searches every declaration in the workspace by name. A query matches fuzzily, as in gopls:
/// its characters have to appear in order, and exact names rank before prefixes, prefixes before
/// substrings, and substrings before scattered matches.
/// </summary>
public sealed class WorkspaceSymbolHandler(NebraWorkspace workspace) : WorkspaceSymbolsHandlerBase
{
    private const int MaxResults = 200;

    public override async Task<Container<WorkspaceSymbol>?> Handle(WorkspaceSymbolParams request, CancellationToken ct)
    {
        var query = request.Query ?? string.Empty;
        var matches = new List<(int Rank, WorkspaceSymbol Symbol)>();
        var results = new List<AnalysisResult>();
        foreach (var project in workspace.AllProjects())
        {
            results.AddRange((await project.GetSnapshotAsync(ct)).SourceResults());
        }

        foreach (var result in results)
        {
            var uri = DocumentUri.Parse(result.Uri);
            foreach (var (symbol, container) in Flatten(SymbolCollector.Collect(result), null))
            {
                var rank = Rank(symbol.Name, query);
                if (rank < 0) continue;

                matches.Add((rank, new WorkspaceSymbol
                {
                    Name = symbol.Name,
                    Kind = symbol.Kind,
                    ContainerName = container,
                    Location = new Location { Uri = uri, Range = symbol.SelectionRange }
                }));
            }
        }

        var ordered = matches
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.Symbol.Name.Length)
            .ThenBy(match => match.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(match => match.Symbol);

        return new Container<WorkspaceSymbol>(ordered);
    }

    private static IEnumerable<(DocumentSymbol Symbol, string? Container)> Flatten(IEnumerable<DocumentSymbol> symbols, string? container)
    {
        foreach (var symbol in symbols)
        {
            yield return (symbol, container);

            if (symbol.Children == null) continue;

            foreach (var child in Flatten(symbol.Children, symbol.Name))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Ranks how well <paramref name="name"/> matches <paramref name="query"/>, lower being better,
    /// or returns -1 when it does not match. An empty query matches everything.
    /// </summary>
    public static int Rank(string name, string query)
    {
        if (query.Length == 0) return 3;
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return IsSubsequence(name, query) ? 3 : -1;
    }

    private static bool IsSubsequence(string name, string query)
    {
        var position = 0;
        foreach (var character in query)
        {
            position = name.IndexOf(character.ToString(), position, StringComparison.OrdinalIgnoreCase);
            if (position < 0) return false;
            position++;
        }

        return true;
    }

    protected override WorkspaceSymbolRegistrationOptions CreateRegistrationOptions(
        WorkspaceSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new WorkspaceSymbolRegistrationOptions();
    }
}
