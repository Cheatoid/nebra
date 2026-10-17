using Nebra.PackageManager;

namespace Nebra.LPS.Tests.Compiler;

public sealed class LocalDependencyTests
{
    [Fact]
    public void ThePackageIntegrityIgnoresLinksBackToItselfItsOutputAndGit()
    {
        var root = Directory.CreateTempSubdirectory("nebra-local-package").FullName;
        File.WriteAllText(Path.Combine(root, "nebra.toml"), "name = \"lib\"\noutput = \"build\"\n");
        File.WriteAllText(Path.Combine(root, "init.neb"), "print(1)\n");
        var nested = Path.Combine(root, "tests", "smoke", "nebra_modules");
        Directory.CreateDirectory(nested);
        Directory.CreateSymbolicLink(Path.Combine(nested, "lib"), root);
        var before = GitFetcher.ComputeIntegrity(root);

        Directory.CreateDirectory(Path.Combine(root, "build"));
        File.WriteAllText(Path.Combine(root, "build", "init.lua"), "print(1)\n");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "HEAD"), "ref: refs/heads/main\n");

        Assert.Equal(before, GitFetcher.ComputeIntegrity(root));
    }

    [Fact]
    public void TheDeclarationsOfADependencysNestedProjectsAreNotLoaded()
    {
        var build = ProjectBuild.Run(null,
            ("nebra_modules/lib/nebra.toml", "name = \"lib\"\n"),
            ("nebra_modules/lib/types.d.neb", "declare class File\nend\n"),
            ("nebra_modules/lib/tests/smoke/nebra.toml", "name = \"smoke\"\n"),
            ("nebra_modules/lib/tests/smoke/out/types.d.neb", "declare class File\nend\n"),
            ("src/main.neb", "local file: File? = nil\nprint(file)\n"));

        Assert.Empty(build.Errors);
    }
}
