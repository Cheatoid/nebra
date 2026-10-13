using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Harness;

/// <summary>
/// Records the work done progress the server reports: it accepts every progress token the server
/// asks to create and keeps what is then reported under it, in order.
/// </summary>
public sealed class ProgressRecorder
{
    private readonly object _gate = new();
    private readonly List<WorkDoneProgress> _reports = [];
    private ILanguageClient? _client;

    public void Attach(ILanguageClient client)
    {
        _client = client;
    }

    public void Accept(WorkDoneProgressCreateParams request)
    {
        _client?.WorkDoneManager.Monitor(request.Token!).Subscribe(report =>
        {
            lock (_gate) _reports.Add(report);
        });
    }

    public IReadOnlyList<WorkDoneProgress> Reports
    {
        get
        {
            lock (_gate) return _reports.ToList();
        }
    }
}
