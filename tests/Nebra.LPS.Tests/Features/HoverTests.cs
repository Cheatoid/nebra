using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class HoverTests
{
    [Fact]
    public async Task HoverOverALocalShowsItsType()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local answer: number = 42\nprint({|use|}answer)\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("use");

        Assert.NotNull(hover);
        Assert.Contains("number", hover);
    }

    [Fact]
    public async Task HoverOverAFunctionCallShowsItsSignature()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function add(a: number, b: number): number\n    return a + b\nend\n\nprint({|call|}add(1, 2))\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("call");

        Assert.NotNull(hover);
        Assert.Contains("add", hover);
        Assert.Contains("a: number", hover);
    }

    [Fact]
    public async Task HoverOverAMethodShowsItsSignature()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "class Counter\n    value: number = 0\n\n    function bump(by: number): number\n        self.value = self.value + by\n        return self.value\n    end\nend\n\nlocal counter = new Counter()\nprint(counter:{|method|}bump(2))\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("method");

        Assert.NotNull(hover);
        Assert.Contains("bump", hover);
        Assert.Contains("by: number", hover);
    }

    [Fact]
    public async Task HoverOverAnImportedFunctionShowsItsSignature()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint({|call|}greet(\"ada\"))\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("call");

        Assert.NotNull(hover);
        Assert.Contains("name: string", hover);
    }

    [Fact]
    public async Task HoverSeesUnsavedChangesInAnImportedFile()
    {
        await using var session = await LspSession.StartAsync(
            ("src/lib.neb", "export function greet(name: string): string\n    return \"hi \" .. name\nend\n"),
            ("src/main.neb", "import { greet } from \"lib\"\n\nprint({|call|}greet(\"ada\"))\n"));

        await session.OpenAsync("src/lib.neb");
        await session.OpenAsync("src/main.neb");
        session.SendChange("src/lib.neb", "export function greet(name: string, punctuation: string = \"!\"): string\n    return \"hi \" .. name .. punctuation\nend\n", 2);

        var hover = await session.HoverAsync("call");

        Assert.NotNull(hover);
        Assert.Contains("punctuation", hover);
    }

    [Fact]
    public async Task HoverOverAnEnumMemberShowsItsValue()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "enum Test\n    Krass\n    Mega = 5\n    Ultra\nend\n\nprint(Test.{|first|}Krass, Test.{|last|}Ultra)\n"));

        await session.OpenAsync("src/main.neb");

        Assert.Contains("Test.Krass = 0", await session.HoverAsync("first"));
        Assert.Contains("Test.Ultra = 6", await session.HoverAsync("last"));
    }

    [Fact]
    public async Task HoverOverAnEnumListsItsMembersWithTheirValues()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "enum State\n    Active = \"ACTIVE\"\n    Idle = \"IDLE\"\nend\n\nprint({|enum|}State.Active)\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("enum");

        Assert.Contains("Active = \"ACTIVE\"", hover);
        Assert.Contains("Idle = \"IDLE\"", hover);
    }

    [Fact]
    public async Task HoverOverAClassListsItsPublicMembersAndItsDocumentation()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", """
                --- A player in the game.
                class Player
                    health: number = 100
                    protected secret: string = "x"

                    function heal(amount: number): number
                        return self.health + amount
                    end
                end

                local player = new {|class|}Player()
                print({|local|}player)
                """));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("class");

        Assert.Contains("health: number", hover);
        Assert.Contains("function heal(amount: number): number", hover);
        Assert.DoesNotContain("secret", hover);
        Assert.DoesNotContain("self", hover);
        Assert.Contains("A player in the game.", hover);
        Assert.Contains("local player: Player", await session.HoverAsync("local"));
    }

    [Fact]
    public async Task HoverOverAMemberShowsItsDocumentationWhereItIsUsedAndDeclared()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", """
                class Player
                    --- Heals the player.
                    --- @param amount number how much to heal
                    function {|declared|}heal(amount: number): number
                        return amount
                    end
                end

                local player = new Player()
                print(player:{|used|}heal(5))
                """));

        await session.OpenAsync("src/main.neb");

        foreach (var marker in new[] { "declared", "used" })
        {
            var hover = await session.HoverAsync(marker);
            Assert.Contains("function Player:heal(amount: number): number", hover);
            Assert.Contains("Heals the player.", hover);
            Assert.Contains("how much to heal", hover);
        }
    }

    [Fact]
    public async Task HoverOverALibraryFunctionShowsItsDocumentationAndSide()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", "name = \"lps-test\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\nglobals = [\"types\"]\n"),
            ("types/api.d.neb", "--- Kicks a player from the server.\n@side(server)\ndeclare function kick(reason: string): nil\n"),
            ("src/main.neb", "{|call|}kick(\"bye\")\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("call");

        Assert.Contains("declare function kick(reason: string): nil", hover);
        Assert.Contains("runs on the **server**", hover);
        Assert.Contains("Kicks a player from the server.", hover);
    }

    [Fact]
    public async Task HoverTellsFunctionMembersFromMethods()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "print(string.{|function|}upper(\"x\"))\n"));

        await session.OpenAsync("src/main.neb");

        var hover = await session.HoverAsync("function");

        Assert.Contains("function StringLib.upper(s: string): string", hover);
        Assert.Contains("Returns a copy with every lowercase letter turned uppercase.", hover);
    }

    [Fact]
    public async Task HoverShowsTheDocumentationOfStandardLibraryMethods()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local file = io.open(\"data.txt\")\nif file ~= nil then\n    print(file:{|method|}read(\"a\"))\nend\n"));

        await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("method");

        Assert.Contains("method IoFile:read(...: any): any", hover);
        Assert.Contains("Reads from the file following formats", hover);
    }
}
