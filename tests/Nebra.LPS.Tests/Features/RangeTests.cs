using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class RangeTests
{
    private const string Source =
        "import { a } from \"lib\"\n" +
        "import { b } from \"lib\"\n" +
        "\n" +
        "-- first comment line\n" +
        "-- second comment line\n" +
        "class Counter\n" +
        "    value: number = 0\n" +
        "\n" +
        "    function bump(by: number): number\n" +
        "        if by > 0 then\n" +
        "            self.value = self.value + by\n" +
        "        end\n" +
        "        return self.value\n" +
        "    end\n" +
        "end\n" +
        "\n" +
        "local settings = {\n" +
        "    size = 1,\n" +
        "    name = \"x\"\n" +
        "}\n" +
        "print(a, b, settings)\n";

    [Fact]
    public async Task BlocksImportsAndCommentsFold()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export local a: number = 1\nexport local b: number = 2\n"),
            ("src/main.neb", Source));

        await session.OpenAsync("src/main.neb");
        var ranges = (await session.FoldingRangesAsync("src/main.neb"))
            .Select(range => (range.StartLine, range.EndLine, range.Kind))
            .ToList();

        Assert.Contains((0, 1, (FoldingRangeKind?)FoldingRangeKind.Imports), ranges);
        Assert.Contains((3, 4, (FoldingRangeKind?)FoldingRangeKind.Comment), ranges);
        Assert.Contains((5, 13, (FoldingRangeKind?)null), ranges);
        Assert.Contains((8, 12, (FoldingRangeKind?)null), ranges);
        Assert.Contains((9, 10, (FoldingRangeKind?)null), ranges);
        Assert.Contains((16, 18, (FoldingRangeKind?)null), ranges);
    }

    [Fact]
    public async Task ASelectionGrowsFromTheNameToTheWholeFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local function add(a: number, b: number): number\n    return a + {|cursor|}b\nend\n\nprint(add(1, 2))\n"));

        await session.OpenAsync("src/main.neb");
        var chain = await session.SelectionChainAsync("cursor");

        Assert.Equal(new Position(1, 15), chain[0].Start);
        Assert.Equal(new Position(1, 16), chain[0].End);
        Assert.Contains(chain, range => range.Start == new Position(1, 11) && range.End == new Position(1, 16));
        Assert.Contains(chain, range => range.Start == new Position(0, 0) && range.End.Line == 2);
        Assert.Equal(new Position(0, 0), chain[^1].Start);

        for (var index = 1; index < chain.Count; index++)
        {
            Assert.True(Encloses(chain[index], chain[index - 1]), $"step {index} does not enclose step {index - 1}");
        }
    }

    private static bool Encloses(OmniSharp.Extensions.LanguageServer.Protocol.Models.Range outer, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range inner)
    {
        return outer.Start <= inner.Start && inner.End <= outer.End && outer != inner;
    }
}
