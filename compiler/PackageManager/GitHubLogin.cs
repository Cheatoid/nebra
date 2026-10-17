using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebra.PackageManager;

/// <summary>
/// Logs in to GitHub through the device flow of the Nebra CLI's GitHub App, whose tokens can only
/// open issues in the package registry, and keeps the token in the Nebra home, renewing it when it
/// expires.
/// </summary>
public static class GitHubLogin
{
    private const string ClientId = "Iv23liRd3IxSx3Z5TnYw";
    private static readonly HttpClient Http = CreateClient();

    private static string CredentialsFile => Path.Combine(NebraHome.Root, "credentials.json");

    /// <summary><c>nebra login</c>: asks GitHub for a code, waits until it is confirmed and stores the token.</summary>
    public static async Task<int> LoginAsync()
    {
        var device = await PostFormAsync("https://github.com/login/device/code", new() { ["client_id"] = ClientId });
        if (device == null || !device.Value.TryGetProperty("device_code", out var deviceCode))
        {
            await Console.Error.WriteLineAsync("error: GitHub did not start the login.");
            return 1;
        }

        var userCode = device.Value.GetProperty("user_code").GetString();
        var verificationUri = device.Value.GetProperty("verification_uri").GetString() ?? "https://github.com/login/device";
        Console.WriteLine($"Open {verificationUri} and enter the code {userCode}");
        OpenBrowser(verificationUri);

        var interval = device.Value.TryGetProperty("interval", out var seconds) ? seconds.GetInt32() : 5;
        var deadline = DateTime.UtcNow.AddSeconds(device.Value.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval));
            var answer = await PostFormAsync("https://github.com/login/oauth/access_token", new()
            {
                ["client_id"] = ClientId,
                ["device_code"] = deviceCode.GetString()!,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            });
            if (answer == null) continue;

            switch (answer.Value.TryGetProperty("error", out var error) ? error.GetString() : null)
            {
                case null:
                    var credentials = await StoreAsync(answer.Value);
                    Console.WriteLine($"Logged in to GitHub as {credentials.Login}.");
                    return 0;
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    await Console.Error.WriteLineAsync("error: the code expired; run `nebra login` again.");
                    return 1;
                case "access_denied":
                    await Console.Error.WriteLineAsync("error: the login was declined on GitHub.");
                    return 1;
                default:
                    await Console.Error.WriteLineAsync($"error: GitHub refused the login: {error.GetString()}");
                    return 1;
            }
        }

        await Console.Error.WriteLineAsync("error: the code expired; run `nebra login` again.");
        return 1;
    }

    /// <summary><c>nebra logout</c>: forgets the stored token.</summary>
    public static int Logout()
    {
        if (!File.Exists(CredentialsFile))
        {
            Console.WriteLine("Not logged in.");
            return 0;
        }

        File.Delete(CredentialsFile);
        Console.WriteLine("Logged out of GitHub.");
        return 0;
    }

    /// <summary><c>nebra whoami</c>: names the GitHub account the stored token belongs to.</summary>
    public static async Task<int> WhoAmIAsync()
    {
        var credentials = await CurrentAsync();
        Console.WriteLine(credentials == null ? "Not logged in; run `nebra login`." : $"Logged in to GitHub as {credentials.Login}.");
        return credentials == null ? 1 : 0;
    }

    /// <summary>The stored token, renewed first when it has expired; <c>null</c> when there is none that works.</summary>
    public static async Task<string?> TokenAsync() => (await CurrentAsync())?.AccessToken;

    private static async Task<Credentials?> CurrentAsync()
    {
        var credentials = Read();
        if (credentials == null) return null;
        if (credentials.ExpiresAt == null || credentials.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return credentials;
        if (credentials.RefreshToken == null || credentials.RefreshExpiresAt <= DateTimeOffset.UtcNow) return null;

        var renewed = await PostFormAsync("https://github.com/login/oauth/access_token", new()
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken,
        });
        if (renewed == null || renewed.Value.TryGetProperty("error", out _)) return null;
        return await StoreAsync(renewed.Value);
    }

    private static async Task<Credentials> StoreAsync(JsonElement answer)
    {
        var now = DateTimeOffset.UtcNow;
        var accessToken = answer.GetProperty("access_token").GetString()!;
        var credentials = new Credentials
        {
            AccessToken = accessToken,
            ExpiresAt = answer.TryGetProperty("expires_in", out var expires) ? now.AddSeconds(expires.GetInt32()) : null,
            RefreshToken = answer.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            RefreshExpiresAt = answer.TryGetProperty("refresh_token_expires_in", out var refreshExpires) ? now.AddSeconds(refreshExpires.GetInt32()) : null,
            Login = await LoginNameAsync(accessToken),
        };

        Directory.CreateDirectory(NebraHome.Root);
        File.Delete(CredentialsFile);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(CredentialsFile, options))
            await JsonSerializer.SerializeAsync(stream, credentials);
        return credentials;
    }

    private static Credentials? Read()
    {
        try
        {
            return File.Exists(CredentialsFile) ? JsonSerializer.Deserialize<Credentials>(File.ReadAllText(CredentialsFile)) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private static async Task<string> LoginNameAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return "";
        using var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return user.RootElement.GetProperty("login").GetString() ?? "";
    }

    private static async Task<JsonElement?> PostFormAsync(string url, Dictionary<string, string> form)
    {
        try
        {
            using var response = await Http.PostAsync(url, new FormUrlEncodedContent(form));
            var text = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            Console.WriteLine("(the browser could not be opened; open the link above)");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("nebra-cli");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private sealed class Credentials
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = "";
        [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("refresh_expires_at")] public DateTimeOffset? RefreshExpiresAt { get; init; }
        [JsonPropertyName("login")] public string Login { get; init; } = "";
    }
}
