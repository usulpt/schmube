using System;
using System.IO;
using System.Text.Json;

namespace Schmube;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Schmube",
        "settings.json");

    public StreamSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new StreamSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<StreamSettings>(json) ?? new StreamSettings();
        }
        catch
        {
            return new StreamSettings();
        }
    }

    public void Save(StreamSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Unable to determine settings directory.");
        }

        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
