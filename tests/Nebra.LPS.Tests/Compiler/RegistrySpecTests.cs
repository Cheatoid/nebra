using Nebra.PackageManager;

namespace Nebra.LPS.Tests.Compiler;

public sealed class RegistrySpecTests
{
    [Theory]
    [InlineData("@nebra-lang/nanos-world", "@nebra-lang/nanos-world", null)]
    [InlineData("@nebra-lang/nanos-world@^1", "@nebra-lang/nanos-world", "^1")]
    [InlineData("nanos-world@v2", "nanos-world", "v2")]
    public void ARegistryNameKeepsItsScopeApartFromItsRange(string written, string name, string? range)
    {
        var spec = PackageSpec.Parse(written);

        Assert.Equal(SpecKind.Alias, spec.Kind);
        Assert.Equal(name, spec.AliasName);
        Assert.Equal(range, spec.AliasRange);
    }

    [Fact]
    public void APackageInADirectoryOfItsRepositoryKeepsTheDirectoryAndTheRange()
    {
        var spec = PackageSpec.Parse(Registry.WithRange("github:acme/mono/packages/core", "^1"));

        Assert.Equal(SpecKind.Git, spec.Kind);
        Assert.Equal("mono", spec.Repo);
        Assert.Equal("packages/core", spec.Subdir);
        Assert.Equal("^1", spec.Ref);
    }

    [Fact]
    public void APackageAtTheRootOfItsRepositoryTakesTheRangeAsItsRef()
    {
        var spec = PackageSpec.Parse(Registry.WithRange("https://github.com/acme/lib", "v2"));

        Assert.Equal("lib", spec.Repo);
        Assert.Null(spec.Subdir);
        Assert.Equal("v2", spec.Ref);
    }
}
