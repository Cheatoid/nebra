using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class StdlibSourceTests
{
    private static string StdlibTestDeclarations()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "compiler", "stdlib", "test.d.neb");
            if (File.Exists(path)) return File.ReadAllText(path);
        }

        throw new FileNotFoundException("compiler/stdlib/test.d.neb");
    }

    [Fact]
    public async Task AStdlibFileOpenedFromSourceTakesThePlaceOfTheEmbeddedOne()
    {
        var source = StdlibTestDeclarations().Replace("declare module \"nebra:test\"", "declare module {|module|}\"nebra:test\"");
        await using var session = await LspSession.StartAsync(("src/stdlib/test.d.neb", source));

        var diagnostics = await session.OpenAsync("src/stdlib/test.d.neb");
        var hover = await session.HoverAsync("module");

        Assert.Empty(diagnostics);
        Assert.NotNull(hover);
        Assert.Contains("declare module \"nebra:test\"", hover);
        Assert.Contains("function expect", hover);
    }
}
