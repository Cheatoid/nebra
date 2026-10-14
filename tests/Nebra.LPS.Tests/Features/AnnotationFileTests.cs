using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class AnnotationFileTests
{
    private const string Config = "name = \"app\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\nannotations = [\"annotations\"]\n";

    private const string Hook = """
        --- Subscribes the function to an event.

        export local annotation = {
            targets = { "Function" },
            params = {
                name = { type = "string", required = true },
                priority = { type = "number" }
            }
        }

        export function apply(target, args)
            local subscribe = ir.exprStmt(ir.call(ir.nameExpr("subscribe"), {
                ir.stringLiteral(args.name), ir.nameExpr(target.namePath[1].name)
            }))
            return { target, subscribe }
        end
        """;

    private static Task<LspSession> Start(string annotation)
    {
        return LspSession.StartAsync(("nebra.toml", Config), ("annotations/Hook.neb", annotation), ("src/main.neb", "print(1)\n"));
    }

    [Fact]
    public async Task AValidAnnotationFileHasNoProblems()
    {
        await using var session = await Start(Hook);

        Assert.Empty(await session.OpenAsync("annotations/Hook.neb"));
    }

    [Fact]
    public async Task AnAnnotationFileOfADependencyHasNoProblems()
    {
        await using var session = await LspSession.StartAsync(
            ("nebra.toml", Config.Replace("annotations = [\"annotations\"]\n", "")),
            ("nebra_modules/hooks/nebra.toml", "name = \"hooks\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nannotations = [\"annotations\"]\n"),
            ("nebra_modules/hooks/annotations/Hook.neb", Hook),
            ("src/main.neb", "print(1)\n"));

        Assert.Empty(await session.OpenAsync("nebra_modules/hooks/annotations/Hook.neb"));
    }

    [Fact]
    public async Task AMissingApplyAndAnUnknownTargetAreReportedWhereTheyAre()
    {
        await using var session = await Start("export local annotation = {\n    targets = { \"Funktion\" }\n}\n");

        var diagnostics = await session.OpenAsync("annotations/Hook.neb");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Code?.String == "ErrAnnotationMissingApply" && diagnostic.Range.Start.Line == 0);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code?.String == "ErrAnnotationMetaInvalid" && diagnostic.Message.Contains("Funktion"));
    }

    [Fact]
    public async Task TheIrBuilderIsCompletedAndDocumented()
    {
        await using var session = await Start(Hook.Replace("local subscribe = ir.exprStmt(", "local subscribe = ir.{|member|}exprStmt("));
        await session.OpenAsync("annotations/Hook.neb");

        Assert.Contains("stringLiteral", await session.CompletionLabelsAsync("member"));
        Assert.Contains("An expression used as a statement.", await session.HoverAsync("member"));
    }

    [Fact]
    public async Task TheArgumentsHaveTheShapeOfTheDeclaredParameters()
    {
        await using var session = await Start(Hook.Replace("ir.stringLiteral(args.name)", "ir.stringLiteral(args.{|field|}name .. args.nmae)"));

        var diagnostics = await session.OpenAsync("annotations/Hook.neb");

        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("nmae"));
        Assert.Contains("string", await session.HoverAsync("field"));
    }

    [Fact]
    public async Task TheTargetsOfTheMetaTableAreCompleted()
    {
        await using var session = await Start(Hook.Replace("targets = { \"Function\" }", "targets = { \"{|target|}\" }"));
        await session.OpenAsync("annotations/Hook.neb");

        var labels = await session.CompletionLabelsAsync("target");

        Assert.Contains("ClassMethod", labels);
        Assert.Contains("Function", labels);
    }
}
