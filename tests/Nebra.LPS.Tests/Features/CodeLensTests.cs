using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class CodeLensTests
{
    [Fact]
    public async Task EveryTestAndSuiteCanBeRunWithTheFilterSelectingIt()
    {
        await using var session = await LspSession.StartAsync(("src/math_test.neb", """
            import { describe, it as check, expect } from "nebra:test"

            describe("math", function(): nil
                check("adds", function(): nil
                    expect(1 + 1):toBe(2)
                end)
            end)
            """));
        await session.OpenAsync("src/math_test.neb");

        var lenses = await Lenses(session, "src/math_test.neb");

        Assert.Equal(
            [("Run tests in file", 0, null), ("Run suite", 2, "math"), ("Run test", 3, "math > adds")],
            lenses.Select(lens => (lens.Command!.Title, lens.Range.Start.Line, (string?)lens.Command.Arguments![1])));
        Assert.All(lenses, lens => Assert.Equal(session.Root, (string?)lens.Command!.Arguments![2]));
    }

    [Fact]
    public async Task TestsCalledThroughANamespaceImportGetLensesToo()
    {
        await using var session = await LspSession.StartAsync(("src/text_test.neb", """
            import * as t from "nebra:test"

            t.test("trims", function(): nil
                t.expect(1):toBe(1)
            end)
            """));
        await session.OpenAsync("src/text_test.neb");

        var lenses = await Lenses(session, "src/text_test.neb");

        Assert.Contains(lenses, lens => lens.Command!.Title == "Run test" && (string?)lens.Command.Arguments![1] == "trims");
    }

    [Fact]
    public async Task FilesWithoutTheTestFrameworkHaveNoLenses()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", "local function test(name: string): nil\nend\n\ntest(\"x\")\n"));
        await session.OpenAsync("src/main.neb");

        Assert.Empty(await Lenses(session, "src/main.neb"));
    }

    private static async Task<List<CodeLens>> Lenses(LspSession session, string path)
    {
        var lenses = await session.Client.TextDocument.RequestCodeLens(new CodeLensParams { TextDocument = session.Uri(path) });
        return lenses?.ToList() ?? [];
    }
}
