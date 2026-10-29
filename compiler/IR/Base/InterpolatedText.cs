namespace Nebra.IR;

/// <summary>
/// The parts of an interpolated string as written, <c>`Hello, {name}`</c>: where the code between
/// its braces starts and ends, so that code can be lexed and parsed at its place in the file.
/// </summary>
public static class InterpolatedText
{
    /// <summary>
    /// The ranges of code between the braces of <paramref name="text"/>, the string with its
    /// backticks, as offsets into it without the braces. A brace left open runs to the end.
    /// </summary>
    public static List<(int Start, int End)> CodeRanges(string text)
    {
        var ranges = new List<(int Start, int End)>();
        var i = text.StartsWith('`') ? 1 : 0;
        while (i < text.Length)
        {
            if (text[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (text[i] != '{')
            {
                i++;
                continue;
            }

            var end = ClosingBrace(text, i + 1);
            ranges.Add((i + 1, end));
            i = end + 1;
        }

        return ranges;
    }

    /// <summary>The 1-based line and 0-based column of <paramref name="offset"/> in a text starting at <paramref name="line"/> and <paramref name="column"/>.</summary>
    public static (int Line, int Column) PositionOf(string text, int offset, int line, int column)
    {
        var before = text[..Math.Min(offset, text.Length)];
        var lastNewline = before.LastIndexOf('\n');
        if (lastNewline < 0) return (line, column + offset);

        return (line + before.Count(character => character == '\n'), offset - lastNewline - 1);
    }

    private static int ClosingBrace(string text, int start)
    {
        var depth = 1;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            if (text[i] != '}') continue;

            depth--;
            if (depth == 0) return i;
        }

        return text[^1] == '`' ? text.Length - 1 : text.Length;
    }
}
