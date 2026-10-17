using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nebra.Configuration;

namespace Nebra.PackageManager;

/// <summary>
/// Sends requests to the package registry: publishing and unpublishing packages and managing the
/// owners of a scope. A request is an issue in the registry repository, which its workflow checks
/// and carries out; GitHub tells it who the request comes from. With a GitHub token the issue is
/// opened directly and its answer awaited, without one it is opened in the browser.
/// </summary>
public static class RegistryPublisher
{
    private const string Marker = "<!-- nebra-registry-request -->";
    private static readonly HttpClient Http = CreateClient();

    private static string RegistryRepository =>
        Environment.GetEnvironmentVariable("NEBRA_REGISTRY_REPO") is { Length: > 0 } repository ? repository : "nebra-lang/pm-registry";

    /// <summary><c>nebra publish</c>: publishes the project in the current directory.</summary>
    public static async Task<int> PublishAsync(string? scope, string? description)
    {
        var project = Environment.CurrentDirectory;
        var config = Config.LoadFromFile(Path.Combine(project, "nebra.toml"));
        if (config?.Name is not { Length: > 0 } name)
        {
            await Console.Error.WriteLineAsync("error: publishing needs a nebra.toml with a name in the current directory.");
            return 1;
        }

        var repository = await RepositoryOfAsync(project);
        if (repository == null) return 1;

        var token = await TokenAsync();
        var package = await PackageNameAsync(name, scope, token);
        if (package == null) return 1;

        var request = new Dictionary<string, string> { ["action"] = "publish", ["package"] = package, ["repo"] = repository };
        if (!string.IsNullOrWhiteSpace(description)) request["description"] = description;
        return await SendAsync($"publish {package}", request, token);
    }

    /// <summary><c>nebra unpublish [package]</c>: removes a package, by default the current project's.</summary>
    public static async Task<int> UnpublishAsync(string? package, string? scope)
    {
        var token = await TokenAsync();
        if (package == null)
        {
            var name = Config.LoadFromFile(Path.Combine(Environment.CurrentDirectory, "nebra.toml"))?.Name;
            if (string.IsNullOrEmpty(name))
            {
                await Console.Error.WriteLineAsync("error: name the package to unpublish, such as `nebra unpublish @scope/name`.");
                return 1;
            }

            package = await PackageNameAsync(name, scope, token);
            if (package == null) return 1;
        }

        return await SendAsync($"unpublish {package}", new Dictionary<string, string> { ["action"] = "unpublish", ["package"] = package }, token);
    }

    /// <summary><c>nebra scope add-owner|remove-owner &lt;scope&gt; &lt;user&gt;</c>.</summary>
    public static async Task<int> ChangeOwnerAsync(bool adding, string scope, string user)
    {
        var action = adding ? "add-owner" : "remove-owner";
        var request = new Dictionary<string, string> { ["action"] = action, ["scope"] = scope.TrimStart('@'), ["user"] = user };
        return await SendAsync($"{action} @{scope.TrimStart('@')} {user}", request, await TokenAsync());
    }

    private static async Task<int> SendAsync(string title, Dictionary<string, string> request, string? token)
    {
        var body = $"{Marker}\n```json\n{JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true })}\n```\n";
        if (token == null) return OpenInBrowser(title, body);

        var created = await PostAsync(token, $"/repos/{RegistryRepository}/issues", new { title, body });
        if (created == null) return 1;

        var number = created.Value.GetProperty("number").GetInt32();
        Console.WriteLine($"Sent request #{number} to {RegistryRepository}, waiting for its answer...");
        return await AwaitAnswerAsync(token, number);
    }

    /// <summary>
    /// Waits for the registry's workflow to close the request and prints the answer it left.
    /// </summary>
    private static async Task<int> AwaitAnswerAsync(string token, int number)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            var issue = await GetAsync(token, $"/repos/{RegistryRepository}/issues/{number}");
            if (issue?.GetProperty("state").GetString() != "closed") continue;

            var comments = await GetAsync(token, $"/repos/{RegistryRepository}/issues/{number}/comments");
            var answer = comments is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0
                ? list[list.GetArrayLength() - 1].GetProperty("body").GetString() ?? ""
                : "";
            Console.WriteLine(answer);
            return answer.StartsWith("✅") ? 0 : 1;
        }

        Console.WriteLine($"No answer yet; see https://github.com/{RegistryRepository}/issues/{number}");
        return 1;
    }

    private static int OpenInBrowser(string title, string body)
    {
        var url = $"https://github.com/{RegistryRepository}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        Console.WriteLine("No GitHub token found (GITHUB_TOKEN, GH_TOKEN or `gh auth login`).");
        Console.WriteLine("Submit the request in your browser instead:");
        Console.WriteLine(url);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Console.WriteLine("(the browser could not be opened; copy the link above)");
        }

        return 0;
    }

    /// <summary>
    /// The registry name of a package: the manifest's name when it is scoped, else the name under
    /// the given scope or, without one, under the publisher's GitHub account.
    /// </summary>
    private static async Task<string?> PackageNameAsync(string name, string? scope, string? token)
    {
        if (name.StartsWith('@')) return name.ToLowerInvariant();

        scope ??= token != null ? (await GetAsync(token, "/user"))?.GetProperty("login").GetString() : null;
        if (string.IsNullOrEmpty(scope))
        {
            await Console.Error.WriteLineAsync("error: name the scope with --scope <scope>, or log in with `gh auth login` to use your account's.");
            return null;
        }

        return $"@{scope.TrimStart('@')}/{name}".ToLowerInvariant();
    }

    /// <summary>
    /// The GitHub repository a project is published from: the <c>origin</c> remote of the git
    /// repository whose root the project is.
    /// </summary>
    private static async Task<string?> RepositoryOfAsync(string project)
    {
        var (rootCode, root, _) = await GitRunner.RunAsync(project, "rev-parse", "--show-toplevel");
        if (rootCode != 0 || Path.GetFullPath(root.Trim()) != Path.GetFullPath(project))
        {
            await Console.Error.WriteLineAsync("error: a package is published from the root of its git repository.");
            return null;
        }

        var (remoteCode, remote, _) = await GitRunner.RunAsync(project, "remote", "get-url", "origin");
        var match = Regex.Match(remote.Trim(), @"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?/?$");
        if (remoteCode != 0 || !match.Success)
        {
            await Console.Error.WriteLineAsync("error: the repository's origin has to be a GitHub repository.");
            return null;
        }

        return $"https://github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}";
    }

    private static async Task<string?> TokenAsync()
    {
        foreach (var variable in new[] { "GITHUB_TOKEN", "GH_TOKEN" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } token) return token;
        }

        try
        {
            var start = new ProcessStartInfo("gh", "auth token") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start);
            if (process == null) return null;
            var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<JsonElement?> GetAsync(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement?> PostAsync(string token, string path, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode) return JsonDocument.Parse(text).RootElement.Clone();

        await Console.Error.WriteLineAsync($"error: GitHub refused the request ({(int)response.StatusCode}): {text}");
        return null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("nebra-cli");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
