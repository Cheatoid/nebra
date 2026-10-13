using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class ProjectTests
{
    private const string Config = "name = \"{0}\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n";
    private const string ConfigWithTypes = Config + "globals = [\"types\"]\n";

    [Fact]
    public async Task ANestedProjectIsCompiledWithItsOwnConfiguration()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "print(toolValue)\n"),
            ("src/plugin/nebra.toml", string.Format(ConfigWithTypes, "plugin")),
            ("src/plugin/types/ambient.d.neb", "declare toolValue: number\n"),
            ("src/plugin/src/tool.neb", "local value: number = toolValue\nprint(value)\n"));

        Assert.Empty(await session.OpenAsync("src/plugin/src/tool.neb"));
        Assert.NotEmpty(await session.OpenAsync("src/main.neb"));
    }

    [Fact]
    public async Task TheEnclosingProjectLeavesANestedProjectsFilesAlone()
    {
        await using var session = await LspSession.StartAsync(
            ("src/main.neb", "print(1)\n"),
            ("src/plugin/nebra.toml", string.Format(ConfigWithTypes, "plugin")),
            ("src/plugin/types/ambient.d.neb", "declare toolValue: number\n"),
            ("src/plugin/src/tool.neb", "local value: number = toolValue\nprint(value)\n"));

        await session.OpenAsync("src/main.neb");

        Assert.Empty(await session.PublishedDiagnostics("src/plugin/src/tool.neb"));
    }

    [Fact]
    public async Task EveryWorkspaceFolderIsAProjectOfItsOwn()
    {
        await using var session = await LspSession.StartAsync(
            ["game", "server"],
            ("game/nebra.toml", string.Format(ConfigWithTypes, "game")),
            ("game/types/ambient.d.neb", "declare toolValue: number\n"),
            ("game/src/main.neb", "local value: number = toolValue\nprint(value)\n"),
            ("server/nebra.toml", string.Format(Config, "server")),
            ("server/src/main.neb", "print(toolValue)\n"));

        Assert.Empty(await session.OpenAsync("game/src/main.neb"));
        Assert.NotEmpty(await session.OpenAsync("server/src/main.neb"));
    }

    [Fact]
    public async Task WorkspaceSymbolsSearchEveryProject()
    {
        await using var session = await LspSession.StartAsync(
            ["game", "server"],
            ("game/nebra.toml", string.Format(Config, "game")),
            ("game/src/main.neb", "local function spawnPlayer(): nil\nend\n\nspawnPlayer()\n"),
            ("server/nebra.toml", string.Format(Config, "server")),
            ("server/src/main.neb", "local function spawnServer(): nil\nend\n\nspawnServer()\n"));

        await session.OpenAsync("game/src/main.neb");
        var names = (await session.WorkspaceSymbolsAsync("spawn")).Select(symbol => symbol.Name).Order().ToList();

        Assert.Equal(["spawnPlayer", "spawnServer"], names);
    }

    [Fact]
    public async Task ANewProjectConfigurationTakesOverItsOpenFiles()
    {
        await using var session = await LspSession.StartAsync(
            ("src/plugin/types/ambient.d.neb", "declare toolValue: number\n"),
            ("src/plugin/src/tool.neb", "local value: number = toolValue\nprint(value)\n"));

        Assert.NotEmpty(await session.OpenAsync("src/plugin/src/tool.neb"));

        var next = session.NextDiagnostics("src/plugin/src/tool.neb");
        session.ChangeOnDisk(("src/plugin/nebra.toml", string.Format(ConfigWithTypes, "plugin")));

        Assert.Empty(await next);
    }
}
