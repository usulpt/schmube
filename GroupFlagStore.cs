using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Schmube;

public sealed class GroupFlagStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly IReadOnlyDictionary<string, string> CountryAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PT"] = "PT",
        ["PORTUGAL"] = "PT",
        ["POR"] = "PT",
        ["UK"] = "GB",
        ["GB"] = "GB",
        ["GBR"] = "GB",
        ["UNITEDKINGDOM"] = "GB",
        ["ENGLAND"] = "GB",
        ["US"] = "US",
        ["USA"] = "US",
        ["UNITEDSTATES"] = "US",
        ["AMERICA"] = "US",
        ["BR"] = "BR",
        ["BRAZIL"] = "BR",
        ["BRASIL"] = "BR",
        ["ES"] = "ES",
        ["SPAIN"] = "ES",
        ["ESPANA"] = "ES",
        ["FR"] = "FR",
        ["FRANCE"] = "FR",
        ["DE"] = "DE",
        ["GERMANY"] = "DE",
        ["DEUTSCHLAND"] = "DE",
        ["IT"] = "IT",
        ["ITALY"] = "IT",
        ["ITALIA"] = "IT",
        ["NL"] = "NL",
        ["NETHERLANDS"] = "NL",
        ["BE"] = "BE",
        ["BELGIUM"] = "BE",
        ["CH"] = "CH",
        ["SWITZERLAND"] = "CH",
        ["SE"] = "SE",
        ["SWEDEN"] = "SE",
        ["NO"] = "NO",
        ["NORWAY"] = "NO",
        ["DK"] = "DK",
        ["DENMARK"] = "DK",
        ["FI"] = "FI",
        ["FINLAND"] = "FI",
        ["PL"] = "PL",
        ["POLAND"] = "PL",
        ["RO"] = "RO",
        ["ROMANIA"] = "RO",
        ["GR"] = "GR",
        ["GREECE"] = "GR",
        ["TR"] = "TR",
        ["TURKEY"] = "TR",
        ["AR"] = "AR",
        ["ARGENTINA"] = "AR",
        ["MX"] = "MX",
        ["MEXICO"] = "MX",
        ["CA"] = "CA",
        ["CANADA"] = "CA"
    };

    private static readonly Regex LeadingCountryCodePattern = new("^([A-Za-z]{2})\\s*\\|", RegexOptions.Compiled);

    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "schmube.group-flags.json");
    private readonly Lazy<GroupFlagConfig> _config;

    public GroupFlagStore()
    {
        _config = new Lazy<GroupFlagConfig>(LoadInternal);
    }

    public GroupFlagInfo Resolve(string groupTitle)
    {
        var trimmedGroupTitle = groupTitle?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedGroupTitle))
        {
            return new GroupFlagInfo(string.Empty, string.Empty);
        }

        foreach (var rule in _config.Value.Rules)
        {
            if (!IsMatch(rule, trimmedGroupTitle))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(rule.Label) ? BuildDefaultLabel(trimmedGroupTitle) : rule.Label.Trim();
            return new GroupFlagInfo(BuildFlagCode(rule.CountryCode), label);
        }

        if (TryParseLeadingCountryCode(trimmedGroupTitle, out var prefixedCountryCode))
        {
            return new GroupFlagInfo(BuildFlagCode(prefixedCountryCode), BuildDefaultLabel(trimmedGroupTitle));
        }

        if (TryInferCountryCode(trimmedGroupTitle, out var inferredCountryCode))
        {
            return new GroupFlagInfo(BuildFlagCode(inferredCountryCode), BuildDefaultLabel(trimmedGroupTitle));
        }

        return new GroupFlagInfo(string.Empty, trimmedGroupTitle);
    }

    private GroupFlagConfig LoadInternal()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new GroupFlagConfig();
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<GroupFlagConfig>(json, JsonOptions) ?? new GroupFlagConfig();
        }
        catch
        {
            return new GroupFlagConfig();
        }
    }

    private static bool IsMatch(GroupFlagRule rule, string groupTitle)
    {
        if (string.IsNullOrWhiteSpace(rule.Match))
        {
            return false;
        }

        return (rule.MatchMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "exact" => string.Equals(groupTitle, rule.Match.Trim(), StringComparison.CurrentCultureIgnoreCase),
            "contains" => groupTitle.Contains(rule.Match.Trim(), StringComparison.CurrentCultureIgnoreCase),
            _ => groupTitle.StartsWith(rule.Match.Trim(), StringComparison.CurrentCultureIgnoreCase)
        };
    }

    private static bool TryParseLeadingCountryCode(string groupTitle, out string countryCode)
    {
        countryCode = string.Empty;
        var match = LeadingCountryCodePattern.Match(groupTitle);
        if (!match.Success)
        {
            return false;
        }

        var prefix = match.Groups[1].Value.Trim().ToUpperInvariant();
        if (!CountryAliases.TryGetValue(prefix, out var resolvedCountryCode) || string.IsNullOrWhiteSpace(resolvedCountryCode))
        {
            return false;
        }

        countryCode = resolvedCountryCode;
        return true;
    }

    private static bool TryInferCountryCode(string groupTitle, out string countryCode)
    {
        countryCode = string.Empty;
        var normalized = Regex.Replace(groupTitle.ToUpperInvariant(), "[^A-Z0-9]+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in BuildCandidates(tokens))
        {
            if (CountryAliases.TryGetValue(candidate, out var resolvedCountryCode) && !string.IsNullOrWhiteSpace(resolvedCountryCode))
            {
                countryCode = resolvedCountryCode!;
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> BuildCandidates(string[] tokens)
    {
        if (tokens.Length == 0)
        {
            yield break;
        }

        yield return tokens[0];

        if (tokens.Length >= 2)
        {
            yield return string.Concat(tokens[0], tokens[1]);
        }

        if (tokens.Length >= 3)
        {
            yield return string.Concat(tokens[0], tokens[1], tokens[2]);
        }
    }

    private static string BuildDefaultLabel(string groupTitle)
    {
        var separatorIndex = groupTitle.IndexOf('|');
        return separatorIndex > 0
            ? groupTitle[..separatorIndex].Trim()
            : groupTitle.Trim();
    }

    private static string BuildFlagCode(string countryCode)
    {
        var normalized = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 2 || normalized.Any(ch => ch is < 'A' or > 'Z'))
        {
            return string.Empty;
        }

        return normalized;
    }
}

public sealed record GroupFlagInfo(string Flag, string DisplayTitle);

public sealed class GroupFlagConfig
{
    public List<GroupFlagRule> Rules { get; set; } = [];
}

public sealed class GroupFlagRule
{
    public string Match { get; set; } = string.Empty;

    public string MatchMode { get; set; } = "StartsWith";

    public string CountryCode { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}


