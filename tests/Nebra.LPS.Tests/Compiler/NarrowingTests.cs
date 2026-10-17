using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class NarrowingTests
{
    private static List<Diagnostic> Errors(params (string Name, string Source)[] files)
    {
        var root = Directory.CreateTempSubdirectory("nebra-narrowing").FullName;
        var config = new Config { ProjectRoot = root, Source = "src" };
        config.Rules.StrictNil = true;
        var compiler = new NebraCompiler { Config = config };
        foreach (var (name, source) in files)
        {
            var path = Path.Combine(root, "src", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            compiler.AddSource(path, source);
        }

        compiler.Check();
        return compiler.Diagnostics.Diagnostics.Where(diagnostic => diagnostic.Level == DiagnosticLevel.Error).ToList();
    }

    private static List<Diagnostic> Errors(string source) => Errors(("main.neb", source));

    [Fact]
    public void AFileImportedThroughSeveralPathsIsCompiledOnce()
    {
        var errors = Errors(
            ("types/Box.neb", "export class Box<T>\n    value: T\n\n    constructor(value: T)\n        self.value = value\n    end\nend\n"),
            ("types/Use.neb", "import { Box } from \"./Box\"\n\nexport local first: Box<number> = new Box(1)\n"),
            ("types/columns/Other.neb", "import { Box } from \"../Box\"\n\nexport local second: Box<string> = new Box(\"a\")\n"));

        Assert.Empty(errors);
    }

    [Fact]
    public void NestedGenericArgumentsCloseWithTwoAngleBrackets()
    {
        Assert.Empty(Errors("class Box<T>\n    value: T\n\n    constructor(value: T)\n        self.value = value\n    end\nend\n\nlocal nested: Box<Box<number>> = new Box(new Box(1))\nprint(nested.value.value)\n"));
    }

    [Fact]
    public void ARightShiftNeedsBothAngleBracketsTogether()
    {
        Assert.Equal(DiagnosticCode.ErrUnexpectedToken, Assert.Single(Errors("print(256 > > 2)\n")).Code);
    }

    [Fact]
    public void TableIsATypeToCheckFor()
    {
        Assert.Empty(Errors("local function size(raw: any): number\n    if raw is table then\n        return #raw\n    end\n    return 0\nend\n\nprint(size({}))\n"));
    }

    private const string Shapes = "class Decimal\n    value: number = 0\nend\n\n";

    [Fact]
    public void InstanceofNarrowsBothBranches()
    {
        Assert.Empty(Errors(Shapes + "local function read(value: Decimal | number): number\n    if value instanceof Decimal then\n        local decimal: Decimal = value\n        return decimal.value\n    else\n        local plain: number = value\n        return plain\n    end\nend\n\nprint(read(1))\n"));
    }

    [Theory]
    [InlineData("local function check(id: number?): number\n    if id == nil or id < 1 then\n        return 0\n    end\n    return id + 1\nend\n\nprint(check(1))\n")]
    [InlineData("local function check(id: number?): boolean\n    return id ~= nil and id > 1\nend\n\nprint(check(1))\n")]
    [InlineData("local function pad(hex: string?): string\n    if hex == nil then\n        return \"\"\n    end\n    if #hex == 6 then\n        hex = hex .. \"FF\"\n    end\n    return string.upper(hex)\nend\n\nprint(pad(\"a\"))\n")]
    public void GuardsNarrowAcrossAndOrAndAssignments(string source)
    {
        Assert.Empty(Errors(source));
    }

    [Fact]
    public void AssigningNilEndsTheNarrowing()
    {
        var errors = Errors("local function pad(hex: string?): string\n    if hex == nil then\n        return \"\"\n    end\n    hex = nil\n    return string.upper(hex)\nend\n\nprint(pad(\"a\"))\n");

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ParenthesesKeepOnlyTheFirstValue()
    {
        Assert.Empty(Errors("local trimmed: string = (string.gsub(\" a \", \" \", \"\"))\nprint(trimmed)\n"));
    }

    [Fact]
    public void ATableWithNamedKeysFitsAMapOfStrings()
    {
        Assert.Empty(Errors("local factors: { [string]: number } = { ms = 1, s = 1000 }\nprint(factors[\"ms\"])\n"));
        Assert.NotEmpty(Errors("local factors: { [string]: number } = { ms = \"one\" }\nprint(factors)\n"));
    }

    private const string Tree = "class Node\n    name: string = \"n\"\n    parent: Node? = nil\nend\n\n";

    [Fact]
    public void AWhileConditionNarrowsTheBodyUntilTheValueIsReassigned()
    {
        Assert.Empty(Errors(Tree + "local function depth(start: Node?): number\n    local x = start\n    local count = 0\n    while x ~= nil do\n        print(x.name)\n        count = count + 1\n        x = x.parent\n    end\n    return count\nend\n\nprint(depth(nil))\n"));
        Assert.Equal(DiagnosticCode.ErrPossiblyNil, Assert.Single(Errors(Tree + "local function walk(start: Node?): nil\n    local x = start\n    while x ~= nil do\n        x = x.parent\n        print(x.name)\n    end\nend\n\nwalk(nil)\n")).Code);
    }

    [Fact]
    public void KeywordsAreMethodNamesExceptNew()
    {
        Assert.Empty(Errors("class Builder\n    function abstract(): Builder\n        return self\n    end\n\n    function extends(base: string): Builder\n        return self\n    end\nend\n\nprint(new Builder():abstract():extends(\"Model\"))\n"));
        Assert.Equal(DiagnosticCode.ErrUnexpectedToken, Assert.Single(Errors("class Builder\n    function new(): nil\n    end\nend\n\nprint(Builder)\n")).Code);
    }

    [Fact]
    public void ASyntaxErrorInAnImportedModuleIsReported()
    {
        var errors = Errors(("broken.neb", "export class Broken\n    function (): nil\n    end\nend\n"), ("main.neb", "import { Broken } from \"./broken\"\n\nprint(Broken)\n"));

        Assert.Contains(errors, error => error.Code == DiagnosticCode.ErrUnexpectedToken && error.Message == "missing a name");
    }
}
