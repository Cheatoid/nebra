using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class CodeActionTests
{
    [Fact]
    public async Task MissingInterfaceMembersOfferAnImplementQuickFix()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "interface Named\n    function name(): string\nend\n\nclass Person implements Named\nend\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Contains(actions, action =>
            action.Kind == CodeActionKind.QuickFix && action.Title.Contains("Implement missing members"));
    }

    [Fact]
    public async Task AnUnresolvedNameOffersAnImportQuickFix()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(): string\n    return \"hi\"\nend\n"),
            ("src/main.neb", "print(greet())\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);

        Assert.Contains(actions, action =>
            action.Kind == CodeActionKind.QuickFix && action.Title.Contains("Import 'greet'"));
    }
}

public sealed class ImportFixTests
{
    [Fact]
    public async Task AnImportFixExtendsAnImportOfTheSameModule()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(): string\n    return \"hi\"\nend\n\nexport function farewell(): string\n    return \"bye\"\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint(greet(), farewell())\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);
        var fix = Assert.Single(actions, action => action.Title == "Import 'farewell' from 'lib'");

        Assert.Equal(
            "import { farewell, greet } from \"lib\"\n\nprint(greet(), farewell())\n",
            session.Apply(fix.Edit)["src/main.neb"]);
    }

    [Fact]
    public async Task AnImportFixReachesASubdirectoryThroughTheSourceRoot()
    {
        await using var session = await LspSession.StartAsync(
            ("src/shapes/square.neb", "export function area(side: number): number\n    return side * side\nend\n"),
            ("src/main.neb", "-- entry point\nprint(area(2))\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAsync("src/main.neb", diagnostics);
        var fix = Assert.Single(actions, action => action.Title.StartsWith("Import 'area'"));

        Assert.Equal("Import 'area' from 'shapes/square'", fix.Title);
        Assert.Equal(
            "-- entry point\nimport { area } from \"shapes/square\"\nprint(area(2))\n",
            session.Apply(fix.Edit)["src/main.neb"]);
    }
}
