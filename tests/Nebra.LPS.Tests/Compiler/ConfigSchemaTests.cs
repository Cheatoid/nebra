using System.Reflection;
using System.Text.Json;
using Nebra.Configuration;

namespace Nebra.LPS.Tests.Compiler;

/// <summary>
/// The extension completes and explains <c>nebra.toml</c> from a schema written by hand. Every
/// setting the configuration reads has to be in it, or the editor would flag a valid key.
/// </summary>
public sealed class ConfigSchemaTests
{
    public static TheoryData<string> Settings()
    {
        var settings = new TheoryData<string>();
        foreach (var path in SettingsOf(typeof(Config), ""))
        {
            settings.Add(path);
        }

        return settings;
    }

    [Theory]
    [MemberData(nameof(Settings))]
    public void EverySettingIsDescribedInTheSchema(string path)
    {
        var node = Schema();
        foreach (var key in path.Split('.'))
        {
            Assert.True(node.TryGetProperty("properties", out var properties) && properties.TryGetProperty(key, out node),
                $"nebra.schema.json does not describe '{path}'");
        }

        Assert.True(node.TryGetProperty("description", out _), $"'{path}' has no description");
    }

    private static IEnumerable<string> SettingsOf(Type type, string prefix)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.SetMethod == null || IsIgnored(property)) continue;

            var key = prefix + JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name);
            yield return key;

            if (property.PropertyType.Name.EndsWith("Section", StringComparison.Ordinal))
            {
                foreach (var nested in SettingsOf(property.PropertyType, key + "."))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool IsIgnored(PropertyInfo property)
    {
        return property.GetCustomAttributes().Any(attribute => attribute.GetType().Name is "TomlIgnoreAttribute" or "JsonIgnoreAttribute");
    }

    private static JsonElement Schema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Nebra.sln")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory!.FullName, "vscode-nebra", "schemas", "nebra.schema.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }
}
