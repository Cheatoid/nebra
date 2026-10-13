using System.Collections.Concurrent;
using Antlr4.Runtime;

namespace Nebra.Compiler;

/// <summary>
/// Parse trees keyed by the source text they were parsed from. A library is loaded into every
/// package of a build, and a long-running process such as the language server compiles the same
/// unchanged files on every edit; with this each text is parsed once. A tree is only read after
/// parsing - every use lowers it into fresh IR - so no two packages share nodes the later passes
/// mutate.
/// </summary>
internal static class ParseTreeCache
{
    private const int Capacity = 512;

    private sealed record Entry(NebraParser.ScriptContext Tree, bool HasErrors);

    private static readonly ConcurrentDictionary<string, Entry> Trees = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the parse tree of <paramref name="source"/>, ignoring syntax errors. For sources a
    /// caller loads without reporting their errors, such as declaration libraries.
    /// </summary>
    public static NebraParser.ScriptContext Parse(string source)
    {
        return GetOrParse(source).Tree;
    }

    /// <summary>
    /// Returns the cached tree of <paramref name="source"/> when it parsed without a single
    /// syntax error. A source with errors has to be parsed again with listeners attached so its
    /// errors are reported, which this cannot replay.
    /// </summary>
    public static bool TryGetClean(string source, out NebraParser.ScriptContext tree)
    {
        if (Trees.TryGetValue(source, out var entry) && !entry.HasErrors)
        {
            tree = entry.Tree;
            return true;
        }

        tree = null!;
        return false;
    }

    /// <summary>Records a tree a caller parsed itself, together with whether it had syntax errors.</summary>
    public static void Add(string source, NebraParser.ScriptContext tree, bool hasErrors)
    {
        Store(source, new Entry(tree, hasErrors));
    }

    private static Entry GetOrParse(string source)
    {
        if (Trees.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var errors = new CountingErrorListener();
        var lexer = new NebraLexer(new AntlrInputStream(source));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);
        var parser = new NebraParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        var entry = new Entry(parser.script(), errors.Count > 0);
        Store(source, entry);
        return entry;
    }

    private static void Store(string source, Entry entry)
    {
        if (Trees.Count >= Capacity)
        {
            Trees.Clear();
        }

        Trees[source] = entry;
    }

    private sealed class CountingErrorListener : IAntlrErrorListener<int>, IAntlrErrorListener<IToken>
    {
        public int Count { get; private set; }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol, int line,
            int charPositionInLine, string msg, RecognitionException e)
        {
            Count++;
        }

        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line,
            int charPositionInLine, string msg, RecognitionException e)
        {
            Count++;
        }
    }
}
