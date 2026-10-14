using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class AnnotationTests
{
    private const string Hook = """
        --- Subscribes the function to an event, such as `@Hook("Tick")`.

        export local annotation = {
            targets = { "Function" },
            params = {
                name = { type = "string", required = true }
            }
        }

        export function apply(target, args)
            local subscribe = ir.exprStmt(ir.call(ir.nameExpr("subscribe"), {
                ir.stringLiteral(args.name), ir.nameExpr(target.namePath[1].name)
            }))
            return { target, subscribe }
        end
        """;

    private static readonly (string, string)[] DependencyFiles =
    [
        ("nebra_modules/hooks/nebra.toml", "name = \"hooks\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nannotations = [\"annotations\"]\n"),
        ("nebra_modules/hooks/annotations/Hook.neb", Hook),
        ("types/api.d.neb", "declare function subscribe(name: string, handler: any): nil\n")
    ];

    private const string Config = "name = \"app\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\nglobals = [\"types\"]\n";

    private static Task<LspSession> Start(string main)
    {
        return LspSession.StartAsync([("nebra.toml", Config), ..DependencyFiles, ("src/main.neb", main)]);
    }

    [Fact]
    public async Task AnnotationsOfDependenciesAreOfferedWhereTheyApply()
    {
        await using var session = await Start("@{|function|}\nfunction onTick(): nil\nend\n\n@{|variable|}\nlocal count = 1\nprint(count)\n");
        await session.OpenAsync("src/main.neb");

        Assert.Contains("Hook", await session.CompletionLabelsAsync("function"));
        Assert.Contains("side", await session.CompletionLabelsAsync("variable"));
        Assert.DoesNotContain("Hook", await session.CompletionLabelsAsync("variable"));
    }

    [Fact]
    public async Task HoverOverAnAnnotationShowsItsDocumentationAndWhatItExpandsTo()
    {
        await using var session = await Start("@{|annotation|}Hook(\"Tick\")\nfunction onTick(): nil\nend\n");
        await session.OpenAsync("src/main.neb");

        var hover = await session.HoverAsync("annotation");

        Assert.Contains("@Hook(name: string)", hover);
        Assert.Contains("Applies to functions", hover);
        Assert.Contains("Subscribes the function to an event", hover);
        Assert.Contains("subscribe(\"Tick\", onTick)", hover);
    }

    [Fact]
    public async Task GoingToTheDefinitionOfAnAnnotationOpensItsFile()
    {
        await using var session = await Start("@{|annotation|}Hook(\"Tick\")\nfunction onTick(): nil\nend\n");
        await session.OpenAsync("src/main.neb");

        var location = Assert.Single(await session.DefinitionAsync("annotation"));

        Assert.EndsWith("Hook.neb", location.Path);
    }

    [Fact]
    public async Task ArgumentsOfAnAnnotationAreCompletedAndExplained()
    {
        await using var session = await Start("@Hook({|argument|})\nfunction onTick(): nil\nend\n");
        await session.OpenAsync("src/main.neb");

        Assert.Contains("name", await session.CompletionLabelsAsync("argument"));

        var help = await session.SignatureHelpAsync("argument");
        Assert.Equal("@Hook(name: string)", Assert.Single(help!.Signatures).Label);
    }

    [Fact]
    public async Task TheSidesOfSideAreCompleted()
    {
        await using var session = await Start("@side(server, {|side|})\nfunction onTick(): nil\nend\n");
        await session.OpenAsync("src/main.neb");

        var labels = await session.CompletionLabelsAsync("side");

        Assert.Contains("client", labels);
        Assert.DoesNotContain("server", labels);
    }
}
