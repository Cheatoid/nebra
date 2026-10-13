using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class InlayHintTests
{
    [Fact]
    public async Task AnUnannotatedLocalShowsItsInferredType()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "class Counter\n    value: number = 0\nend\n\nlocal counter{|counter|} = new Counter()\nlocal typed: Counter = counter\nprint(typed)\n"));

        await session.OpenAsync("src/main.neb");
        var hints = await session.InlayHintsAsync("src/main.neb");

        Assert.Equal([(session.Location("counter"), ": Counter")], hints);
    }

    [Fact]
    public async Task CallArgumentsShowTheParameterTheyBindTo()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function scale(value: number, factor: number): number\n    return value * factor\nend\n\nlocal factor: number = 3\nprint(scale({|first|}2, factor))\n"));

        await session.OpenAsync("src/main.neb");
        var hints = await session.InlayHintsAsync("src/main.neb");

        Assert.Equal([(session.Location("first"), "value:")], hints);
    }

    [Fact]
    public async Task MethodAndConstructorArgumentsSkipTheImplicitSelf()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    value: number = 0\n\n    constructor(start: number)\n        self.value = start\n    end\n\n    function bump(by: number): number\n        self.value = self.value + by\n        return self.value\n    end\nend\n\nlocal counter: Counter = new Counter({|start|}1)\nprint(counter:bump({|by|}2))\n"));

        await session.OpenAsync("src/main.neb");
        var hints = await session.InlayHintsAsync("src/main.neb");

        Assert.Equal([(session.Location("start"), "start:"), (session.Location("by"), "by:")], hints);
    }

    [Fact]
    public async Task TypeHintsCanBeTurnedOffInTheSettings()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function scale(value: number, factor: number): number\n    return value * factor\nend\n\nlocal result{|result|} = scale({|first|}2, 3)\nprint(result)\n"));

        await session.OpenAsync("src/main.neb");
        Assert.Contains(await session.InlayHintsAsync("src/main.neb"), hint => hint.Label == ": number");

        session.ChangeSettings(Newtonsoft.Json.Linq.JObject.Parse("{ \"inlayHints\": { \"types\": false } }"));

        var hints = await WaitFor(session, current => current.All(hint => hint.Label != ": number"));
        Assert.Contains(hints, hint => hint.Label == "value:");
    }

    /// <summary>Requests the hints until <paramref name="condition"/> holds, since settings reach the server asynchronously.</summary>
    private static async Task<IReadOnlyList<(SourceLocation Location, string Label)>> WaitFor(LspSession session,
        Func<IReadOnlyList<(SourceLocation Location, string Label)>, bool> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var hints = await session.InlayHintsAsync("src/main.neb");
            if (condition(hints)) return hints;
            await Task.Delay(100);
        }

        throw new TimeoutException("The settings never took effect.");
    }
}
