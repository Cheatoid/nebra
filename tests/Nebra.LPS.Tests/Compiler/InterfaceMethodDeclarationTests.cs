using Nebra.Compiler;
using Nebra.Configuration;

namespace Nebra.LPS.Tests.Compiler;

public sealed class InterfaceMethodDeclarationTests
{
    [Fact]
    public void GeneratedDeclarationsKeepHowEachMemberIsDeclared()
    {
        var root = Directory.CreateTempSubdirectory("nebra-declgen").FullName;
        var path = Path.Combine(root, "src", "greeter.neb");
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(path, "export interface Greeter\n    method greet(name: string): string\n    function create(): Greeter\nend\n");

        Assert.True(compiler.Compile(), string.Join("\n", compiler.Diagnostics.Diagnostics));

        var declarations = (string)compiler.Cache["GeneratedDeclarations"];
        Assert.Contains("method greet(name: string): string", declarations);
        Assert.Contains("function create(): Greeter", declarations);
    }
}
