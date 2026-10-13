using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class HighlightTests
{
    [Fact]
    public async Task ALocalIsWrittenAtItsDeclarationAndAssignmentsAndReadElsewhere()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local mut {|decl|}total: number = 0\n{|assign|}total = {|read|}total + 1\nprint({|use|}total)\n"));

        await session.OpenAsync("src/main.neb");
        var highlights = await session.HighlightsAsync("use");

        Assert.Equal(
            [
                (session.Location("decl"), DocumentHighlightKind.Write),
                (session.Location("assign"), DocumentHighlightKind.Write),
                (session.Location("read"), DocumentHighlightKind.Read),
                (session.Location("use"), DocumentHighlightKind.Read)
            ],
            highlights);
    }

    [Fact]
    public async Task AFieldIsHighlightedWhereverItIsReachedThroughItsClass()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    {|decl|}value: number = 0\n\n    function bump(): nil\n        self.{|write|}value = self.{|read|}value + 1\n    end\nend\n\nclass Other\n    value: number = 0\nend\n\nlocal counter = new Counter()\nprint(counter.{|use|}value)\nprint((new Other()).value)\n"));

        await session.OpenAsync("src/main.neb");
        var highlights = await session.HighlightsAsync("use");

        Assert.Equal(
            [
                (session.Location("decl"), DocumentHighlightKind.Write),
                (session.Location("write"), DocumentHighlightKind.Write),
                (session.Location("read"), DocumentHighlightKind.Read),
                (session.Location("use"), DocumentHighlightKind.Read)
            ],
            highlights);
    }
}
