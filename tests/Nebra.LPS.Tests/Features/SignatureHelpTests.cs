using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class SignatureHelpTests
{
    [Fact]
    public async Task InsideACallTheActiveParameterFollowsTheCursor()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function add(a: number, b: number): number\n    return a + b\nend\n\nprint(add({|first|}1, {|second|}2))\n"));

        await session.OpenAsync("src/main.neb");
        var atFirst = await session.SignatureHelpAsync("first");
        var atSecond = await session.SignatureHelpAsync("second");

        Assert.NotNull(atFirst);
        Assert.NotNull(atSecond);
        Assert.Contains("add", atFirst.Signatures.First().Label);
        Assert.Equal(0, atFirst.ActiveParameter);
        Assert.Equal(1, atSecond.ActiveParameter);
    }

    private const string Formats = "class Format\n    constructor(pattern: string)\n        print(pattern)\n    end\nend\n\nclass Iso extends Format\n    constructor()\n        super({|super|})\n    end\nend\n\nclass Plain extends Format\nend\n\n";

    [Fact]
    public async Task InsideNewTheInheritedConstructorIsShown()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Formats + "print(new Plain({|new|}\"x\"))\n"));

        await session.OpenAsync("src/main.neb");
        var help = await session.SignatureHelpAsync("new");

        Assert.NotNull(help);
        Assert.Contains("pattern: string", help.Signatures.First().Label);
    }

    [Fact]
    public async Task InsideSuperTheBaseConstructorIsShown()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Formats + "print(new Iso())\n"));

        await session.OpenAsync("src/main.neb");
        var help = await session.SignatureHelpAsync("super");

        Assert.NotNull(help);
        Assert.Contains("super(pattern: string)", help.Signatures.First().Label);
    }
}
