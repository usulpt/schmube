using System;
using System.IO;
using System.Text.Json;

namespace Schmube;

public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "schmube.config.json");

    public SchmubeAppConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new SchmubeAppConfig();
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<SchmubeAppConfig>(json, JsonOptions) ?? new SchmubeAppConfig();
        }
        catch
        {
            return new SchmubeAppConfig();
        }
    }
}
