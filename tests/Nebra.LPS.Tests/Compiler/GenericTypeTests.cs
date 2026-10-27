using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class GenericTypeTests
{
    private const string Library = "export interface Source<T>\n    method next(): T?\nend\n\nexport class Box<T> implements Source<T>\n    value: T\n\n    constructor(value: T)\n        self.value = value\n    end\n\n    function next(): T?\n        return self.value\n    end\n\n    function map<U>(f: (T) -> U): Box<U>\n        return new Box(f(self.value))\n    end\nend\n\nexport class Numbers extends Box<number>\nend\n\nexport function firstOf<T>(source: Source<T>): T?\n    return source:next()\nend\n\n";

    private static ProjectBuild Build(string source) =>
        ProjectBuild.Run(config => config.Rules.StrictNil = true, ("src/main.neb", Library + source));

    [Theory]
    [InlineData("local box = new Box(1)\nlocal wrong: string = box.value\n", "number")]
    [InlineData("local box = new Box(1)\nlocal wrong: boolean = box:map(function(item: number): string return tostring(item) end).value\n", "string")]
    [InlineData("local wrong: string = new Numbers(2).value\n", "number")]
    [InlineData("local wrong: string = firstOf(new Numbers(2))\n", "number | nil")]
    [InlineData("local wrong: Box<string> = new Numbers(2)\n", "Numbers")]
    [InlineData("local numbers: Box<number> = new Box(1)\nlocal wrong: Source<string> = numbers\n", "Box<number>")]
    public void TheMembersOfAnInstantiationHaveItsTypeArguments(string use, string got)
    {
        var error = Assert.Single(Build(use).Errors);

        Assert.Equal(DiagnosticCode.ErrTypeMismatch, error.Code);
        Assert.Contains($"but got '{got}'", error.Message);
    }

    [Fact]
    public void AnInstantiationFitsOneOfWiderTypeArguments()
    {
        Assert.Empty(Build("local nested: Box<Box<number>> = new Box(new Box(1))\nlocal wide: Box<number?> = new Numbers(2)\nlocal source: Source<number> = new Numbers(3)\nlocal inner: number = nested.value.value\nprint(wide, source, inner)\n").Errors);
    }

    [Fact]
    public void ARuntimeCheckAgainstAnInstantiationRecordsTheTypeArgumentsOfWhatIsCreated()
    {
        var build = Build("local value: any = new Numbers(2)\nprint(value is Box<number>, value instanceof Box<number>, value is Source<number>, value is Box)\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("__nebra_generic_types", lua);
    }

    [Fact]
    public void WithoutARuntimeCheckAgainstAnInstantiationNothingIsRecorded()
    {
        var build = Build("local value: any = new Numbers(2)\nprint(value is Box)\n");

        Assert.Empty(build.Errors);
        Assert.DoesNotContain("__nebra_generic_types", build.Lua("src/main.neb"));
    }

    [Fact]
    public void ACheckAgainstTypeArgumentsThatAreTypeParametersWarnsThatOnlyTheClassIsChecked()
    {
        var build = Build("local function holds<T>(value: any): boolean\n    return value is Box<T>\nend\n\nprint(holds(1))\n");

        Assert.Contains(build.Compiler.Diagnostics.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCode.WarnGenericInstanceOfErased);
    }

    [Fact]
    public void GeneratedDeclarationsKeepTypeParametersAndTypeArguments()
    {
        var root = Directory.CreateTempSubdirectory("nebra-declgen").FullName;
        var compiler = new NebraCompiler { Config = new Config { ProjectRoot = root, Source = "src" } };
        compiler.AddSource(Path.Combine(root, "src", "init.neb"), Library + "export class Tagged<T extends Box<number> implements Source<number>>\n    item: T\n\n    constructor(item: T)\n        self.item = item\n    end\nend\n");

        Assert.True(compiler.Compile(), string.Join("\n", compiler.Diagnostics.Diagnostics));

        var declarations = (string)compiler.Cache["GeneratedDeclarations"];
        Assert.Contains("interface Source<T>", declarations);
        Assert.Contains("class Box<T> implements Source<T>", declarations);
        Assert.Contains("function map<U>(f: (T) -> U): Box<U>", declarations);
        Assert.Contains("class Numbers extends Box<number>", declarations);
        Assert.Contains("function firstOf<T>(source: Source<T>): T | nil", declarations);
        Assert.Contains("class Tagged<T extends Box<number> implements Source<number>>", declarations);
    }
}
