using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class WorkspaceSymbolTests
{
    private static Task<LspSession> StartProject()
    {
        return LspSession.StartAsync(
            ("src/shapes.neb",
                "export class {|square|}Square\n    side: number = 2\n\n    function {|area|}area(): number\n        return self.side * self.side\n    end\nend\n"),
            ("src/colors.neb", "export enum Color\n    {|red|}Red\n    Green\nend\n"),
            ("src/main.neb", "local function {|helper|}squareHelper(): number\n    return 4\nend\n\nprint(squareHelper())\n"));
    }

    [Fact]
    public async Task FindsDeclarationsInFilesThatWereNeverOpened()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var symbols = await session.WorkspaceSymbolsAsync("area");

        var area = Assert.Single(symbols);
        Assert.Equal(SymbolKind.Method, area.Kind);
        Assert.Equal("Square", area.Container);
        Assert.Equal(session.Location("area"), area.Location);
    }

    [Fact]
    public async Task ExactMatchesRankBeforePrefixMatches()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var symbols = await session.WorkspaceSymbolsAsync("square");

        Assert.Equal(["Square", "squareHelper"], symbols.Select(symbol => symbol.Name).ToList());
        Assert.Equal(session.Location("square"), symbols[0].Location);
    }

    [Fact]
    public async Task QueryCharactersMayBeScattered()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var symbols = await session.WorkspaceSymbolsAsync("sqhlp");

        Assert.Equal(session.Location("helper"), Assert.Single(symbols).Location);
    }

    [Fact]
    public async Task EnumMembersAreListedUnderTheirEnum()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var red = Assert.Single(await session.WorkspaceSymbolsAsync("Red"));

        Assert.Equal(SymbolKind.EnumMember, red.Kind);
        Assert.Equal("Color", red.Container);
    }
}
