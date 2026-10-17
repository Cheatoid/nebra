using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class RuntimeGlobalTests
{
    private static readonly (string, string)[] Package =
    [
        ("nebra_modules/orm/nebra.toml", "name = \"orm\"\nversion = \"0.1.0\"\nsource = \"src\"\nruntime_global = \"Orm\"\n"),
        ("nebra_modules/orm/src/Model.neb", "export class Model\n    id: number = 0\nend\n"),
        ("nebra_modules/orm/src/util/Uuid.neb", "export class Uuid\n    static function v7(): Uuid\n        return new Uuid()\n    end\nend\n"),
        ("nebra_modules/orm/src/init.neb", "export { Model } from \"./Model\"\nexport { Uuid as Id } from \"./util/Uuid\"\n"),
    ];

    private static ProjectBuild Consumer(string main) => ProjectBuild.Run(null, Package.Append(("src/main.neb", main)).ToArray());

    [Fact]
    public void AnImportFromThePackageReadsItsGlobal()
    {
        var build = Consumer("import { Model, Id } from \"orm\"\nimport type { Uuid } from \"orm/util/Uuid\"\n\nclass User extends Model\n    key: Uuid = Id.v7()\nend\n\nprint(new User())\n");

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/main.neb");
        Assert.Contains("= Orm or error(\"the package 'orm' is not loaded: the global Orm is missing\", 0)", lua);
        Assert.DoesNotContain("require", lua);
    }

    [Fact]
    public void AValueImportFromInsideThePackageIsReported()
    {
        var error = Assert.Single(Consumer("import { Uuid } from \"orm/util/Uuid\"\n\nprint(Uuid)\n").Errors);

        Assert.Equal(DiagnosticCode.ErrRuntimeGlobalSubpath, error.Code);
    }

    [Theory]
    [InlineData(null, "_G[\"Orm\"] = __nebra_exports")]
    [InlineData("Package.Export(%name, %value)", "Package.Export(\"Orm\", __nebra_exports)")]
    public void TheRootModuleSetsTheGlobalFromItsExports(string? template, string expected)
    {
        var build = ProjectBuild.Run(config =>
            {
                config.RuntimeGlobal = "Orm";
                config.RuntimeExport = template;
            },
            ("src/Model.neb", "export class Model\nend\n"),
            ("src/init.neb", "export { Model } from \"./Model\"\nexport local version = \"1\"\n"));

        Assert.Empty(build.Errors);
        var lua = build.Lua("src/init.neb");
        Assert.Contains("local __nebra_exports = {", lua);
        Assert.Contains("Model = Model", lua);
        Assert.Contains(expected, lua);
        Assert.EndsWith("return __nebra_exports\n", lua);
        Assert.DoesNotContain("__nebra_exports", build.Lua("src/Model.neb"));
    }
}
