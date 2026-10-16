using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

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

    [Theory]
    [InlineData("local function show(format: Format?)\n    print(format.text())\nend\n")]
    [InlineData("local function show(format: Format?, fallback: Plain)\n    print((format ?? fallback).text())\nend\n")]
    public void AReceiverThatMayBeOfSeveralTypesStillNeedsAColon(string use)
    {
        var root = Directory.CreateTempSubdirectory("nebra-colon").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(Path.Combine(root, "src", "main.neb"),
            "interface Format\n    method text(): string\nend\n\nclass Plain implements Format\n    function text(): string\n        return \"plain\"\n    end\nend\n\n" + use);
        compiler.Check();

        var error = Assert.Single(compiler.Diagnostics.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.Equal(DiagnosticCode.ErrInterfaceMethodNeedsColon, error.Code);
    }
}
