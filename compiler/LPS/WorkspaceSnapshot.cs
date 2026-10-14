using Nebra.Compiler.Annotations;
using System.Collections.Concurrent;
using Nebra.Compiler;
using Nebra.Configuration;
using Nebra.Diagnostics;
using Nebra.IR;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Nebra.LPS;

/// <summary>A source file handed to a snapshot: where it lives and the text to compile for it.</summary>
public sealed record SourceDocument(string Path, string Text, string? Uri = null);

/// <summary>
/// The workspace compiled once, as a whole. Every source file goes through the same passes as
/// <c>nebra build</c>, with open documents contributing their editor content, so the editor and
/// the build agree. All files share one type table and one symbol ID space: a symbol or type
/// reached through one file is the same object everywhere. A snapshot never changes once built;
/// an edit leads to the next one.
/// </summary>
public sealed class WorkspaceSnapshot
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Dictionary<string, SourceDocument> _documents;
    private readonly Dictionary<string, (PackageContext Package, PreparsedFile File)> _files;
    private readonly Dictionary<NodeID, Node> _nodes;
    private readonly Dictionary<NodeID, string> _nodeFiles;
    private readonly Dictionary<SymID, (PackageContext Package, Symbol Symbol)> _symbols;
    private readonly Dictionary<string, List<Diagnostic>> _diagnostics;
    private readonly HashSet<string> _staleFiles;
    private readonly ConcurrentDictionary<string, AnalysisResult?> _results = new(PathComparer);

    /// <summary>The workspace version this snapshot was built for.</summary>
    public long Version { get; }

    /// <summary>The exception a pass threw while building, if any; the snapshot then holds what was produced before it.</summary>
    public Exception? Failure { get; }

    /// <summary>
    /// Whether this snapshot covers the whole workspace. A snapshot of a single document holds that
    /// document and what it imports, which answers questions about positions in it but not about
    /// who else uses a symbol.
    /// </summary>
    public bool IsWorkspace { get; private init; }

    /// <summary>How long each phase of building this snapshot took, in order: parsing, every pass, indexing.</summary>
    public IReadOnlyList<(string Phase, TimeSpan Duration)> Timings { get; private init; } = [];

    /// <summary>The configuration this snapshot was compiled with.</summary>
    public Config Config { get; private init; } = null!;

    /// <summary>The type table every file of this snapshot shares.</summary>
    public TypeTable Types { get; private init; } = null!;

    /// <summary>The paths of the source files compiled into this snapshot.</summary>
    public IEnumerable<string> SourcePaths => _documents.Keys;

    private WorkspaceSnapshot(long version, Exception? failure, Dictionary<string, SourceDocument> documents,
        Dictionary<string, (PackageContext Package, PreparsedFile File)> files, Dictionary<NodeID, Node> nodes,
        Dictionary<NodeID, string> nodeFiles, Dictionary<SymID, (PackageContext Package, Symbol Symbol)> symbols,
        Dictionary<string, List<Diagnostic>> diagnostics, HashSet<string> staleFiles)
    {
        Version = version;
        Failure = failure;
        _documents = documents;
        _files = files;
        _nodes = nodes;
        _nodeFiles = nodeFiles;
        _symbols = symbols;
        _diagnostics = diagnostics;
        _staleFiles = staleFiles;
    }

    /// <summary>
    /// Compiles <paramref name="documents"/> with <paramref name="config"/>. A document whose text
    /// does not parse into a program is compiled from <paramref name="lastParsed"/> instead when
    /// that holds a version of it, so its importers keep resolving while it is being edited; only
    /// its syntax errors are reported then. Cancelling <paramref name="cancellationToken"/> stops
    /// the build between two passes with an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static WorkspaceSnapshot Build(long version, Config config, IReadOnlyCollection<SourceDocument> documents,
        IReadOnlyDictionary<string, string> lastParsed, bool isWorkspace = true,
        CancellationToken cancellationToken = default, Action<string, int, int>? passStarting = null)
    {
        var timings = new List<(string, TimeSpan)>();
        var compiler = new NebraCompiler
        {
            Config = config,
            PassCompleted = (pass, duration) => timings.Add((pass, duration)),
            PassStarting = passStarting,
            CancellationToken = cancellationToken
        };
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var staleFiles = new HashSet<string>(PathComparer);
        var byPath = new Dictionary<string, SourceDocument>(PathComparer);

        foreach (var document in documents)
        {
            var path = Path.GetFullPath(document.Path);
            byPath[path] = document with { Path = path };
            compiler.AddSource(path, document.Text);

            if (compiler.Packages.ContainsKey(path) || !lastParsed.TryGetValue(path, out var previous))
            {
                continue;
            }

            compiler.AddSource(path, previous);
            if (compiler.Packages.ContainsKey(path))
            {
                staleFiles.Add(path);
            }
        }

        timings.Add(("Parse", System.Diagnostics.Stopwatch.GetElapsedTime(started)));

        Exception? failure = null;
        try
        {
            compiler.Check();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failure = exception;
        }

        started = System.Diagnostics.Stopwatch.GetTimestamp();
        var files = CollectFiles(compiler);
        var (nodes, nodeFiles) = IndexNodes(files.Values.Select(entry => entry.File));
        var builtins = compiler.Library.Files
            .Where(file => !Path.IsPathRooted(file.Filename))
            .SelectMany(file => NodeFinder.BuildNodeRegistry(file.Hir))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        var symbols = IndexSymbols(compiler);
        var diagnostics = GroupDiagnostics(compiler.Diagnostics, staleFiles);

        timings.Add(("Index", System.Diagnostics.Stopwatch.GetElapsedTime(started)));

        return new WorkspaceSnapshot(version, failure, byPath, files, nodes, nodeFiles, symbols, diagnostics, staleFiles)
        {
            Timings = timings,
            BuiltinDeclarations = builtins,
            Annotations = compiler.Cache.TryGetValue(AnnotationRegistry.CacheKey, out var registry) && registry is AnnotationRegistry annotations
                ? annotations
                : new AnnotationRegistry(),
            Expansions = compiler.Cache.TryGetValue(AnnotationExpansion.CacheKey, out var recorded) && recorded is List<AnnotationExpansion> expansions
                ? expansions
                : [],
            IsWorkspace = isWorkspace,
            Types = compiler.TypeUniverse,
            Config = config
        };
    }

    /// <summary>
    /// The diagnostics reported for <paramref name="path"/>. For a file compiled from its last
    /// parsable version, only the syntax errors of the current text are included.
    /// </summary>
    public IReadOnlyList<Diagnostic> DiagnosticsFor(string path)
    {
        return _diagnostics.TryGetValue(Path.GetFullPath(path), out var list) ? list : [];
    }

    /// <summary>Whether <paramref name="path"/> was compiled from its last parsable version rather than its current text.</summary>
    public bool IsStale(string path) => _staleFiles.Contains(Path.GetFullPath(path));

    /// <summary>
    /// The view of <paramref name="path"/> in this snapshot, or null when the file is not part of
    /// the compilation. Covers source files as well as declaration files the compiler loaded.
    /// </summary>
    public AnalysisResult? GetResult(string path)
    {
        return _results.GetOrAdd(Path.GetFullPath(path), CreateResult);
    }

    /// <summary>The views of every source file compiled into this snapshot.</summary>
    public IEnumerable<AnalysisResult> SourceResults()
    {
        foreach (var path in _documents.Keys)
        {
            var result = GetResult(path);
            if (result != null)
            {
                yield return result;
            }
        }
    }

    /// <summary>Finds a symbol by ID in whichever package declared it.</summary>
    public bool TryGetSymbol(SymID id, out Symbol symbol)
    {
        if (_symbols.TryGetValue(id, out var entry))
        {
            symbol = entry.Symbol;
            return true;
        }

        symbol = null!;
        return false;
    }

    /// <summary>Finds a node by ID together with the file it was parsed from.</summary>
    public bool TryGetNode(NodeID id, out Node node, out string file)
    {
        if (_nodes.TryGetValue(id, out node!) && _nodeFiles.TryGetValue(id, out file!))
        {
            return true;
        }

        node = null!;
        file = null!;
        return false;
    }

    /// <summary>
    /// Finds a declaration by ID, including those of built-in libraries that have no file to
    /// navigate to; for reading what a declaration says, such as its documentation.
    /// </summary>
    public bool TryGetDeclaration(NodeID id, out Node node)
    {
        return _nodes.TryGetValue(id, out node!) || BuiltinDeclarations.TryGetValue(id, out node!);
    }

    /// <summary>The annotations the build knew: the project's own and those of its dependencies.</summary>
    public AnnotationRegistry Annotations { get; private init; } = new();

    /// <summary>What every annotation applied in the build expanded to.</summary>
    public IReadOnlyList<AnnotationExpansion> Expansions { get; private init; } = [];

    /// <summary>The declarations of built-in libraries, which have no file and so no place in the node index.</summary>
    private IReadOnlyDictionary<NodeID, Node> BuiltinDeclarations { get; init; } = new Dictionary<NodeID, Node>();

    /// <summary>
    /// Returns the symbol <paramref name="id"/> ultimately stands for: the exported symbol it was
    /// imported from, or itself when it was not imported.
    /// </summary>
    public SymID Origin(SymID id)
    {
        return TryGetSymbol(id, out var symbol) && symbol.ImportedFrom != SymID.Invalid ? symbol.ImportedFrom : id;
    }

    private AnalysisResult? CreateResult(string path)
    {
        if (!_files.TryGetValue(path, out var entry))
        {
            return null;
        }

        _documents.TryGetValue(path, out var document);

        return new AnalysisResult
        {
            Uri = document?.Uri ?? DocumentUri.FromFileSystemPath(path).ToString(),
            FilePath = path,
            SourceText = document?.Text ?? entry.File.Content,
            File = entry.File,
            Package = entry.Package,
            Diagnostics = DiagnosticsFor(path),
            NodeRegistry = _nodes,
            FileMap = _nodeFiles,
            ImportedDeclarations = CollectImportedDeclarations(entry.Package),
            Snapshot = this
        };
    }

    private Dictionary<SymID, ImportedDecl> CollectImportedDeclarations(PackageContext package)
    {
        var imported = new Dictionary<SymID, ImportedDecl>();

        foreach (var (id, symbol) in package.Syms.ByID)
        {
            if (symbol.ImportedFrom == SymID.Invalid)
            {
                continue;
            }

            if (!_symbols.TryGetValue(symbol.ImportedFrom, out var origin))
            {
                continue;
            }

            var declaringNode = origin.Symbol.DeclaringNode;
            if (!_nodes.TryGetValue(declaringNode, out var node) || !_nodeFiles.TryGetValue(declaringNode, out var file))
            {
                continue;
            }

            imported[id] = new ImportedDecl(file, node.Span, node);
        }

        return imported;
    }

    /// <summary>
    /// Maps every file in the compilation to the package it was compiled in. A source file is
    /// keyed to its own package; a library file to the shared library package, or to the first
    /// package holding a copy of it when it could not be shared.
    /// </summary>
    private static Dictionary<string, (PackageContext Package, PreparsedFile File)> CollectFiles(NebraCompiler compiler)
    {
        var files = new Dictionary<string, (PackageContext Package, PreparsedFile File)>(PathComparer);

        foreach (var package in compiler.Packages.Values)
        {
            var own = package.Files.FirstOrDefault(file => PathComparer.Equals(file.Filename, package.Path));
            if (own != null)
            {
                files[Path.GetFullPath(package.Path)] = (package, own);
            }
        }

        foreach (var package in compiler.AllPackages)
        {
            foreach (var file in package.Files)
            {
                if (string.IsNullOrEmpty(file.Filename) || !Path.IsPathRooted(file.Filename))
                {
                    continue;
                }

                files.TryAdd(Path.GetFullPath(file.Filename), (package, file));
            }
        }

        return files;
    }

    private static (Dictionary<NodeID, Node>, Dictionary<NodeID, string>) IndexNodes(IEnumerable<PreparsedFile> files)
    {
        var nodes = new Dictionary<NodeID, Node>();
        var nodeFiles = new Dictionary<NodeID, string>();

        foreach (var file in files.Distinct())
        {
            var path = Path.GetFullPath(file.Filename!);
            foreach (var (id, node) in NodeFinder.BuildNodeRegistry(file.Hir))
            {
                nodes.TryAdd(id, node);
                nodeFiles.TryAdd(id, path);
            }
        }

        return (nodes, nodeFiles);
    }

    private static Dictionary<SymID, (PackageContext Package, Symbol Symbol)> IndexSymbols(NebraCompiler compiler)
    {
        var symbols = new Dictionary<SymID, (PackageContext Package, Symbol Symbol)>();

        foreach (var package in compiler.AllPackages)
        {
            foreach (var (id, symbol) in package.Syms.ByID)
            {
                symbols.TryAdd(id, (package, symbol));
            }
        }

        return symbols;
    }

    private static Dictionary<string, List<Diagnostic>> GroupDiagnostics(DiagnosticsBag bag, HashSet<string> staleFiles)
    {
        var grouped = new Dictionary<string, List<Diagnostic>>(PathComparer);

        foreach (var diagnostic in bag.Diagnostics)
        {
            var file = diagnostic.Span.File;
            if (string.IsNullOrEmpty(file) || !Path.IsPathRooted(file))
            {
                continue;
            }

            var path = Path.GetFullPath(file);
            if (staleFiles.Contains(path) && diagnostic.Category != DiagnosticCategory.Syntax)
            {
                continue;
            }

            if (!grouped.TryGetValue(path, out var list))
            {
                list = [];
                grouped[path] = list;
            }

            if (!list.Any(existing => SameDiagnostic(existing, diagnostic)))
            {
                list.Add(diagnostic);
            }
        }

        return grouped;
    }

    private static bool SameDiagnostic(Diagnostic left, Diagnostic right)
    {
        return left.Code == right.Code
            && left.Message == right.Message
            && left.Span.StartLn == right.Span.StartLn
            && left.Span.StartCol == right.Span.StartCol;
    }
}
