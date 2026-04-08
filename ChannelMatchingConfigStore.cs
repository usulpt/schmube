using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Schmube;

public sealed class ChannelMatchingConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "schmube.channel-matching.json");

    public ChannelMatchingConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new ChannelMatchingConfig();
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<ChannelMatchingConfig>(json, JsonOptions) ?? new ChannelMatchingConfig();
        }
        catch
        {
            return new ChannelMatchingConfig();
        }
    }
}

public sealed class ChannelMatchingConfig
{
    public List<ChannelAliasRule> Aliases { get; set; } = [];

    public List<string> IgnoredOfferNames { get; set; } = [];
}

public sealed class ChannelAliasRule
{
    public string OfferPattern { get; set; } = string.Empty;

    public bool IsRegex { get; set; }

    public string CountryCode { get; set; } = string.Empty;

    public List<string> ChannelNameContains { get; set; } = [];

    public List<string> ChannelIds { get; set; } = [];

    public List<string> ChannelGroupContains { get; set; } = [];

    public List<string> ExcludeChannelNameContains { get; set; } = [];

    public int ScoreBoost { get; set; } = 400;
}
