using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class RefactoringTests
{
    /// <summary>
    /// Applies the action titled <paramref name="title"/> offered for the range between two
    /// markers, sends the result back and requires it to compile without errors.
    /// </summary>
    private static async Task<string> Refactor(LspSession session, string title, string start, string? end = null)
    {
        await session.OpenAsync("src/main.neb");
        var actions = await session.CodeActionsAtAsync(start, end);
        var action = Assert.Single(actions, candidate => candidate.Title == title);
        var text = session.Apply(action.Edit)["src/main.neb"];

        var after = await session.ChangeAsync("src/main.neb", text, 2);
        Assert.DoesNotContain(after, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return text;
    }

    private static async Task<IReadOnlyList<string>> Titles(LspSession session, string start, string? end = null)
    {
        await session.OpenAsync("src/main.neb");
        return (await session.CodeActionsAtAsync(start, end)).Select(action => action.Title).ToList();
    }

    [Fact]
    public async Task ExtractVariableHoistsTheExpressionBeforeItsStatement()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local a: number = 1\nlocal b: number = 2\nif a > 0 then\n    print(a + {|start|}b * 2{|end|})\nend\n"));

        var text = await Refactor(session, "Extract to a local variable", "start", "end");

        Assert.Equal("local a: number = 1\nlocal b: number = 2\nif a > 0 then\n    local value = b * 2\n    print(a + value)\nend\n", text);
    }

    [Fact]
    public async Task ExtractVariableIsNotOfferedInALoopCondition()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local mut n: number = 0\nwhile {|start|}n + 1{|end|} < 10 do\n    n = n + 1\nend\n"));

        Assert.DoesNotContain("Extract to a local variable", await Titles(session, "start", "end"));
    }

    [Fact]
    public async Task ExtractVariablePicksAFreeName()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local value: number = 1\nprint({|start|}value + 1{|end|})\n"));

        var text = await Refactor(session, "Extract to a local variable", "start", "end");

        Assert.Equal("local value: number = 1\nlocal value2 = value + 1\nprint(value2)\n", text);
    }

    [Fact]
    public async Task InlineReplacesEveryUseAndKeepsPrecedence()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local a: number = 3\nlocal {|cursor|}shifted = a + 1\nprint(shifted * 2, shifted)\n"));

        var text = await Refactor(session, "Inline 'shifted'", "cursor");

        Assert.Equal("local a: number = 3\nprint((a + 1) * 2, (a + 1))\n", text);
    }

    [Fact]
    public async Task InlineIsNotOfferedWhenACallWouldRunTwice()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local function roll(): number\n    return 4\nend\n\nlocal {|cursor|}dice = roll()\nprint(dice, dice)\n"));

        Assert.DoesNotContain("Inline 'dice'", await Titles(session, "cursor"));
    }

    [Fact]
    public async Task InlineIsNotOfferedForAReassignedLocal()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local mut {|cursor|}count = 1\ncount = 2\nprint(count)\n"));

        Assert.DoesNotContain("Inline 'count'", await Titles(session, "cursor"));
    }

    [Fact]
    public async Task ExtractFunctionPassesLocalsInAndReturnsWhatIsUsedAfterwards()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function compute(base: number): number\n    local doubled = base * 2\n    {|start|}local total = doubled + 1\n    print(total){|end|}\n    return total\nend\n\nprint(compute(2))\n"));

        var text = await Refactor(session, "Extract to a local function", "start", "end");

        Assert.Equal(
            "local function extracted(doubled: number): number\n    local total = doubled + 1\n    print(total)\n    return total\nend\n\n" +
            "local function compute(base: number): number\n    local doubled = base * 2\n    local total = extracted(doubled)\n    return total\nend\n\nprint(compute(2))\n",
            text);
    }

    [Fact]
    public async Task ExtractFunctionAtTheTopLevelGoesRightBeforeTheSelection()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "local greeting = \"hi\"\n{|start|}print(greeting)\nprint(greeting .. \"!\"){|end|}\n"));

        var text = await Refactor(session, "Extract to a local function", "start", "end");

        Assert.Equal(
            "local greeting = \"hi\"\nlocal function extracted()\n    print(greeting)\n    print(greeting .. \"!\")\nend\n\nextracted()\n",
            text);
    }

    [Fact]
    public async Task ExtractFunctionIsNotOfferedWhenTheSelectionReturns()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function check(value: number): string\n    {|start|}if value > 0 then\n        return \"positive\"\n    end{|end|}\n    return \"other\"\nend\n\nprint(check(1))\n"));

        Assert.DoesNotContain("Extract to a local function", await Titles(session, "start", "end"));
    }

    [Fact]
    public async Task ExtractFunctionIsNotOfferedWhenItWouldReassignAParameter()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb",
                "local function count(): number\n    local mut total = 0\n    {|start|}total = total + 1{|end|}\n    return total\nend\n\nprint(count())\n"));

        Assert.DoesNotContain("Extract to a local function", await Titles(session, "start", "end"));
    }
}
