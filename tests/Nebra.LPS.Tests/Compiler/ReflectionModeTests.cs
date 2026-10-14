using Nebra.Compiler;
using Nebra.Configuration;

namespace Nebra.LPS.Tests.Compiler;

public sealed class ReflectionModeTests
{
    [Fact]
    public void AnnotatedReflectionRecordsOnlyMarkedVariables()
    {
        var root = Directory.CreateTempSubdirectory("nebra-reflection").FullName;
        var config = new Config { ProjectRoot = root, Source = "src", Name = "app" };
        config.Reflection.Mode = ReflectionMode.Annotated;
        var compiler = new NebraCompiler { Config = config };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"), "local secret = 1\n@reflectable\nlocal shown = 2\nprint(secret, shown)\n");

        Assert.True(compiler.Compile(), string.Join("\n", compiler.Diagnostics.Diagnostics));

        var lua = compiler.Packages.Values.SelectMany(package => package.Files).Single(file => !file.IsDeclarationFile).GeneratedLua!;
        Assert.Contains("main::shown", lua);
        Assert.DoesNotContain("main::secret", lua);
    }
}
