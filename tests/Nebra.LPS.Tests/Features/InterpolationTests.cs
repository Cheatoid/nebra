using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class InterpolationTests
{
    private const int Keyword = 0;
    private const int Number = 2;
    private const int Variable = 4;
    private const int Function = 5;

    private const string Prelude =
        "class User\n    name: string = \"\"\n    age: number = 0\nend\n\nlocal user = new User()\nlocal count = 1\n\nlocal function greet(): string\n    return \"hi\"\nend\n\n";

    private static async Task<List<(string Text, int Type)>> TokensAsync(string source)
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", source));
        await session.OpenAsync("src/main.neb");
        var tokens = await session.SemanticTokensAsync("src/main.neb");
        Assert.NotNull(tokens);

        var lines = source.Split('\n');
        var result = new List<(string, int)>();
        var line = 0;
        var column = 0;
        for (var i = 0; i < tokens.Data.Length; i += 5)
        {
            line += tokens.Data[i];
            column = tokens.Data[i] == 0 ? column + tokens.Data[i + 1] : tokens.Data[i + 1];
            result.Add((lines[line].Substring(column, tokens.Data[i + 2]), tokens.Data[i + 3]));
        }

        return result;
    }

    [Fact]
    public async Task TheCodeOfAnInterpolationIsHighlightedLikeCode()
    {
        var tokens = await TokensAsync(Prelude + "print(`Hi {greet()}, {user.name} is {user.age + 1} and {count > 0 and \"yes\" or `no {count}`}`)\n");
        var line = tokens.SkipWhile(token => token.Text != "print").ToList();

        Assert.Contains(("greet", Function), line);
        Assert.Contains(("user", Variable), line);
        Assert.Contains(("1", Number), line);
        Assert.Contains(("and", Keyword), line);
        Assert.Equal(2, line.Count(token => token == ("count", Variable)));
    }

    [Fact]
    public async Task HoverAndDefinitionFindANameInsideAnInterpolation()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Prelude + "print(`Hi {{|name|}count} and {user.{|field|}age}`)\n"));
        Assert.Empty(await session.OpenAsync("src/main.neb"));

        var hover = await session.HoverAsync("name");
        var field = await session.HoverAsync("field");
        var definition = await session.DefinitionAsync("name");

        Assert.Contains("count: number", hover);
        Assert.Contains("age: number", field);
        Assert.Equal(6, Assert.Single(definition).Line);
    }

    [Fact]
    public async Task AnErrorInsideAnInterpolationIsReportedWhereItIsWritten()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Prelude + "print(`Hi {missing}`)\n"));

        var error = Assert.Single(await session.OpenAsync("src/main.neb"));
        Assert.Equal(12, error.Range.Start.Line);
        Assert.Equal(11, error.Range.Start.Character);
    }

    private static async Task<IReadOnlyList<string>> LabelsAsync(string source)
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Prelude));
        await session.OpenAsync("src/main.neb");
        await session.ChangeAsync("src/main.neb", Prelude + source, 2);
        return await session.CompletionLabelsAsync("cursor");
    }

    [Fact]
    public async Task AnInterpolationCompletesValuesAndExpressionKeywords()
    {
        var labels = await LabelsAsync("print(`Hi {c{|cursor|}}`)\n");

        Assert.Contains("count", labels);
        Assert.Contains("greet", labels);
        Assert.Contains("not", labels);
        Assert.DoesNotContain("while", labels);
        Assert.DoesNotContain("local", labels);
    }

    [Fact]
    public async Task AnInterpolationCompletesMembers()
    {
        var labels = await LabelsAsync("print(`Hi {user.{|cursor|}}`)\n");

        Assert.Contains("name", labels);
        Assert.Contains("age", labels);
    }

    [Fact]
    public async Task TheTextOfAnInterpolatedStringCompletesNothing()
    {
        Assert.Empty(await LabelsAsync("print(`Hi {count} and wh{|cursor|}`)\n"));
    }
}
