using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;

namespace Nebra.LPS.Handlers;

/// <summary>Applies the <c>nebra</c> settings an editor pushes whenever the user changes them.</summary>
public sealed class ConfigurationHandler(NebraWorkspace workspace) : DidChangeConfigurationHandlerBase
{
    public override Task<Unit> Handle(DidChangeConfigurationParams request, CancellationToken ct)
    {
        workspace.UpdateSettings(ServerSettings.From(request.Settings?["nebra"]));
        return Unit.Task;
    }
}
