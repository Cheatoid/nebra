namespace Nebra.LPS;

/// <summary>
/// Where the cursor sits among the strings of a line: inside a string being typed, or in the code
/// between the braces of an interpolated string, <c>`Hello, {name}`</c>, which is code again.
/// </summary>
public sealed record StringAtCursor(int? OpenStart, bool InInterpolation)
{
    /// <summary>The strings of <paramref name="lineText"/> before the 0-based <paramref name="cursor"/>.</summary>
    public static StringAtCursor Find(string lineText, int cursor)
    {
        int? open = null;
        var interpolations = new Stack<(int Backtick, int Depth)>();
        var end = Math.Min(cursor, lineText.Length);
        for (var i = 0; i < end; i++)
        {
            var character = lineText[i];
            if (open is { } quote)
            {
                if (character == '\\')
                {
                    i++;
                    continue;
                }

                if (lineText[quote] == '`' && character == '{')
                {
                    interpolations.Push((quote, 0));
                    open = null;
                    continue;
                }

                if (character == lineText[quote]) open = null;
                continue;
            }

            if (character is '"' or '\'' or '`')
            {
                open = i;
                continue;
            }

            if (interpolations.Count == 0) continue;

            var (backtick, depth) = interpolations.Pop();
            if (character == '{') interpolations.Push((backtick, depth + 1));
            else if (character != '}') interpolations.Push((backtick, depth));
            else if (depth > 0) interpolations.Push((backtick, depth - 1));
            else open = backtick;
        }

        return new StringAtCursor(open, open == null && interpolations.Count > 0);
    }

    /// <summary>The offset of the quote opening a string left open before <paramref name="cursor"/>, or <c>null</c>.</summary>
    public static int? OpenStringStart(string lineText, int cursor) => Find(lineText, cursor).OpenStart;
}
