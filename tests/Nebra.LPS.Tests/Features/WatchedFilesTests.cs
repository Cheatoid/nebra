using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class WatchedFilesTests
{
    [Fact]
    public async Task AnErrorWrittenToAClosedFileIsPublished()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "print(1)\n"),
            ("src/other.neb", "local count: number = 3\nprint(count)\n"));

        await session.OpenAsync("src/main.neb");
        Assert.Empty(await session.PublishedDiagnostics("src/other.neb"));

        var next = session.NextDiagnostics("src/other.neb");
        session.ChangeOnDisk(("src/other.neb", "local count: number = \"three\"\nprint(count)\n"));

        Assert.Single(await next);
    }

    [Fact]
    public async Task DeletingAFileClearsItsDiagnostics()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "print(1)\n"),
            ("src/other.neb", "local count: number = \"three\"\nprint(count)\n"));

        await session.OpenAsync("src/main.neb");
        Assert.Single(await session.PublishedDiagnostics("src/other.neb"));

        var next = session.NextDiagnostics("src/other.neb");
        session.ChangeOnDisk(("src/other.neb", null));

        Assert.Empty(await next);
    }

    [Fact]
    public async Task EditingTheProjectConfigurationTakesEffect()
    {
        const string config = "name = \"lps-test\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n";
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", config),
            ("types/ambient.d.neb", "declare ambientValue: number\n"),
            ("src/main.neb", "local value: number = ambientValue\nprint(value)\n"));

        Assert.NotEmpty(await session.OpenAsync("src/main.neb"));

        var next = session.NextDiagnostics("src/main.neb");
        session.ChangeOnDisk(("nebra.toml", config + "globals = [\"types\"]\n"));

        Assert.Empty(await next);
    }
}
