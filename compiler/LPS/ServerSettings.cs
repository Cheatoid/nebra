using Newtonsoft.Json.Linq;

namespace Nebra.LPS;

/// <summary>
/// The settings an editor can change for the language server, read from its <c>nebra</c>
/// configuration section: which inlay hints to show, how long diagnostics wait for typing to
/// pause, and whether hovers tell which side code runs on.
/// </summary>
public sealed record ServerSettings
{
    public bool InlayTypeHints { get; init; } = true;
    public bool InlayParameterHints { get; init; } = true;
    public TimeSpan DiagnosticsDelay { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>Whether a hover tells which side its declaration runs on; off when <c>sideMarkers</c> is <c>"off"</c>.</summary>
    public bool SideInHover { get; init; } = true;

    /// <summary>Reads a <c>nebra</c> section, keeping the default for anything missing or malformed.</summary>
    public static ServerSettings From(JToken? section)
    {
        var defaults = new ServerSettings();
        if (section is not JObject nebra) return defaults;

        var delay = nebra.SelectToken("diagnostics.delay");
        return new ServerSettings
        {
            InlayTypeHints = ReadBool(nebra.SelectToken("inlayHints.types"), defaults.InlayTypeHints),
            InlayParameterHints = ReadBool(nebra.SelectToken("inlayHints.parameterNames"), defaults.InlayParameterHints),
            DiagnosticsDelay = delay is { Type: JTokenType.Integer or JTokenType.Float } && delay.Value<double>() >= 0
                ? TimeSpan.FromMilliseconds(delay.Value<double>())
                : defaults.DiagnosticsDelay,
            SideInHover = nebra.SelectToken("sideMarkers")?.Type != JTokenType.String || nebra.Value<string>("sideMarkers") != "off"
        };
    }

    private static bool ReadBool(JToken? token, bool fallback)
    {
        return token?.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;
    }
}
