using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class CallHierarchyTests
{
    private static Task<LspSession> StartProject()
    {
        return LspSession.StartAsync(
            ("src/math.neb", "export function {|square|}square(x: number): number\n    return x * x\nend\n"),
            ("src/main.neb",
                "import { square } from \"math\"\n\n" +
                "local function {|area|}area(side: number): number\n    return {|call1|}square(side)\nend\n\n" +
                "local function {|twice|}twice(side: number): number\n    return {|areaCall|}area(side) + {|call2|}square(side)\nend\n\n" +
                "class Shape\n    function describe(): string\n        return tostring({|methodCall|}area(2))\n    end\nend\n\n" +
                "print({|top|}square(3), twice(1))\n"));
    }

    [Fact]
    public async Task IncomingCallsListEveryCallerAcrossFiles()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/math.neb");

        var item = await session.PrepareCallHierarchyAsync("square");
        Assert.NotNull(item);
        Assert.Equal("square", item.Name);

        var incoming = await session.IncomingCallsAsync(item);

        Assert.Equal(
            [
                ("area", (IReadOnlyList<SourceLocation>)[session.Location("call1")]),
                ("main.neb", [session.Location("top")]),
                ("twice", [session.Location("call2")])
            ],
            incoming.Select(call => (call.Caller, call.Calls)).ToList(),
            new CallComparer());
    }

    [Fact]
    public async Task AMethodCanBeACaller()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var item = await session.PrepareCallHierarchyAsync("area");
        var incoming = await session.IncomingCallsAsync(item!);

        Assert.Equal(["describe", "twice"], incoming.Select(call => call.Caller).ToList());
    }

    [Fact]
    public async Task OutgoingCallsListEveryCallee()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var item = await session.PrepareCallHierarchyAsync("twice");
        var outgoing = await session.OutgoingCallsAsync(item!);

        Assert.Equal(["area", "square"], outgoing);
    }

    [Fact]
    public async Task PreparingFromACallFindsTheCalledFunction()
    {
        await using var session = await StartProject();
        await session.OpenAsync("src/main.neb");

        var item = await session.PrepareCallHierarchyAsync("call1");

        Assert.NotNull(item);
        Assert.Equal("square", item.Name);
        Assert.EndsWith("math.neb", item.Uri.ToString());
    }

    private sealed class CallComparer : IEqualityComparer<(string, IReadOnlyList<SourceLocation>)>
    {
        public bool Equals((string, IReadOnlyList<SourceLocation>) x, (string, IReadOnlyList<SourceLocation>) y)
        {
            return x.Item1 == y.Item1 && x.Item2.SequenceEqual(y.Item2);
        }

        public int GetHashCode((string, IReadOnlyList<SourceLocation>) obj) => obj.Item1.GetHashCode();
    }
}
