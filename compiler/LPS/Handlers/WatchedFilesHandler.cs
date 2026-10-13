using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nebra.LPS.Handlers;

/// <summary>
/// Picks up changes made outside the editor - a checkout, a generator run, an edited
/// <c>nebra.toml</c> - which the workspace would otherwise only notice after the next edit.
/// </summary>
public sealed class WatchedFilesHandler(NebraWorkspace workspace) : DidChangeWatchedFilesHandlerBase
{
    public override Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken ct)
    {
        workspace.OnFilesChanged(request.Changes.Select(change => (change.Uri.ToString(), change.Type != FileChangeType.Deleted)));
        return Unit.Task;
    }

    protected override DidChangeWatchedFilesRegistrationOptions CreateRegistrationOptions(
        DidChangeWatchedFilesCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DidChangeWatchedFilesRegistrationOptions
        {
            Watchers = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher>(
                Watch("**/*.neb"),
                Watch("**/nebra.toml"))
        };
    }

    private static OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher Watch(string pattern)
    {
        return new OmniSharp.Extensions.LanguageServer.Protocol.Models.FileSystemWatcher
        {
            GlobPattern = pattern,
            Kind = WatchKind.Create | WatchKind.Change | WatchKind.Delete
        };
    }
}
