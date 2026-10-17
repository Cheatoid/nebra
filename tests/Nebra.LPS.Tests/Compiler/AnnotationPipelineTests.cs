using Nebra.Diagnostics;

namespace Nebra.LPS.Tests.Compiler;

public sealed class AnnotationPipelineTests
{
    private static ProjectBuild Build(string main, params (string Name, string Source)[] annotations)
    {
        var files = annotations.Select(file => ("annotations/" + file.Name, file.Source)).Append(("src/main.neb", main)).ToArray();
        return ProjectBuild.Run(config => config.Annotations = ["annotations"], files);
    }

    private const string Marker = "export local annotation = {\n    targets = { \"ClassField\" }\n}\n\nexport function apply(target, args)\n    return target\nend\n";

    [Fact]
    public void AClassAnnotationSeesTheAnnotationsOfItsFields()
    {
        const string columns = "export local annotation = {\n    targets = { \"Class\" }\n}\n\nexport function apply(target, args)\n    local names = {}\n    for _, field in ipairs(target.fields) do\n        for _, marker in ipairs(field.annotations or {}) do\n            if marker.name.name == \"Column\" then\n                names[#names + 1] = ir.stringLiteral(field.name.name)\n            end\n        end\n    end\n    return { target, ir.localDecl(\"columns\", ir.table(names)) }\nend\n";
        var build = Build("@Columns\nclass User\n    @Column\n    name: string = \"\"\n    age: number = 0\n    @Column\n    mail: string = \"\"\nend\n\nprint(User, columns)\n",
            ("Columns.neb", columns), ("Column.neb", Marker));

        Assert.Empty(build.Errors);
        Assert.Matches("local columns = \\{\\s*\"name\",\\s*\"mail\"\\s*\\}", build.Lua("src/main.neb"));
    }

    [Fact]
    public void ATableBuiltByAnAnnotationKeepsEveryKindOfField()
    {
        const string settings = "export local annotation = {\n    targets = { \"Class\" }\n}\n\nexport function apply(target, args)\n    local value = ir.table({ { key = \"size\", value = ir.numberLiteral(2) }, { key = ir.stringLiteral(\"mode\"), value = ir.stringLiteral(\"fast\") }, ir.boolLiteral(true) })\n    return { target, ir.localDecl(\"settings\", value) }\nend\n";
        var lua = Build("@Settings\nclass Box\nend\n\nprint(Box, settings)\n", ("Settings.neb", settings)).Lua("src/main.neb");

        Assert.Contains("size = 2", lua);
        Assert.Contains("[\"mode\"] = \"fast\"", lua);
        Assert.Contains("true", lua);
    }

    [Fact]
    public void AnAnnotationImportsHelpersThatAreNotAnnotationsThemselves()
    {
        const string helper = "export function shout(text: string): string\n    return string.upper(text) .. \"!\"\nend\n";
        const string greet = "import { shout } from \"./util/text\"\n\nexport local annotation = {\n    targets = { \"Class\" }\n}\n\nexport function apply(target, args)\n    return { target, ir.localDecl(\"greeting\", ir.stringLiteral(shout(\"hi\"))) }\nend\n";
        var build = Build("@Greet\nclass Box\nend\n\nprint(Box, greeting)\n", ("Greet.neb", greet), ("util/text.neb", helper));

        Assert.Empty(build.Errors);
        Assert.Contains("local greeting = \"HI!\"", build.Lua("src/main.neb"));
    }

    [Fact]
    public void AGenericFieldSurvivesAnAnnotationOnItsClass()
    {
        const string kinds = "export local annotation = {\n    targets = { \"Class\" }\n}\n\nexport function apply(target, args)\n    return { target, ir.localDecl(\"kind\", ir.stringLiteral(target.fields[1].typeAnnotation.kind)) }\nend\n";
        var build = Build("class Json<T>\n    value: T? = nil\nend\n\n@Kinds\nclass Holder\n    data: Json<any>? = nil\nend\n\nprint(Holder, kind)\n", ("Kinds.neb", kinds));

        Assert.Empty(build.Errors);
        Assert.Contains("local kind = \"Named\"", build.Lua("src/main.neb"));
    }

    [Fact]
    public void AnImportTheSandboxDoesNotServeIsReportedByName()
    {
        const string broken = "export local annotation = {\n    targets = { \"Class\" }\n}\n\nexport function apply(target, args)\n    require(\"os\")\n    return target\nend\n";
        var error = Assert.Single(Build("@Broken\nclass Box\nend\n\nprint(Box)\n", ("Broken.neb", broken)).Errors);

        Assert.Equal(DiagnosticCode.ErrAnnotationRuntimeError, error.Code);
        Assert.Contains("'os'", error.Message);
    }
}
