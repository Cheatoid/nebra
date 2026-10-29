using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class TypeCompletionTests
{
    private const string Prelude =
        "class User\n    id: number = 0\n    name: string = \"\"\n    email: string = \"\"\nend\n\ninterface Events\n    \"user:login\": (User) -> nil\n    tick: (number) -> nil\nend\n\nlocal function on<K extends keyof Events>(name: K, callback: Events[K]): nil\nend\n\nlocal function draw(mode: \"fill\" | \"line\", size: number): nil\nend\n\nlocal count = 1\n\n";

    private static async Task<List<CompletionItem>> ItemsAsync(string source)
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Prelude));
        await session.OpenAsync("src/main.neb");
        await session.ChangeAsync("src/main.neb", Prelude + source, 2);
        var items = await session.CompletionItemsAsync("cursor");
        return items.OrderBy(item => item.SortText ?? item.Label, StringComparer.Ordinal).ToList();
    }

    [Theory]
    [InlineData("local a: {|cursor|}")]
    [InlineData("local a: Partial<{|cursor|}")]
    [InlineData("local a: Pick<User, \"name\">, b: Partial<{|cursor|}")]
    [InlineData("local a: number | {|cursor|}")]
    [InlineData("local a: (number, {|cursor|}) -> nil")]
    [InlineData("local function f(): {|cursor|}")]
    [InlineData("local a: (number) -> {|cursor|}")]
    [InlineData("local a: keyof {|cursor|}")]
    [InlineData("interface Admin extends {|cursor|}")]
    [InlineData("print(count as {|cursor|})")]
    public async Task ATypePositionOffersOnlyTypes(string source)
    {
        var labels = (await ItemsAsync(source)).Select(item => item.Label).ToList();

        Assert.Contains("User", labels);
        Assert.Contains("Partial", labels);
        Assert.Contains("keyof", labels);
        Assert.DoesNotContain("count", labels);
        Assert.DoesNotContain("while", labels);
    }

    [Theory]
    [InlineData("if count < {|cursor|}")]
    [InlineData("print(count, {|cursor|})")]
    [InlineData("local function f(a: number, {|cursor|}")]
    public async Task AValuePositionIsNoTypePosition(string source)
    {
        var labels = (await ItemsAsync(source)).Select(item => item.Label).ToList();

        Assert.Contains("count", labels);
    }

    [Fact]
    public async Task AGenericTypeCompletesWithItsTypeArgumentList()
    {
        var partial = (await ItemsAsync("local a: {|cursor|}")).Single(item => item.Label == "Partial");

        Assert.Equal("Partial<$0>", partial.InsertText);
        Assert.Equal(CompletionItemKind.Interface, partial.Kind);
    }

    [Theory]
    [InlineData("local a: Pick<User, {|cursor|}")]
    [InlineData("local a: Omit<User, \"id\" | {|cursor|}")]
    [InlineData("local a: User[{|cursor|}")]
    public async Task TheKeysOfAUtilityTypeAreTheFieldNamesOfItsTarget(string source)
    {
        var items = await ItemsAsync(source);

        Assert.Equal(["\"id\"", "\"name\"", "\"email\""], items.Select(item => item.Label));
    }

    [Fact]
    public async Task AKeyTypedInsideAStringReplacesTheWholeString()
    {
        var items = await ItemsAsync("local a: Pick<User, \"na{|cursor|}\">");

        var edit = items.Single(item => item.Label == "\"name\"").TextEdit!.TextEdit!;
        Assert.Equal("\"name\"", edit.NewText);
        Assert.Equal(edit.Range.Start.Line, edit.Range.End.Line);
        Assert.Equal(4, edit.Range.End.Character - edit.Range.Start.Character);
    }

    [Theory]
    [InlineData("on({|cursor|})", new[] { "\"user:login\"", "\"tick\"" })]
    [InlineData("on(\"{|cursor|}\")", new[] { "\"user:login\"", "\"tick\"" })]
    [InlineData("draw({|cursor|})", new[] { "\"fill\"", "\"line\"" })]
    [InlineData("local mode: \"fast\" | \"slow\" = {|cursor|}", new[] { "\"fast\"", "\"slow\"" })]
    public async Task AnExpectedLiteralTypeOffersItsValuesFirst(string source, string[] values)
    {
        var items = await ItemsAsync(source);

        Assert.Equal(values, items.Take(values.Length).Select(item => item.Label));
        Assert.True(items[0].Preselect);
    }

    [Fact]
    public async Task InsideAStringOnlyTheExpectedLiteralsAreOffered()
    {
        var items = await ItemsAsync("draw(\"{|cursor|}\")");

        Assert.Equal(["\"fill\"", "\"line\""], items.Select(item => item.Label));
    }

    [Fact]
    public async Task TheUtilityTypesAreDeclaredAndDocumented()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Prelude + "local draft: {|type|}Partial<User> = {}\nprint(draft)\n"));
        var diagnostics = await session.OpenAsync("src/main.neb");
        var hover = await session.HoverAsync("type");

        Assert.Empty(diagnostics);
        Assert.NotNull(hover);
        Assert.Contains("type Partial<T>", hover);
        Assert.Contains("each made optional", hover);
    }
}
