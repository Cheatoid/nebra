using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class OrganizeImportsTests
{
    private const string Library =
        "export function greet(): string\n    return \"hi\"\nend\n\n" +
        "export function farewell(): string\n    return \"bye\"\nend\n\n" +
        "export class Square\n    side: number = 1\nend\n";

    [Fact]
    public async Task UnusedNamesGoAndImportsOfOneModuleMerge()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/util.neb", "export local version: number = 1\n"),
            ("src/main.neb",
                "{|start|}import { greet, farewell } from \"lib\"\nimport { version } from \"util\"\nimport { Square } from \"lib\"\n\nlocal shape: Square = new Square()\nprint(greet(), shape)\n"));

        await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAtAsync("start");
        var organize = Assert.Single(actions, action => action.Kind == CodeActionKind.SourceOrganizeImports);

        Assert.Equal(
            "import { Square, greet } from \"lib\"\n\nlocal shape: Square = new Square()\nprint(greet(), shape)\n",
            session.Apply(organize.Edit)["src/main.neb"]);
    }

    [Fact]
    public async Task TidyImportsOfferNothing()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "{|start|}import { farewell, greet } from \"lib\"\n\nprint(greet(), farewell())\n"));

        await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAtAsync("start");

        Assert.DoesNotContain(actions, action => action.Kind == CodeActionKind.SourceOrganizeImports);
    }
}
