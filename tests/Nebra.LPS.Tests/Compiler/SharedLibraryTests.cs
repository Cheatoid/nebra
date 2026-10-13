using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class SharedLibraryTests
{
    private static NebraCompiler Check(params (string Name, string Source)[] files)
    {
        var root = Directory.CreateTempSubdirectory("nebra-library").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        foreach (var (name, source) in files)
        {
            compiler.AddSource(Path.Combine(root, "src", name), source);
        }

        compiler.Check();
        return compiler;
    }

    [Fact]
    public void EveryPackageSeesTheLibrariesWhichAreLoadedOnce()
    {
        var compiler = Check(("a.neb", "print(string.upper(\"a\"))\n"), ("b.neb", "print(math.floor(1.5))\n"));

        Assert.False(compiler.Diagnostics.HasErrors, string.Join("\n", compiler.Diagnostics.Diagnostics));
        Assert.All(compiler.Packages.Values, package => Assert.DoesNotContain(package.Files, file => file.IsDeclarationFile));
        Assert.Contains(compiler.Library.Files, file => file.Filename == "<stdlib>/std.d.neb");
    }

    [Fact]
    public void ATopLevelNameCannotShadowALibraryName()
    {
        var compiler = Check(("main.neb", "local string = 5\nprint(string)\n"));

        var error = Assert.Single(compiler.Diagnostics.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCode.ErrRedeclaration);
        Assert.EndsWith("main.neb", error.Span.File);
    }

    [Fact]
    public void ATopLevelFunctionStillOverloadsALibraryFunction()
    {
        var compiler = Check(("main.neb", "function print(value: number, label: string): nil\nend\n\nprint(1, \"one\")\nprint(\"plain\")\n"));

        Assert.False(compiler.Diagnostics.HasErrors, string.Join("\n", compiler.Diagnostics.Diagnostics));
    }
}
