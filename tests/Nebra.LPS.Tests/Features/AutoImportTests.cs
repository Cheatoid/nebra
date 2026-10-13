using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class AutoImportTests
{
    private const string Library =
        "export function greet(): string\n    return \"hi\"\nend\n\n" +
        "export function farewell(): string\n    return \"bye\"\nend\n\n" +
        "export class Square\n    side: number = 1\nend\n";

    [Fact]
    public async Task AnUnimportedExportIsOfferedWithItsImport()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint(greet(), far{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var items = await session.CompletionItemsAsync("cursor");

        var farewell = Assert.Single(items, item => item.Label == "farewell");
        Assert.Equal("lib", farewell.LabelDetails?.Description);

        var edit = new WorkspaceEdit
        {
            Changes = new Dictionary<OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri, IEnumerable<TextEdit>>
            {
                [session.Uri("src/main.neb")] = farewell.AdditionalTextEdits!
            }
        };
        Assert.StartsWith("import { farewell, greet } from \"lib\"\n", session.Apply(edit)["src/main.neb"]);
    }

    [Fact]
    public async Task AnAlreadyImportedNameIsNotOfferedAgain()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint(gre{|cursor|})\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var items = await session.CompletionItemsAsync("cursor");

        var greet = Assert.Single(items, item => item.Label == "greet");
        Assert.Null(greet.AdditionalTextEdits);
    }

    [Fact]
    public async Task ATypeAnnotationIsOfferedOnlyTypes()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", Library),
            ("src/main.neb", "local shape: S{|cursor|}\n"));

        await session.OpenAsync("src/main.neb");
        await session.PublishedDiagnostics("src/lib.neb");
        var labels = (await session.CompletionItemsAsync("cursor")).Where(item => item.AdditionalTextEdits != null).Select(item => item.Label).ToList();

        Assert.Equal(["Square"], labels);
    }
}
