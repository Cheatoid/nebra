using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class SourceFormTests
{
    private static ProjectBuild Build(string main, params (string Name, string Source)[] others)
    {
        return ProjectBuild.Run(null, others.Append(("src/main.neb", main)).ToArray());
    }

    [Fact]
    public void AStaticFieldInitializedWithItsOwnNameReadsTheOuterValue()
    {
        var build = Build("class Meta\n    name: string = \"meta\"\nend\n\nclass Api\n    static Meta: any = Meta\n    static count: number = 1\n    static next: number = count + 1\n\n    static function describe(entry: Meta): string\n        return entry.name\n    end\nend\n\nprint(Api.describe(new Meta()), Api.next)\n");

        Assert.Empty(build.Errors);
        Assert.Contains("Api.Meta = Meta", build.Lua("src/main.neb"));
        Assert.Contains("Api.next = Api.count + 1", build.Lua("src/main.neb"));
    }

    [Fact]
    public void AStaticMemberPassesOnWhatItsClassImports()
    {
        var build = Build("import { toSnakeCase, Connection } from \"./util\"\n\nclass Api\n    static Connection: any = Connection\n    static count: number = 1\n    static next: number = count + 1\n\n    static function toSnakeCase(name: string): string\n        return toSnakeCase(name)\n    end\n\n    static function connect(url: string): Connection\n        return new Connection(url)\n    end\nend\n\nprint(Api.toSnakeCase(\"A\"), Api.connect(\"db\"), Api.next)\n",
            ("src/util.neb", "export function toSnakeCase(name: string): string\n    return string.lower(name)\nend\n\nexport class Connection\n    url: string\n\n    constructor(url: string)\n        self.url = url\n    end\nend\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("return toSnakeCase(name)", lua);
        Assert.Contains("return Connection.new(url)", lua);
        Assert.Contains("Api.next = Api.count + 1", lua);
    }

    [Fact]
    public void MethodCallsFollowNewAndContinueOnTheNextLine()
    {
        var build = Build("class Builder\n    function column(name: string): Builder\n        return self\n    end\n\n    function build(): nil\n    end\nend\n\nnew Builder()\n    :column(\"a\")\n    :column(\"b\")\n    :build()\nprint(new Builder():column(\"c\"))\n");

        Assert.Empty(build.Errors);
        Assert.Contains("Builder.new():column(\"a\"):column(\"b\"):build()", build.Lua("src/main.neb"));
    }

    [Fact]
    public void ADeclarationFileInTheSourcesDeclaresGlobalsUnlessItExports()
    {
        var build = Build("print(GameApi.version)\n",
            ("src/types/globals.d.neb", "declare interface Api\n    version: string\nend\n\ndeclare GameApi: Api\n"),
            ("src/types/module.d.neb", "export declare ModuleOnly: number\n"));

        Assert.Empty(build.Errors);
        Assert.Equal(DiagnosticCode.ErrUndeclaredSymbol, Assert.Single(Build("print(ModuleOnly)\n", ("src/types/module.d.neb", "export declare ModuleOnly: number\n")).Errors).Code);
    }

    [Fact]
    public void AFunctionFitsWhenItsReturnAndParametersFit()
    {
        Assert.Empty(Build("class Column<T>\n    name: string = \"c\"\nend\n\nlocal factory: (string) -> Column<any> = function(name: string): any\n    return new Column()\nend\nlocal ignoring: (string, number) -> nil = function(name: string): nil\n    print(name)\nend\nprint(factory, ignoring)\n").Errors);
        Assert.Equal(DiagnosticCode.ErrTypeMismatch, Assert.Single(Build("local wrong: (string) -> number = function(name: string): string\n    return name\nend\nprint(wrong)\n").Errors).Code);
    }

    [Fact]
    public void OnlyNumericKeysAreShiftedByTheIndexBase()
    {
        var lua = Build("local t = { [\"a\"] = 1, [2] = \"x\" }\nprint(t[\"a\"], t[2])\n").Lua("src/main.neb");

        Assert.Contains("[\"a\"] = 1", lua);
        Assert.Contains("t[\"a\"]", lua);
        Assert.Contains("[(2 + 1)] = \"x\"", lua);
    }
}
