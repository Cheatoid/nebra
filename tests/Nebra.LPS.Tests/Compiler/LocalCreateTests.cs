using Nebra.PackageManager;

namespace Nebra.LPS.Tests.Compiler;

public sealed class LocalCreateTests
{
    [Fact]
    public async Task ATemplateDirectoryOnDiskIsCopied()
    {
        var template = Directory.CreateTempSubdirectory("nebra-template").FullName;
        Directory.CreateDirectory(Path.Combine(template, "src"));
        File.WriteAllText(Path.Combine(template, "src", "main.neb"), "print(1)\n");
        var target = Path.Combine(Directory.CreateTempSubdirectory("nebra-created").FullName, "game");

        Assert.Equal(0, await new ProjectCreator().CreateAsync(template, new CreateOptions { TargetDirectory = target }));
        Assert.True(File.Exists(Path.Combine(target, "src", "main.neb")));
        Assert.True(File.Exists(Path.Combine(template, "src", "main.neb")));
    }

    [Fact]
    public async Task AProjectCannotBeCreatedInsideItsTemplate()
    {
        var template = Directory.CreateTempSubdirectory("nebra-template").FullName;
        File.WriteAllText(Path.Combine(template, "readme.md"), "template\n");

        Assert.Equal(1, await new ProjectCreator().CreateAsync(template, new CreateOptions { TargetDirectory = Path.Combine(template, "inner") }));
        Assert.False(Directory.Exists(Path.Combine(template, "inner")));
    }

    [Fact]
    public async Task ALocalSetupScriptIsTakenAsItIs()
    {
        var scripts = Directory.CreateTempSubdirectory("nebra-scripts").FullName;
        var script = Path.Combine(scripts, "setup.lua");
        File.WriteAllText(script, "print('setup')\n");
        var target = Path.Combine(Directory.CreateTempSubdirectory("nebra-created").FullName, "game");

        Assert.Equal(0, await new ProjectCreator().CreateAsync(script, new CreateOptions { TargetDirectory = target, SkipSetup = true }));
        Assert.Equal("print('setup')\n", File.ReadAllText(Path.Combine(target, "setup.lua")));
    }
}
