using Nebra.Configuration;

namespace Nebra.LPS.Tests.Compiler;

public sealed class TestDiscoveryTests
{
    [Fact]
    public void DiscoveryStaysOutOfLinksModulesAndNestedProjects()
    {
        var root = Directory.CreateTempSubdirectory("nebra-test-discovery").FullName;
        var tests = Path.Combine(root, "tests");
        Directory.CreateDirectory(Path.Combine(tests, "unit"));
        File.WriteAllText(Path.Combine(tests, "unit", "math_test.neb"), "");
        Directory.CreateDirectory(Path.Combine(tests, "fixture", "src"));
        File.WriteAllText(Path.Combine(tests, "fixture", "nebra.toml"), "name = \"fixture\"\n");
        File.WriteAllText(Path.Combine(tests, "fixture", "src", "main.neb"), "");
        Directory.CreateDirectory(Path.Combine(tests, "fixture", "nebra_modules"));
        Directory.CreateSymbolicLink(Path.Combine(tests, "fixture", "nebra_modules", "self"), root);
        Directory.CreateSymbolicLink(Path.Combine(tests, "loop"), root);

        var config = new Config { ProjectRoot = root, Test = { Dirs = [tests] } };

        var discovered = Program.DiscoverTestFiles(config, []);

        Assert.Equal([Path.Combine(tests, "unit", "math_test.neb")], discovered);
    }
}
