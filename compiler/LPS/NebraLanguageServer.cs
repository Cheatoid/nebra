using Nebra.LPS.Handlers;
using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Server;

namespace Nebra.LPS;

public static class NebraLanguageServer
{
    public static async Task RunAsync()
    {
        var server = await LanguageServer.From(options =>
        {
            Configure(options, new NebraWorkspace());
            options
                .WithInput(Console.OpenStandardInput())
                .WithOutput(Console.OpenStandardOutput());
        }).ConfigureAwait(false);

        await server.WaitForExit;
    }

    /// <summary>
    /// Registers every handler and the workspace on <paramref name="options"/>, leaving the
    /// transport to the caller so the same server can run over stdio or over in-memory pipes.
    /// </summary>
    public static LanguageServerOptions Configure(LanguageServerOptions options, NebraWorkspace workspace)
    {
        return options
            .WithServices(services =>
            {
                services.AddSingleton(workspace);
            })
            .WithHandler<TextDocumentSyncHandler>()
            .WithHandler<HoverHandler>()
            .WithHandler<DefinitionHandler>()
            .WithHandler<CompletionHandler>()
            .WithHandler<DocumentSymbolHandler>()
            .WithHandler<SemanticTokensHandler>()
            .WithHandler<ReferencesHandler>()
            .WithHandler<RenameHandler>()
            .WithHandler<SignatureHelpHandler>()
            .WithHandler<CodeActionHandler>()
            .WithHandler<ExecuteCompileCommandHandler>()
            .WithHandler<WatchedFilesHandler>()
            .WithHandler<ConfigurationHandler>()
            .WithHandler<CodeLensHandler>()
            .WithHandler<WorkspaceSymbolHandler>()
            .WithHandler<DocumentHighlightHandler>()
            .WithHandler<TypeDefinitionHandler>()
            .WithHandler<ImplementationHandler>()
            .WithHandler<InlayHintHandler>()
            .WithHandler<FoldingRangeHandler>()
            .WithHandler<SelectionRangeHandler>()
            .WithHandler<CallHierarchyPrepareHandler>()
            .WithHandler<CallHierarchyIncomingHandler>()
            .WithHandler<CallHierarchyOutgoingHandler>()
            .WithHandler<TypeHierarchyPrepareHandler>()
            .WithHandler<TypeHierarchySupertypesHandler>()
            .WithHandler<TypeHierarchySubtypesHandler>()
            .OnInitialize((server, request, ct) =>
            {
                var folders = request.WorkspaceFolders?.Select(folder => folder.Uri.GetFileSystemPath()).OfType<string>();
                workspace.Initialize(request.RootUri?.GetFileSystemPath(), folders);
                workspace.UpdateSettings(ServerSettings.From(request.InitializationOptions as Newtonsoft.Json.Linq.JToken));
                workspace.SetServer(server);
                return Task.CompletedTask;
            })
            .OnStarted((server, ct) =>
            {
                server.WorkspaceFolderManager.Changed.Subscribe(change => OnFolderChanged(workspace, change));
                workspace.Start();
                return Task.CompletedTask;
            });
    }

    private static void OnFolderChanged(NebraWorkspace workspace, WorkspaceFolderChange change)
    {
        var path = change.Folder.Uri.GetFileSystemPath();
        if (path == null) return;

        if (change.Event == WorkspaceFolderEvent.Add)
            workspace.AddFolder(path);
        else
            workspace.RemoveFolder(path);
    }
}
