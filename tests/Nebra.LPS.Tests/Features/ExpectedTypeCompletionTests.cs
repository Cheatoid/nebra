using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class ExpectedTypeCompletionTests
{
    private const string Prelude =
        "enum Color\n    RED\n    GREEN\n    BLUE\nend\n\nlocal favorite: Color = Color.GREEN\nlocal label: string = \"x\"\n\nlocal function pick(): Color\n    return Color.BLUE\nend\n\nlocal function paint(color: Color): nil\nend\n\n";

    private static async Task<List<CompletionItem>> RankedAsync(string source)
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", Prelude + source));
        await session.OpenAsync("src/main.neb");
        var items = await session.CompletionItemsAsync("cursor");
        return items.OrderBy(item => item.SortText ?? item.Label, StringComparer.Ordinal).ToList();
    }

    [Theory]
    [InlineData("local color: Color = {|cursor|}")]
    [InlineData("local mut color: Color = Color.RED\ncolor = {|cursor|}")]
    [InlineData("paint({|cursor|})")]
    [InlineData("local function other(): Color\n    return {|cursor|}\nend\n")]
    [InlineData("print(favorite == {|cursor|})")]
    public async Task AnExpectedEnumOffersItsMembersFirst(string source)
    {
        var items = await RankedAsync(source);

        Assert.Equal(["RED", "GREEN", "BLUE"], items.Take(3).Select(item => item.Label));
        Assert.True(items[0].Preselect);
        Assert.Equal(CompletionItemKind.EnumMember, items[0].Kind);
    }

    [Theory]
    [InlineData("local name = match favorite\n    case {|cursor|}\nend\n")]
    [InlineData("match favorite\n    case {|cursor|}\nend\n")]
    public async Task AMatchOnAnEnumOffersItsMembersAfterCase(string source)
    {
        var items = await RankedAsync(source);

        Assert.Equal("RED", items[0].Label);
    }

    [Fact]
    public async Task ValuesAndFunctionsOfTheExpectedTypeComeBeforeTheRest()
    {
        var items = await RankedAsync("local color: Color = {|cursor|}");
        var labels = items.Select(item => item.Label).ToList();

        Assert.True(labels.IndexOf("favorite") < labels.IndexOf("label"));
        Assert.True(labels.IndexOf("pick") < labels.IndexOf("paint"));
    }

    [Fact]
    public async Task AMemberWhoseNameIsDeclaredIsOfferedWithItsEnum()
    {
        var items = await RankedAsync("local RED = 1\nlocal color: Color = {|cursor|}");

        Assert.Equal("Color.RED", items[0].Label);
        Assert.Equal("GREEN", items[1].Label);
    }

    [Fact]
    public async Task WithoutAnExpectedTypeNothingIsReordered()
    {
        var items = await RankedAsync("{|cursor|}");

        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.EnumMember);
    }
}
