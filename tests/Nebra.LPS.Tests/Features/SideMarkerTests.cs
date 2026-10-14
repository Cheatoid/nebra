using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class SideMarkerTests
{
    private const string SidedConfig =
        "name = \"sided\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n\n[sides]\n\"src/client/**\" = [\"client\", \"shared\"]\n\"src/server/**\" = [\"server\", \"shared\"]\n\"src/shared/**\" = [\"shared\"]\n";

    private static async Task<List<(string Name, string Side, string Kind)>> Markers(LspSession session, string path)
    {
        await session.OpenAsync(path);
        var markers = await session.Client
            .SendRequest(SideMarkers.Method, new SideMarkersParams { TextDocument = new TextDocumentIdentifier(session.Uri(path)) })
            .Returning<List<SideMarker>>(CancellationToken.None);

        var lines = session.Text(path).Split('\n');
        return markers
            .Select(marker => (WordAt(lines[marker.Position.Line], marker.Position.Character), marker.Side, marker.Kind))
            .ToList();
    }

    private static string WordAt(string line, int start)
    {
        var end = start;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
        return line[start..end];
    }

    [Fact]
    public async Task DeclarationsTakeTheSideOfTheirFileUnlessTheyNameTheirOwn()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", SidedConfig),
            ("src/client/hud.neb", """
                class Hud
                    title: string = "hud"

                    function draw(): nil
                    end

                    @side(server)
                    function sync(): nil
                    end
                end

                local function show(): nil
                end

                show()
                """));

        var markers = await Markers(session, "src/client/hud.neb");

        Assert.Contains(("Hud", "client", "declaration"), markers);
        Assert.Contains(("title", "client", "declaration"), markers);
        Assert.Contains(("draw", "client", "declaration"), markers);
        Assert.Contains(("sync", "server", "declaration"), markers);
        Assert.Contains(("show", "client", "declaration"), markers);
    }

    [Fact]
    public async Task UsesOfSomethingRestrictedToASideAreMarked()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", SidedConfig),
            ("src/shared/api.neb", """
                @side(server)
                export function kick(reason: string): nil
                end

                export class Player
                    @side(client)
                    function sound(): nil
                    end

                    function name(): string
                        return "p"
                    end
                end
                """),
            ("src/server/main.neb", """
                import { kick, Player } from "shared/api"

                local player = new Player()
                kick("bye")
                print(player:name())
                """));

        var markers = await Markers(session, "src/server/main.neb");

        Assert.Contains(("kick", "server", "reference"), markers);
        Assert.DoesNotContain(markers, marker => marker is { Name: "name", Kind: "reference" });
    }

    [Fact]
    public async Task AProjectWithoutSidesHasNoMarkers()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", "local function run(): nil\nend\n\nrun()\n"));

        Assert.Empty(await Markers(session, "src/main.neb"));
    }
}
