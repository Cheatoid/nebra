namespace Nebra.LPS.Tests.Compiler;

public sealed class TrampolineTests
{
    private static string NewHome() => Directory.CreateTempSubdirectory("nebra-home").FullName;

    [Fact]
    public void UseSelectsABuildByItsDirectoryAndUnuseForgetsIt()
    {
        var home = NewHome();
        var build = Directory.CreateTempSubdirectory("nebra-build").FullName;
        var assembly = Path.Combine(build, "nebra.dll");
        File.WriteAllText(assembly, "");

        Assert.Equal(0, Trampoline.RunUse([build], home));
        Assert.Equal(assembly, Trampoline.ReadTarget(home));

        Assert.Equal(0, Trampoline.RunUnuse(home));
        Assert.Null(Trampoline.ReadTarget(home));
    }

    [Fact]
    public void UseRejectsAPathWithoutANebraBuild()
    {
        var home = NewHome();

        Assert.Equal(1, Trampoline.RunUse([Directory.CreateTempSubdirectory("nebra-empty").FullName], home));
        Assert.Null(Trampoline.ReadTarget(home));
    }

    [Theory]
    [InlineData("use")]
    [InlineData("unuse")]
    [InlineData("upgrade")]
    [InlineData("check")]
    public void CommandsThatManageTheInstallationAreNeverForwarded(string command)
    {
        var home = NewHome();
        File.WriteAllText(Path.Combine(home, "override"), "/missing/nebra");

        Assert.Null(Trampoline.TryForward([command], home));
    }

    [Fact]
    public void AMissingTargetFailsInsteadOfRunningTheInstalledVersion()
    {
        var home = NewHome();
        File.WriteAllText(Path.Combine(home, "override"), "/missing/nebra");

        Assert.Equal(1, Trampoline.TryForward(["build"], home));
    }

    [Fact]
    public void WithoutASelectedBuildTheCallIsHandledHere()
    {
        Assert.Null(Trampoline.TryForward(["build"], NewHome()));
    }
}
