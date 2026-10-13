using System.IO.Pipelines;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nebra.LPS.Tests.Harness;

/// <summary>A position in a project file, addressed by its path relative to the project root.</summary>
public sealed record SourceLocation(string Path, int Line, int Character);

/// <summary>
/// A Nebra language server and an LSP client wired together over in-memory pipes, serving a
/// project written to a temporary directory. Requests travel through the real protocol stack, so
/// a test observes exactly what an editor would, without spawning a process.
/// </summary>
public sealed class LspSession : IAsyncDisposable
{
    private const string DefaultConfig =
        "name = \"lps-test\"\nversion = \"0.1.0\"\ntarget = \"5.4\"\nsource = \"src\"\noutput = \"out\"\n";

    private static readonly TimeSpan DiagnosticsTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The first analysis in a process pays for JIT compilation and for building the parser's
    /// prediction cache, which takes seconds. Paying it once up front keeps sessions that start
    /// in parallel from all doing so at the same time and running into the timeout.
    /// </summary>
    private static readonly Task Warmup = Task.Run(WarmUpAnalysisAsync);

    private readonly Dictionary<string, MarkedSource> _files;
    private readonly DiagnosticsCollector _diagnostics;
    private readonly ILanguageServer _server;

    public string Root { get; }
    public ILanguageClient Client { get; }

    /// <summary>The work done progress the server has reported so far.</summary>
    public ProgressRecorder Progress { get; }

    private LspSession(string root, Dictionary<string, MarkedSource> files, ILanguageServer server,
        ILanguageClient client, DiagnosticsCollector diagnostics, ProgressRecorder progress)
    {
        Root = root;
        Progress = progress;
        _files = files;
        _server = server;
        Client = client;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Writes <paramref name="files"/> (paths relative to the project root, marker syntax allowed)
    /// to a fresh project directory and starts a server over it. A <c>nebra.toml</c> is added
    /// unless one is supplied.
    /// </summary>
    public static Task<LspSession> StartAsync(params (string Path, string Text)[] files)
    {
        return StartAsync([], files);
    }

    /// <summary>
    /// Like <see cref="StartAsync(ValueTuple{string, string}[])"/>, but reports
    /// <paramref name="folders"/> (relative to the session root) as the editor's workspace folders
    /// instead of the root itself. No <c>nebra.toml</c> is added at the root then.
    /// </summary>
    public static async Task<LspSession> StartAsync(IReadOnlyList<string> folders, params (string Path, string Text)[] files)
    {
        await Warmup;

        var root = Directory.CreateTempSubdirectory("nebra-lps-").FullName;
        var parsed = new Dictionary<string, MarkedSource>(StringComparer.Ordinal);

        foreach (var (path, text) in files)
        {
            var source = MarkedSource.Parse(text);
            parsed[path] = source;
            WriteFile(root, path, source.Text);
        }

        if (folders.Count == 0 && !parsed.ContainsKey("nebra.toml"))
        {
            WriteFile(root, "nebra.toml", DefaultConfig);
        }

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var diagnostics = new DiagnosticsCollector();
        var progress = new ProgressRecorder();

        var serverTask = LanguageServer.From(options =>
        {
            NebraLanguageServer.Configure(options, new NebraWorkspace());
            options
                .WithInput(clientToServer.Reader)
                .WithOutput(serverToClient.Writer);
        });

        var clientTask = LanguageClient.From(options =>
        {
            options
                .WithInput(serverToClient.Reader)
                .WithOutput(clientToServer.Writer)
                .WithRootUri(DocumentUri.FromFileSystemPath(root))
                .OnPublishDiagnostics(diagnostics.Record)
                .OnWorkDoneProgressCreate(request =>
                {
                    progress.Accept(request);
                    return Task.CompletedTask;
                });

            options.ClientCapabilities.Window = new WindowClientCapabilities { WorkDoneProgress = true };

            foreach (var folder in folders)
            {
                options.WithWorkspaceFolder(DocumentUri.FromFileSystemPath(System.IO.Path.Combine(root, folder)), folder);
            }
        });

        await Task.WhenAll(serverTask, clientTask);
        progress.Attach(clientTask.Result);
        return new LspSession(root, parsed, serverTask.Result, clientTask.Result, diagnostics, progress);
    }

    public DocumentUri Uri(string path) => DocumentUri.FromFileSystemPath(FullPath(path));

    public string FullPath(string path) => System.IO.Path.Combine(Root, path);

    /// <summary>Finds the marker <paramref name="name"/> in whichever file declares it.</summary>
    public (string Path, Position Position) Marker(string name)
    {
        foreach (var (path, source) in _files)
        {
            if (source.Markers.TryGetValue(name, out var position))
            {
                return (path, position);
            }
        }

        throw new KeyNotFoundException($"No file declares the marker '{name}'.");
    }

    public SourceLocation Location(string marker)
    {
        var (path, position) = Marker(marker);
        return new SourceLocation(path, position.Line, position.Character);
    }

    public TextDocumentPositionParams At(string marker)
    {
        var (path, position) = Marker(marker);
        return new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path)),
            Position = position
        };
    }

    /// <summary>Converts a location the server returned back into a project-relative one.</summary>
    public SourceLocation ToSourceLocation(DocumentUri uri, Position position)
    {
        var full = System.IO.Path.GetFullPath(uri.GetFileSystemPath());
        var relative = System.IO.Path.GetRelativePath(Root, full).Replace('\\', '/');
        return new SourceLocation(relative, position.Line, position.Character);
    }

    /// <summary>
    /// Opens <paramref name="path"/> in the server and waits for the diagnostics it publishes
    /// in response.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> OpenAsync(string path)
    {
        var uri = Uri(path);
        var published = _diagnostics.WaitForNext(uri);

        Client.TextDocument.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "nebra",
                Version = 1,
                Text = _files[path].Text
            }
        });

        return await published.WaitAsync(DiagnosticsTimeout);
    }

    /// <summary>
    /// Returns a task that completes with the next diagnostics the server publishes for
    /// <paramref name="path"/>. Call it before the action expected to trigger them.
    /// </summary>
    public Task<IReadOnlyList<Diagnostic>> NextDiagnostics(string path)
    {
        return _diagnostics.WaitForNext(Uri(path)).WaitAsync(DiagnosticsTimeout);
    }

    /// <summary>
    /// The diagnostics the server has published for <paramref name="path"/>, waiting for the
    /// first publication if there has been none - including for files that were never opened.
    /// </summary>
    public Task<IReadOnlyList<Diagnostic>> PublishedDiagnostics(string path)
    {
        return _diagnostics.WaitForAny(Uri(path)).WaitAsync(DiagnosticsTimeout);
    }

    /// <summary>
    /// Changes files on disk behind the editor's back - writing the given text, or deleting the
    /// file when it is null - and reports it the way an editor's file watcher does.
    /// </summary>
    public void ChangeOnDisk(params (string Path, string? Text)[] changes)
    {
        var events = new List<FileEvent>();
        foreach (var (path, text) in changes)
        {
            var existed = File.Exists(FullPath(path));
            if (text == null)
            {
                File.Delete(FullPath(path));
                events.Add(new FileEvent { Uri = Uri(path), Type = FileChangeType.Deleted });
                continue;
            }

            WriteFile(Root, path, text);
            events.Add(new FileEvent { Uri = Uri(path), Type = existed ? FileChangeType.Changed : FileChangeType.Created });
        }

        Client.Workspace.DidChangeWatchedFiles(new DidChangeWatchedFilesParams { Changes = new Container<FileEvent>(events) });
    }

    /// <summary>
    /// Replaces the content of an open document without waiting for anything, so the next request
    /// arrives before the server has recompiled the workspace - the moment a user is typing in.
    /// </summary>
    public void SendChange(string path, string markedText, int version)
    {
        var source = MarkedSource.Parse(markedText);
        _files[path] = source;

        Client.TextDocument.DidChangeTextDocument(new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = Uri(path), Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = source.Text })
        });
    }

    /// <summary>
    /// Replaces the content of an open document and waits for the diagnostics published for it.
    /// </summary>
    public async Task<IReadOnlyList<Diagnostic>> ChangeAsync(string path, string markedText, int version)
    {
        var source = MarkedSource.Parse(markedText);
        _files[path] = source;

        var uri = Uri(path);
        var published = NextDiagnostics(path);

        Client.TextDocument.DidChangeTextDocument(new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = source.Text })
        });

        return await published;
    }

    public async Task<string?> HoverAsync(string marker)
    {
        var at = At(marker);
        var hover = await Client.TextDocument.RequestHover(new HoverParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return hover?.Contents.MarkupContent?.Value;
    }

    public async Task<IReadOnlyList<SourceLocation>> DefinitionAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestDefinition(new DefinitionParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result
            .Select(link => link.IsLocation
                ? ToSourceLocation(link.Location!.Uri, link.Location.Range.Start)
                : ToSourceLocation(link.LocationLink!.TargetUri, link.LocationLink.TargetSelectionRange.Start))
            .ToList();
    }

    public async Task<IReadOnlyList<SourceLocation>> TypeDefinitionAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestTypeDefinition(new TypeDefinitionParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return (result ?? new LocationOrLocationLinks())
            .Select(link => link.IsLocation
                ? ToSourceLocation(link.Location!.Uri, link.Location.Range.Start)
                : ToSourceLocation(link.LocationLink!.TargetUri, link.LocationLink.TargetSelectionRange.Start))
            .ToList();
    }

    public async Task<IReadOnlyList<SourceLocation>> ImplementationAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestImplementation(new ImplementationParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return (result ?? new LocationOrLocationLinks())
            .Select(link => link.IsLocation
                ? ToSourceLocation(link.Location!.Uri, link.Location.Range.Start)
                : ToSourceLocation(link.LocationLink!.TargetUri, link.LocationLink.TargetSelectionRange.Start))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ToList();
    }

    public async Task<IReadOnlyList<FoldingRange>> FoldingRangesAsync(string path)
    {
        var result = await Client.TextDocument.RequestFoldingRange(new FoldingRangeRequestParam
        {
            TextDocument = new TextDocumentIdentifier(Uri(path))
        });

        return result?.ToList() ?? [];
    }

    /// <summary>The ranges a selection grows through from <paramref name="marker"/>, innermost first.</summary>
    public async Task<IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>> SelectionChainAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestSelectionRange(new SelectionRangeParams
        {
            TextDocument = at.TextDocument,
            Positions = new Container<Position>(at.Position)
        });

        var chain = new List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>();
        for (var current = result?.FirstOrDefault(); current != null; current = current.Parent)
        {
            chain.Add(current.Range);
        }

        return chain;
    }

    public async Task<CallHierarchyItem?> PrepareCallHierarchyAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestCallHierarchyPrepare(new CallHierarchyPrepareParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result?.SingleOrDefault();
    }

    /// <summary>The callers of <paramref name="item"/> by name, each with the start of every call it makes.</summary>
    public async Task<IReadOnlyList<(string Caller, IReadOnlyList<SourceLocation> Calls)>> IncomingCallsAsync(CallHierarchyItem item)
    {
        var result = await Client.TextDocument.RequestCallHierarchyIncoming(new CallHierarchyIncomingCallsParams { Item = item });
        return (result ?? [])
            .Select(call => (call.From.Name, (IReadOnlyList<SourceLocation>)call.FromRanges
                .Select(range => ToSourceLocation(call.From.Uri, range.Start)).ToList()))
            .OrderBy(call => call.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The callees of <paramref name="item"/> by name.</summary>
    public async Task<IReadOnlyList<string>> OutgoingCallsAsync(CallHierarchyItem item)
    {
        var result = await Client.TextDocument.RequestCallHierarchyOutgoing(new CallHierarchyOutgoingCallsParams { Item = item });
        return (result ?? []).Select(call => call.To.Name).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<TypeHierarchyItem?> PrepareTypeHierarchyAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestTypeHierarchyPrepare(new TypeHierarchyPrepareParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result?.SingleOrDefault();
    }

    public async Task<IReadOnlyList<string>> SupertypesAsync(TypeHierarchyItem item)
    {
        var result = await Client.TextDocument.RequestTypeHierarchySupertypes(new TypeHierarchySupertypesParams { Item = item });
        return (result ?? []).Select(type => type.Name).Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<string>> SubtypesAsync(TypeHierarchyItem item)
    {
        var result = await Client.TextDocument.RequestTypeHierarchySubtypes(new TypeHierarchySubtypesParams { Item = item });
        return (result ?? []).Select(type => type.Name).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Every inlay hint in <paramref name="path"/>, as position and label.</summary>
    public async Task<IReadOnlyList<(SourceLocation Location, string Label)>> InlayHintsAsync(string path)
    {
        var lines = _files[path].Text.Split('\n').Length;
        var result = await Client.TextDocument.RequestInlayHints(new InlayHintParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path)),
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, lines, 0)
        });

        return (result ?? new InlayHintContainer())
            .Select(hint => (new SourceLocation(path, hint.Position.Line, hint.Position.Character), hint.Label.String ?? string.Empty))
            .ToList();
    }

    /// <summary>The highlights for the symbol at <paramref name="marker"/>, sorted by position.</summary>
    public async Task<IReadOnlyList<(SourceLocation Location, DocumentHighlightKind Kind)>> HighlightsAsync(string marker)
    {
        var at = At(marker);
        var (path, _) = Marker(marker);
        var result = await Client.TextDocument.RequestDocumentHighlight(new DocumentHighlightParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return (result ?? new DocumentHighlightContainer())
            .Select(highlight => (new SourceLocation(path, highlight.Range.Start.Line, highlight.Range.Start.Character), highlight.Kind))
            .OrderBy(highlight => highlight.Item1.Line)
            .ThenBy(highlight => highlight.Item1.Character)
            .ToList();
    }

    public async Task<IReadOnlyList<SourceLocation>> ReferencesAsync(string marker, bool includeDeclaration)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestReferences(new ReferenceParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position,
            Context = new ReferenceContext { IncludeDeclaration = includeDeclaration }
        });

        return result
            .Select(location => ToSourceLocation(location.Uri, location.Range.Start))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Character)
            .ToList();
    }

    /// <summary>
    /// Requests a rename and returns the start of every edited range, grouped by file and sorted,
    /// or an empty list when the server declined the rename.
    /// </summary>
    public async Task<IReadOnlyList<SourceLocation>> RenameAsync(string marker, string newName)
    {
        var at = At(marker);
        var edit = await Client.TextDocument.RequestRename(new RenameParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position,
            NewName = newName
        });

        if (edit?.Changes == null)
        {
            return [];
        }

        return edit.Changes
            .SelectMany(change => change.Value.Select(text => ToSourceLocation(change.Key, text.Range.Start)))
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line)
            .ThenBy(location => location.Character)
            .ToList();
    }

    public async Task<IReadOnlyList<CompletionItem>> CompletionItemsAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestCompletion(new CompletionParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result.Items.ToList();
    }

    public async Task<IReadOnlyList<string>> CompletionLabelsAsync(string marker)
    {
        var at = At(marker);
        var result = await Client.TextDocument.RequestCompletion(new CompletionParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });

        return result.Items.Select(item => item.Label).ToList();
    }

    public async Task<SignatureHelp?> SignatureHelpAsync(string marker)
    {
        var at = At(marker);
        return await Client.TextDocument.RequestSignatureHelp(new SignatureHelpParams
        {
            TextDocument = at.TextDocument,
            Position = at.Position
        });
    }

    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string path)
    {
        var result = await Client.TextDocument.RequestDocumentSymbol(new DocumentSymbolParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path))
        });

        return result
            .Where(symbol => symbol.IsDocumentSymbol)
            .Select(symbol => symbol.DocumentSymbol!)
            .ToList();
    }

    /// <summary>The workspace symbols matching <paramref name="query"/>, in the order the server ranked them.</summary>
    public async Task<IReadOnlyList<(string Name, SymbolKind Kind, string? Container, SourceLocation Location)>> WorkspaceSymbolsAsync(string query)
    {
        var result = await Client.TextDocument.RequestWorkspaceSymbols(new WorkspaceSymbolParams { Query = query });
        return (result ?? [])
            .Select(symbol => (symbol.Name, symbol.Kind, symbol.ContainerName,
                ToSourceLocation(symbol.Location.Location!.Uri, symbol.Location.Location.Range.Start)))
            .ToList();
    }

    public async Task<SemanticTokens?> SemanticTokensAsync(string path)
    {
        return await Client.TextDocument.RequestSemanticTokensFull(new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path))
        });
    }

    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(string path, IReadOnlyList<Diagnostic> diagnostics)
    {
        var range = diagnostics.Count > 0
            ? diagnostics[0].Range
            : new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 0);

        var result = await Client.TextDocument.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path)),
            Range = range,
            Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>(diagnostics) }
        });

        return result
            .Where(item => item.IsCodeAction)
            .Select(item => item.CodeAction!)
            .ToList();
    }

    /// <summary>Pushes new <c>nebra</c> settings to the server, the way an editor does when the user edits them.</summary>
    public void ChangeSettings(Newtonsoft.Json.Linq.JObject nebra)
    {
        Client.Workspace.DidChangeConfiguration(new DidChangeConfigurationParams
        {
            Settings = new Newtonsoft.Json.Linq.JObject { ["nebra"] = nebra }
        });
    }

    /// <summary>The current text of <paramref name="path"/> as the session knows it.</summary>
    public string Text(string path) => _files[path].Text;

    /// <summary>
    /// Applies the edits of <paramref name="edit"/> to the session's copy of each file and returns
    /// the resulting text per changed file, without touching the server.
    /// </summary>
    public IReadOnlyDictionary<string, string> Apply(WorkspaceEdit? edit)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (edit?.Changes == null) return changed;

        foreach (var (uri, edits) in edit.Changes)
        {
            var path = ToSourceLocation(uri, new Position(0, 0)).Path;
            var text = _files.TryGetValue(path, out var source) ? source.Text : File.ReadAllText(FullPath(path));
            changed[path] = ApplyEdits(text, edits);
        }

        return changed;
    }

    private static string ApplyEdits(string text, IEnumerable<TextEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(e => e.Range.Start.Line).ThenByDescending(e => e.Range.Start.Character))
        {
            var start = Offset(text, edit.Range.Start);
            var end = Offset(text, edit.Range.End);
            text = text[..start] + edit.NewText + text[end..];
        }

        return text;
    }

    private static int Offset(string text, Position position)
    {
        var offset = 0;
        for (var line = 0; line < position.Line; line++)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0) return text.Length;
            offset = next + 1;
        }

        return Math.Min(offset + position.Character, text.Length);
    }

    /// <summary>The code actions offered for the range between two markers, or at one marker.</summary>
    public async Task<IReadOnlyList<CodeAction>> CodeActionsAtAsync(string startMarker, string? endMarker = null,
        IReadOnlyList<Diagnostic>? diagnostics = null, params CodeActionKind[] only)
    {
        var (path, start) = Marker(startMarker);
        var end = endMarker == null ? start : Marker(endMarker).Position;
        var result = await Client.TextDocument.RequestCodeAction(new CodeActionParams
        {
            TextDocument = new TextDocumentIdentifier(Uri(path)),
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(start, end),
            Context = new CodeActionContext
            {
                Diagnostics = new Container<Diagnostic>(diagnostics ?? []),
                Only = only.Length > 0 ? new Container<CodeActionKind>(only) : null
            }
        });

        return (result ?? []).Where(item => item.IsCodeAction).Select(item => item.CodeAction!).ToList();
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        _server.Dispose();

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }

        return ValueTask.CompletedTask;
    }

    private static async Task WarmUpAnalysisAsync()
    {
        var root = Directory.CreateTempSubdirectory("nebra-lps-warmup-").FullName;
        WriteFile(root, "nebra.toml", DefaultConfig);
        WriteFile(root, "src/main.neb", "local value: number = 1\nprint(value)\n");

        var workspace = new NebraWorkspace();
        workspace.Initialize(root);
        await workspace.GetSnapshotAsync(System.IO.Path.Combine(root, "src/main.neb"));

        Directory.Delete(root, true);
    }

    private static void WriteFile(string root, string path, string text)
    {
        var full = System.IO.Path.Combine(root, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
