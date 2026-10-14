using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class ReflectionTests
{
    private static string Config(string mode) =>
        $"name = \"app\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n\n[reflection]\nmode = \"{mode}\"\n";

    private const string Types = """
        class Player
        end

        @reflectable
        class Item
        end

        """;

    [Fact]
    public async Task IdsArePassedToReflectionWithCompletion()
    {
        await using var session = await LspSession.StartAsync(("nebra.toml", Config("all")), ("src/main.neb", Types + "print(reflect.get(\"{|id|}\"))\n"));
        await session.OpenAsync("src/main.neb");

        var labels = await session.CompletionLabelsAsync("id");

        Assert.Contains("app::Player", labels);
        Assert.Contains("app::Item", labels);
    }

    [Fact]
    public async Task AnIdThatNamesNothingIsReported()
    {
        await using var session = await LspSession.StartAsync(("nebra.toml", Config("all")), ("src/main.neb", Types + "print(reflect.get(\"app::Plyer\"), reflect.get(\"app::Player\"))\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        var warning = Assert.Single(diagnostics);
        Assert.Equal("WarnUnknownReflectionId", warning.Code?.String);
        Assert.Contains("app::Plyer", warning.Message);
    }

    [Fact]
    public async Task OnlyMarkedDeclarationsCountWhenReflectionIsAnnotated()
    {
        await using var session = await LspSession.StartAsync(("nebra.toml", Config("annotated")), ("src/main.neb", Types + "print(reflect.Of(Player), reflect.Of(Item), reflect.get(\"{|id|}\"))\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        var warning = Assert.Single(diagnostics);
        Assert.Equal("WarnNotReflected", warning.Code?.String);
        Assert.Contains("Player", warning.Message);

        var labels = await session.CompletionLabelsAsync("id");
        Assert.Contains("app::Item", labels);
        Assert.DoesNotContain("app::Player", labels);
    }

    [Fact]
    public async Task UsingReflectionWhileItIsOffIsReported()
    {
        await using var session = await LspSession.StartAsync(("nebra.toml", Config("none")), ("src/main.neb", "print(reflect.classes())\n"));

        var diagnostics = await session.OpenAsync("src/main.neb");

        Assert.Equal("WarnReflectionDisabled", Assert.Single(diagnostics).Code?.String);
    }

    [Fact]
    public async Task AVariableNamedReflectIsNotTheLibrary()
    {
        await using var session = await LspSession.StartAsync(("nebra.toml", Config("none")), ("src/main.neb", "local function lookup(): string\n    local reflect = { get = function(id: string): string return id end }\n    return reflect.get(\"x\")\nend\n\nprint(lookup())\n"));

        Assert.Empty(await session.OpenAsync("src/main.neb"));
    }

    [Fact]
    public async Task HoverOverAnIdShowsWhatItNames()
    {
        await using var session = await LspSession.StartAsync(("nebra.toml", Config("all")), ("src/main.neb", Types + "print(reflect.get({|id|}\"app::Player\"))\n"));
        await session.OpenAsync("src/main.neb");

        var hover = await session.HoverAsync("id");

        Assert.Contains("class Player", hover);
        Assert.Contains("Registered as `app::Player`", hover);
    }
}
