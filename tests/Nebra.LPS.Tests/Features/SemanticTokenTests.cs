using Nebra.LPS.Tests.Harness;

namespace Nebra.LPS.Tests.Features;

public sealed class SemanticTokenTests
{
    private const int Keyword = 0;
    private const int Variable = 4;

    private const string Source =
        "sealed class C\nend\n\nlocal key: keyof { a: number } = \"a\"\n\ntry\n    print(key)\ncatch\nfinally\nend\n\nlocal promise = { value = 1 }\nprint(promise.catch, promise:finally(), C)\n";

    /// <summary>The token type of each token, by its text and the order it appears in.</summary>
    private static async Task<List<(string Text, int Type)>> TokensAsync()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Source));
        await session.OpenAsync("src/main.neb");
        var tokens = await session.SemanticTokensAsync("src/main.neb");
        Assert.NotNull(tokens);

        var lines = Source.Split('\n');
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

    [Theory]
    [InlineData("sealed")]
    [InlineData("keyof")]
    [InlineData("try")]
    [InlineData("catch")]
    [InlineData("finally")]
    public async Task AKeywordIsHighlightedAsOne(string keyword)
    {
        var tokens = await TokensAsync();

        Assert.Equal(Keyword, tokens.First(token => token.Text == keyword).Type);
    }

    [Fact]
    public async Task AKeywordUsedAsAMemberNameIsAPlainName()
    {
        var tokens = await TokensAsync();

        Assert.Equal(Variable, tokens.Last(token => token.Text == "catch").Type);
        Assert.Equal(Variable, tokens.Last(token => token.Text == "finally").Type);
    }
}
