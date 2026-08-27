using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopOrganizer.Core.Models;

namespace DesktopOrganizer.Core.Config;

public static class ConfigSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ClassifierConfig config)
        => JsonSerializer.Serialize(config, Options);

    public static ClassifierConfig Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ClassifierConfig();
        try
        {
            var config = JsonSerializer.Deserialize<ClassifierConfig>(json, Options) ?? new ClassifierConfig();
            config.Overrides = config.Overrides is null
                ? new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Category>(config.Overrides, StringComparer.OrdinalIgnoreCase);
            return config;
        }
        catch (JsonException)
        {
            return new ClassifierConfig();
        }
    }
}
