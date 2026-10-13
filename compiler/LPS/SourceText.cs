using Nebra.Diagnostics;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nebra.LPS;

/// <summary>
/// Positions and slices over a file's text. Spans count lines and columns from one with an
/// exclusive end column; LSP positions count both from zero.
/// </summary>
public static class SourceText
{
    public static int Offset(string text, int line, int column)
    {
        var offset = 0;
        for (var current = 1; current < line; current++)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0) return text.Length;
            offset = next + 1;
        }

        return Math.Min(offset + column - 1, text.Length);
    }

    public static string Slice(string text, TextSpan span)
    {
        var start = Offset(text, span.StartLn, span.StartCol);
        var end = Offset(text, span.EndLn, span.EndCol);
        return end > start ? text[start..end] : string.Empty;
    }

    /// <summary>The zero-based position of the character <paramref name="offset"/> characters into <paramref name="text"/>.</summary>
    public static Position PositionAt(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < offset && index < text.Length; index++)
        {
            if (text[index] != '\n') continue;
            line++;
            lineStart = index + 1;
        }

        return new Position(line, offset - lineStart);
    }

    /// <summary>The leading whitespace of the zero-based <paramref name="line"/>.</summary>
    public static string Indent(string text, int line)
    {
        var lines = text.Split('\n');
        if (line < 0 || line >= lines.Length) return string.Empty;
        var content = lines[line];
        return content[..(content.Length - content.TrimStart(' ', '\t').Length)];
    }

    public static TextEdit Insert(Position position, string newText)
    {
        return new TextEdit { Range = new Range(position, position), NewText = newText };
    }

    public static TextEdit Replace(TextSpan span, string newText)
    {
        return new TextEdit { Range = NebraWorkspace.SpanToRange(span), NewText = newText };
    }

    public static bool Contains(TextSpan span, Position position)
    {
        return SourceRegions.Contains(span, position.Line + 1, position.Character + 1);
    }

    /// <summary>
    /// The position of the first occurrence of <paramref name="word"/> as a whole word inside
    /// <paramref name="span"/>, or null when it does not appear there.
    /// </summary>
    public static Position? FindWord(string text, TextSpan span, string word)
    {
        var start = Offset(text, span.StartLn, span.StartCol);
        var end = Offset(text, span.EndLn, span.EndCol);

        for (var index = text.IndexOf(word, start, StringComparison.Ordinal);
             index >= 0 && index + word.Length <= end;
             index = text.IndexOf(word, index + 1, StringComparison.Ordinal))
        {
            var before = index == 0 || !IsIdentifier(text[index - 1]);
            var after = index + word.Length >= text.Length || !IsIdentifier(text[index + word.Length]);
            if (before && after) return PositionAt(text, index);
        }

        return null;
    }

    private static bool IsIdentifier(char character) => char.IsLetterOrDigit(character) || character == '_';
}
