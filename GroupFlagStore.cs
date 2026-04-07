using System;
using System.Collections.Generic;
using System.Globalization;
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
    private static readonly char[] CountrySeparators = [':', '|', '-', '–', '—'];
    private static readonly string[] IgnoredStatusPrefixes =
    [
        "NOEVENT",
        "NOSTREAM",
        "NOSIGNAL",
        "OFFLINE"
    ];
    private static readonly char[] GroupSeparators = [':', '|', '-', '–', '—', '/'];

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
        ["ARGENTINA"] = "ARG",
        ["AL"] = "AL",
        ["ALBANIA"] = "AL",
        ["AT"] = "AT",
        ["AUSTRIA"] = "AT",
        ["BA"] = "BA",
        ["BOSNIA"] = "BA",
        ["BOSNIAHERZEGOVINA"] = "BA",
        ["BH"] = "BA",
        ["BG"] = "BG",
        ["BULGARIA"] = "BG",
        ["BO"] = "BO",
        ["BOLIVIA"] = "BO",
        ["MX"] = "MX",
        ["MEXICO"] = "MX",
        ["CA"] = "CA",
        ["CANADA"] = "CA",
        ["CO"] = "CO",
        ["COLOMBIA"] = "CO",
        ["CR"] = "CR",
        ["COSTARICA"] = "CR",
        ["CZ"] = "CZ",
        ["CZECHIA"] = "CZ",
        ["CZECHREPUBLIC"] = "CZ",
        ["DO"] = "DO",
        ["DOMINICANREPUBLIC"] = "DO",
        ["RD"] = "DO",
        ["EC"] = "EC",
        ["ECUADOR"] = "EC",
        ["RC"] = "EC",
        ["GE"] = "GE",
        ["GEORGIA"] = "GE",
        ["GT"] = "GT",
        ["GUATEMALA"] = "GT",
        ["HK"] = "HK",
        ["HONGKONG"] = "HK",
        ["HN"] = "HN",
        ["HONDURAS"] = "HN",
        ["HR"] = "HR",
        ["CROATIA"] = "HR",
        ["HU"] = "HU",
        ["HUNGARY"] = "HU",
        ["IE"] = "IE",
        ["IRELAND"] = "IE",
        ["IL"] = "IL",
        ["ISRAEL"] = "IL",
        ["IR"] = "IR",
        ["IRAN"] = "IR",
        ["AU"] = "AU",
        ["AUSTRALIA"] = "AU",
        ["NZ"] = "NZ",
        ["NEWZEALAND"] = "NZ",
        ["JP"] = "JP",
        ["JAPAN"] = "JP",
        ["CN"] = "CN",
        ["CHINA"] = "CN",
        ["IN"] = "IN",
        ["INDIA"] = "IN",
        ["KH"] = "KH",
        ["CAMBODIA"] = "KH",
        ["KR"] = "KR",
        ["KOREA"] = "KR",
        ["SOUTHKOREA"] = "KR",
        ["KZ"] = "KZ",
        ["KAZAKHSTAN"] = "KZ",
        ["LT"] = "LT",
        ["LITHUANIA"] = "LT",
        ["MK"] = "MK",
        ["MACEDONIA"] = "MK",
        ["NORTHMACEDONIA"] = "MK",
        ["MY"] = "MY",
        ["MALAYSIA"] = "MY",
        ["NP"] = "NP",
        ["NEPAL"] = "NP",
        ["PA"] = "PA",
        ["PANAMA"] = "PA",
        ["PE"] = "PE",
        ["PERU"] = "PE",
        ["PR"] = "PE",
        ["PH"] = "PH",
        ["PHILIPPINES"] = "PH",
        ["PK"] = "PK",
        ["PAKISTAN"] = "PK",
        ["RU"] = "RU",
        ["RUSSIA"] = "RU",
        ["RS"] = "RS",
        ["SERBIA"] = "RS",
        ["SR"] = "RS",
        ["SG"] = "SG",
        ["SINGAPORE"] = "SG",
        ["SI"] = "SI",
        ["SLOVENIA"] = "SI",
        ["MALTA"] = "MT",
        ["MT"] = "MT",
        ["CYPRUS"] = "CY",
        ["CY"] = "CY",
        ["TH"] = "TH",
        ["THAILAND"] = "TH",
        ["UA"] = "UA",
        ["UKRAINE"] = "UA",
        ["UY"] = "UY",
        ["URUGUAY"] = "UY",
        ["UZ"] = "UZ",
        ["UZBEKISTAN"] = "UZ",
        ["VE"] = "VE",
        ["VENEZUELA"] = "VE",
        ["VN"] = "VN",
        ["VIETNAM"] = "VN"
    };

    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "schmube.group-flags.json");
    private readonly Lazy<GroupFlagConfig> _config;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _countryAliases;

    public GroupFlagStore()
    {
        _config = new Lazy<GroupFlagConfig>(LoadInternal);
        _countryAliases = new Lazy<IReadOnlyDictionary<string, string>>(BuildCountryAliases);
    }

    public GroupFlagInfo Resolve(string title, string? fallbackGroupTitle = null)
    {
        var primaryResult = ResolveSingle(title);
        if (!string.IsNullOrWhiteSpace(primaryResult.Flag) || string.IsNullOrWhiteSpace(fallbackGroupTitle))
        {
            return primaryResult;
        }

        var fallbackResult = ResolveSingle(fallbackGroupTitle);
        if (!string.IsNullOrWhiteSpace(fallbackResult.Flag))
        {
            return fallbackResult;
        }

        return primaryResult;
    }

    private GroupFlagInfo ResolveSingle(string title)
    {
        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return new GroupFlagInfo(string.Empty, string.Empty);
        }

        if (IsIgnoredStatusTitle(trimmedTitle))
        {
            return new GroupFlagInfo(string.Empty, trimmedTitle);
        }

        if (TryResolveArabicRegionalCountry(trimmedTitle, out var arabicRegionalResult))
        {
            return arabicRegionalResult;
        }

        if (IsArabicRegionalMarkerTitle(trimmedTitle))
        {
            return new GroupFlagInfo(string.Empty, trimmedTitle);
        }

        var countrySegment = ExtractCountrySegment(trimmedTitle);

        foreach (var rule in _config.Value.Rules)
        {
            if (!IsMatch(rule, trimmedTitle) && !IsMatch(rule, countrySegment))
            {
                continue;
            }

            var label = string.IsNullOrWhiteSpace(rule.Label) ? BuildDefaultLabel(countrySegment) : rule.Label.Trim();
            if (TryResolveConfiguredCountryCode(rule.CountryCode, out var resolvedCountryCode))
            {
                return new GroupFlagInfo(BuildFlagAssetPath(resolvedCountryCode), label);
            }
        }

        foreach (var candidate in EnumerateCountryCandidates(trimmedTitle, countrySegment))
        {
            if (TryResolveAlias(candidate, out var directCountryCode))
            {
                return new GroupFlagInfo(BuildFlagAssetPath(directCountryCode), BuildDefaultLabel(candidate));
            }

            if (TryInferCountryCode(candidate, out var inferredCountryCode))
            {
                return new GroupFlagInfo(BuildFlagAssetPath(inferredCountryCode), BuildDefaultLabel(candidate));
            }
        }

        return new GroupFlagInfo(string.Empty, trimmedTitle);
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

    private IReadOnlyDictionary<string, string> BuildCountryAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                AddCountryAlias(aliases, region.TwoLetterISORegionName, region.TwoLetterISORegionName);
                AddCountryAlias(aliases, region.ThreeLetterISORegionName, region.TwoLetterISORegionName);
                AddCountryAlias(aliases, region.EnglishName, region.TwoLetterISORegionName);
                AddCountryAlias(aliases, region.NativeName, region.TwoLetterISORegionName);
                AddCountryAlias(aliases, region.Name, region.TwoLetterISORegionName);
            }
            catch
            {
            }
        }

        foreach (var alias in CountryAliases)
        {
            AddCountryAlias(aliases, alias.Key, alias.Value);
        }

        foreach (var alias in _config.Value.Aliases)
        {
            AddCountryAlias(aliases, alias.Key, alias.Value);
        }

        return aliases;
    }

    private bool TryResolveConfiguredCountryCode(string rawValue, out string countryCode)
    {
        countryCode = string.Empty;
        var normalized = NormalizeAliasKey(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized.Length == 2 && normalized.All(ch => ch is >= 'A' and <= 'Z'))
        {
            countryCode = normalized;
            return true;
        }

        return TryResolveAlias(normalized, out countryCode);
    }

    private bool TryResolveAlias(string rawValue, out string countryCode)
    {
        countryCode = string.Empty;
        var normalized = NormalizeAliasKey(rawValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (!_countryAliases.Value.TryGetValue(normalized, out var resolvedCountryCode) || string.IsNullOrWhiteSpace(resolvedCountryCode))
        {
            return false;
        }

        countryCode = resolvedCountryCode;
        return true;
    }

    private bool TryInferCountryCode(string groupTitle, out string countryCode)
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
            if (TryResolveAlias(candidate, out var resolvedCountryCode))
            {
                countryCode = resolvedCountryCode;
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

    private static IEnumerable<string> EnumerateCountryCandidates(string title, string countrySegment)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in title
                     .Split(CountrySeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .OrderByDescending(GetCountryCandidatePriority))
        {
            if (seen.Add(segment))
            {
                yield return segment;
            }
        }

        if (seen.Add(title))
        {
            yield return title;
        }

        if (!string.IsNullOrWhiteSpace(countrySegment) && seen.Add(countrySegment))
        {
            yield return countrySegment;
        }
    }

    private static int GetCountryCandidatePriority(string value)
    {
        var normalized = NormalizeAliasKey(value);
        if (normalized.Length == 0)
        {
            return 0;
        }

        return normalized.Length + (normalized.Length > 3 ? 100 : 0);
    }

    private static bool IsIgnoredStatusTitle(string title)
    {
        var normalized = NormalizeAliasKey(title);
        return IgnoredStatusPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

    private bool TryResolveArabicRegionalCountry(string title, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        if (!IsArabicRegionalMarkerTitle(title))
        {
            return false;
        }

        var segments = title
            .Split(GroupSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1);

        foreach (var segment in segments)
        {
            if (TryResolveAlias(segment, out var directCountryCode))
            {
                result = new GroupFlagInfo(BuildFlagAssetPath(directCountryCode), BuildDefaultLabel(segment));
                return true;
            }

            if (TryInferCountryCode(segment, out var inferredCountryCode))
            {
                result = new GroupFlagInfo(BuildFlagAssetPath(inferredCountryCode), BuildDefaultLabel(segment));
                return true;
            }
        }

        return false;
    }

    private static bool IsArabicRegionalMarkerTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmedTitle = title.Trim();
        return trimmedTitle.StartsWith("AR|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AR:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AR/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AR -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AR –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AR —", StringComparison.CurrentCultureIgnoreCase);
    }

    private static string ExtractCountrySegment(string title)
    {
        var trimmedTitle = title.Trim();
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return string.Empty;
        }

        var separatorIndex = trimmedTitle.IndexOfAny(CountrySeparators);
        if (separatorIndex <= 0)
        {
            return trimmedTitle;
        }

        // Only trust early separators as country/name split markers.
        if (separatorIndex > 18)
        {
            return trimmedTitle;
        }

        var segment = trimmedTitle[..separatorIndex].Trim();
        return string.IsNullOrWhiteSpace(segment) ? trimmedTitle : segment;
    }

    private static string BuildDefaultLabel(string groupTitle)
    {
        var separatorIndex = groupTitle.IndexOfAny(CountrySeparators);
        return separatorIndex > 0
            ? groupTitle[..separatorIndex].Trim()
            : groupTitle.Trim();
    }

    private static string NormalizeAliasKey(string value)
    {
        return Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", string.Empty).Trim();
    }

    private static void AddCountryAlias(IDictionary<string, string> aliases, string alias, string countryCode)
    {
        var normalizedAlias = NormalizeAliasKey(alias);
        var normalizedCountryCode = NormalizeAliasKey(countryCode);
        if (string.IsNullOrWhiteSpace(normalizedAlias) || normalizedCountryCode.Length != 2)
        {
            return;
        }

        aliases[normalizedAlias] = normalizedCountryCode;
    }

    private static string BuildFlagAssetPath(string countryCode)
    {
        var normalized = (countryCode ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length != 2 || normalized.Any(ch => ch is < 'A' or > 'Z'))
        {
            return string.Empty;
        }

        var assetPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Flags",
            $"{normalized.ToLowerInvariant()}.png");

        return File.Exists(assetPath) ? assetPath : string.Empty;
    }
}

public sealed record GroupFlagInfo(string Flag, string DisplayTitle);

public sealed class GroupFlagConfig
{
    public List<GroupFlagRule> Rules { get; set; } = [];

    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GroupFlagRule
{
    public string Match { get; set; } = string.Empty;

    public string MatchMode { get; set; } = "StartsWith";

    public string CountryCode { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}
