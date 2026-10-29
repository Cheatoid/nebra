using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using NebraType = Nebra.IR.Type;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Completes the literal values a type takes, such as the names <c>keyof Events</c> stands for or
/// the members of <c>"fill" | "line"</c>, and the field names of a type as string literals. Inside
/// a string being typed, an item replaces the whole string, its quotes included.
/// </summary>
public static class LiteralCompletion
{
    /// <summary>The literal values <paramref name="type"/> takes, without repeats; empty for a type taking any value of its kind.</summary>
    public static List<LiteralValue> ValuesOf(NebraType type, TypeTable types)
    {
        return Values(type, types, 0).DistinctBy(value => value.Text).ToList();
    }

    /// <summary>The names of the fields of <paramref name="shape"/> as string literals.</summary>
    public static List<LiteralValue> FieldNamesOf(NebraType shape)
    {
        var target = shape is ParameterizedType parameterized ? parameterized.Definition : shape;
        return (NebraType.FieldNamesOf(target) ?? []).Select(LiteralValue.Of).ToList();
    }

    /// <summary>
    /// Completion items for <paramref name="values"/> at the 0-based <paramref name="position"/>
    /// of <paramref name="lineText"/>, ranked before every other item, the first one preselected.
    /// </summary>
    public static List<CompletionItem> Items(IReadOnlyList<LiteralValue> values, string detail, string lineText, Position position)
    {
        var cursor = Math.Clamp(position.Character, 0, lineText.Length);
        var range = StringRange(lineText, position.Line, cursor);

        return values.Select((value, index) => new CompletionItem
            {
                Label = value.Text,
                Kind = CompletionItemKind.Value,
                Detail = detail,
                SortText = $"0_{index:D4}",
                FilterText = value.Text,
                Preselect = index == 0,
                TextEdit = range != null ? new TextEditOrInsertReplaceEdit(new TextEdit { Range = range, NewText = value.Text }) : null,
            })
            .ToList();
    }

    /// <summary>The range of the string left open before the cursor, its closing quote included when it follows the cursor.</summary>
    private static Range? StringRange(string lineText, int line, int cursor)
    {
        if (StringAtCursor.OpenStringStart(lineText, cursor) is not { } quote) return null;

        var end = cursor < lineText.Length && lineText[cursor] == lineText[quote] ? cursor + 1 : cursor;
        return new Range(new Position(line, quote), new Position(line, end));
    }

    private static IEnumerable<LiteralValue> Values(NebraType type, TypeTable types, int depth)
    {
        if (depth > 8) return [];

        return type switch
        {
            LiteralType literal => [literal.Value],
            UnionType union => union.Types.SelectMany(member => Values(member, types, depth + 1)),
            KeyOfType keyOf => FieldNamesOf(keyOf.Target),
            TypeParameterType { ExtendsBound: { } bound } when types.GetByID(bound, out var constraint) => Values(constraint, types, depth + 1),
            _ => [],
        };
    }
}
