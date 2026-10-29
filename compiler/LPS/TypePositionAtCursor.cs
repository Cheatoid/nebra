namespace Nebra.LPS;

/// <summary>
/// Where a type is written at the cursor: after a <c>:</c>, <c>-&gt;</c>, <c>extends</c>,
/// <c>implements</c>, <c>keyof</c>, <c>as</c> or <c>is</c>, inside the type arguments of a generic
/// type, the parameters of a function type or a union. <see cref="KeysOf"/> names the type whose
/// field names are wanted there: the target of <c>Pick&lt;User, |&gt;</c>, <c>Omit&lt;User, |&gt;</c>
/// or <c>User[|]</c>.
/// </summary>
public sealed record TypePositionAtCursor(string? KeysOf)
{
    private static readonly TypePositionAtCursor Plain = new((string?)null);

    private static readonly HashSet<string> TypeKeywords = new(StringComparer.Ordinal) { "extends", "implements", "keyof", "as", "is" };

    private static readonly HashSet<string> KeyedUtilities = new(StringComparer.Ordinal) { "Pick", "Omit" };

    /// <summary>
    /// The type position at the 0-based <paramref name="character"/> of <paramref name="lineText"/>,
    /// or <c>null</c> where no type is written. The name or string being typed at the cursor is part
    /// of the type.
    /// </summary>
    public static TypePositionAtCursor? Find(string lineText, int character)
    {
        var cursor = Math.Clamp(character, 0, lineText.Length);
        var start = StringAtCursor.OpenStringStart(lineText, cursor) ?? WordStart(lineText, cursor);
        return Before(lineText, start);
    }

    private static int WordStart(string text, int end)
    {
        var start = end;
        while (start > 0 && IsNameChar(text[start - 1])) start--;
        return start;
    }

    /// <summary>Whether a type starts at <paramref name="position"/>, judged by the text before it.</summary>
    private static TypePositionAtCursor? Before(string text, int position)
    {
        var end = SkipBlanks(text, position);
        if (end == 0) return null;

        var last = text[end - 1];
        if (last == '>' && end >= 2 && text[end - 2] == '-') return Plain;
        if (last == ':') return end >= 2 && text[end - 2] == ':' ? null : Plain;

        if (IsNameChar(last))
        {
            var wordStart = WordStart(text, end);
            return TypeKeywords.Contains(text[wordStart..end]) ? Plain : null;
        }

        return last switch
        {
            '|' when end < 2 || text[end - 2] != '|' => Before(text, OperandStart(text, end - 1)),
            '<' => GenericArgument(text, end - 1, 0),
            '(' => Before(text, end - 1) != null ? Plain : null,
            '[' => IndexedAccess(text, end - 1),
            ',' => ListEntry(text, end - 1),
            _ => null,
        };
    }

    /// <summary>
    /// The type argument <paramref name="index"/> of the generic type whose <c>&lt;</c> sits at
    /// <paramref name="open"/>, when that type is itself written in a type position.
    /// </summary>
    private static TypePositionAtCursor? GenericArgument(string text, int open, int index)
    {
        var nameEnd = SkipBlanks(text, open);
        var nameStart = WordStart(text, nameEnd);
        if (nameStart == nameEnd || Before(text, nameStart) == null) return null;

        var name = text[nameStart..nameEnd];
        if (index != 1 || !KeyedUtilities.Contains(name)) return Plain;

        var firstArgumentEnd = DepthZeroComma(text, open + 1);
        return new TypePositionAtCursor(firstArgumentEnd > open ? text[(open + 1)..firstArgumentEnd].Trim() : null);
    }

    /// <summary>An entry after a <c>,</c>: a type argument, or a parameter of a function type.</summary>
    private static TypePositionAtCursor? ListEntry(string text, int comma)
    {
        var open = OpenBracket(text, comma);
        if (open < 0) return null;

        return text[open] switch
        {
            '<' => GenericArgument(text, open, CountDepthZeroCommas(text, open + 1, comma + 1)),
            '(' => Before(text, open) != null ? Plain : null,
            _ => null,
        };
    }

    /// <summary><c>T[</c> in a type position: the field names of <c>T</c> go there.</summary>
    private static TypePositionAtCursor? IndexedAccess(string text, int open)
    {
        var targetEnd = SkipBlanks(text, open);
        var targetStart = WordStart(text, targetEnd);
        if (targetStart == targetEnd || Before(text, targetStart) == null) return null;

        return new TypePositionAtCursor(text[targetStart..targetEnd]);
    }

    /// <summary>The start of the union member or operand ending just before <paramref name="end"/>.</summary>
    private static int OperandStart(string text, int end)
    {
        var position = SkipBlanks(text, end);
        var depth = 0;
        while (position > 0)
        {
            var character = text[position - 1];
            if (character is ')' or ']' or '}' or '>') depth++;
            else if (character is '(' or '[' or '{' or '<')
            {
                if (depth == 0) break;
                depth--;
            }
            else if (depth == 0 && !IsNameChar(character) && character is not ('"' or '\'' or '.' or '?' or '-')) break;

            position--;
        }

        return position;
    }

    /// <summary>The offset of the innermost bracket left open before <paramref name="end"/>, or -1.</summary>
    private static int OpenBracket(string text, int end)
    {
        var depth = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            switch (text[i])
            {
                case ')' or ']' or '}':
                    depth++;
                    break;
                case '>' when i == 0 || text[i - 1] != '-':
                    depth++;
                    break;
                case '(' or '[' or '{' or '<':
                    if (depth == 0) return i;
                    depth--;
                    break;
            }
        }

        return -1;
    }

    private static int DepthZeroComma(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '(' or '[' or '{' or '<':
                    depth++;
                    break;
                case '>' when i > 0 && text[i - 1] == '-':
                    break;
                case ')' or ']' or '}' or '>':
                    if (depth == 0) return i;
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
            }
        }

        return text.Length;
    }

    private static int CountDepthZeroCommas(string text, int start, int end)
    {
        var count = 0;
        for (var i = start; i < end; i = DepthZeroComma(text, i) + 1)
        {
            var comma = DepthZeroComma(text, i);
            if (comma >= end || text[comma] != ',') break;
            count++;
        }

        return count;
    }

    private static int SkipBlanks(string text, int end)
    {
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return end;
    }

    private static bool IsNameChar(char character) => char.IsLetterOrDigit(character) || character == '_';
}
