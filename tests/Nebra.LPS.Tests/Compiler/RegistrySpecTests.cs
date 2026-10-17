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
}
