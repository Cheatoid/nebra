using Nebra.Configuration;
using Nebra.LPS.Tests.Harness;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nebra.LPS.Tests.Features;

public sealed class BuildProgressTests
{
    [Fact]
    public async Task TheFirstBuildReportsItsProgressUntilItEnds()
    {
        await using var session = await LspSession.StartAsync(("src/main.neb", "print(1)\n"));
        await session.OpenAsync("src/main.neb");

        var reports = await WaitForEnd(session);

        var begin = Assert.IsType<WorkDoneProgressBegin>(reports[0]);
        Assert.Equal("Loading lps-test", begin.Message);
        Assert.Contains(reports, report => report is WorkDoneProgressReport { Message: "Loading lps-test: infer types" });
    }

    [Fact]
    public void ACancelledBuildStopsBeforeItsNextPass()
    {
        var root = Directory.CreateTempSubdirectory("nebra-cancel").FullName;
        var path = Path.Combine(root, "main.neb");
        var config = new Config { ProjectRoot = root };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => WorkspaceSnapshot.Build(1, config,
            [new SourceDocument(path, "print(1)\n")], new Dictionary<string, string>(),
            cancellationToken: cancellation.Token));
    }

    private static async Task<IReadOnlyList<WorkDoneProgress>> WaitForEnd(LspSession session)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var reports = session.Progress.Reports;
            if (reports.Any(report => report is WorkDoneProgressEnd)) return reports;
            await Task.Delay(50);
        }

        throw new TimeoutException("The first build never reported that it ended.");
    }
}
