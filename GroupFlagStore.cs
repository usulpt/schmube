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
        ["DZ"] = "DZ",
        ["ALGERIA"] = "DZ",
        ["BAHRAIN"] = "BH",
        ["EG"] = "EG",
        ["EGYPT"] = "EG",
        ["AE"] = "AE",
        ["UAE"] = "AE",
        ["EMIRATES"] = "AE",
        ["UNITEDARABEMIRATES"] = "AE",
        ["IRAQ"] = "IQ",
        ["JORDAN"] = "JO",
        ["KUWAIT"] = "KW",
        ["LEBANON"] = "LB",
        ["LIBYA"] = "LY",
        ["MOROCCO"] = "MA",
        ["OMAN"] = "OM",
        ["QATAR"] = "QA",
        ["PALESTINE"] = "PS",
        ["SY"] = "SY",
        ["SYRIA"] = "SY",
        ["YE"] = "YE",
        ["YEMEN"] = "YE",
        ["SD"] = "SD",
        ["SUDAN"] = "SD",
        ["TN"] = "TW",
        ["TUNISIA"] = "TN",
        ["ALGERIE"] = "DZ",
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
        ["CM"] = "CM",
        ["CAMEROON"] = "CM",
        ["CR"] = "CR",
        ["COSTARICA"] = "CR",
        ["NI"] = "NI",
        ["NICARAGUA"] = "NI",
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
        ["NZL"] = "NZ",
        ["NEWZEALAND"] = "NZ",
        ["MA"] = "MT",
        ["JP"] = "JP",
        ["JPN"] = "JP",
        ["JAPAN"] = "JP",
        ["CN"] = "CN",
        ["CHN"] = "CN",
        ["CHINA"] = "CN",
        ["IN"] = "IN",
        ["INDIA"] = "IN",
        ["ID"] = "ID",
        ["IDN"] = "ID",
        ["INDONESIA"] = "ID",
        ["IS"] = "IS",
        ["ISL"] = "IS",
        ["ICELAND"] = "IS",
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
        ["ML"] = "ML",
        ["MALI"] = "ML",
        ["ME"] = "ME",
        ["MNE"] = "ME",
        ["MONTENEGRO"] = "ME",
        ["MZ"] = "MZ",
        ["MOZ"] = "MZ",
        ["MOZAMBIQUE"] = "MZ",
        ["NP"] = "NP",
        ["NEPAL"] = "NP",
        ["NG"] = "NG",
        ["NIGERIA"] = "NG",
        ["PA"] = "PA",
        ["PANAMA"] = "PA",
        ["PE"] = "PE",
        ["PERU"] = "PE",
        ["PR"] = "PE",
        ["PH"] = "PH",
        ["PHILIPPINES"] = "PH",
        ["PK"] = "PK",
        ["PAKISTAN"] = "PK",
        ["SUR"] = "SR",
        ["SU"] = "SR",
        ["SURINAME"] = "SR",
        ["TW"] = "TW",
        ["TWN"] = "TW",
        ["TN"] = "TW",
        ["TAIWAN"] = "TW",
        ["RU"] = "RU",
        ["RUS"] = "RU",
        ["RUSSIA"] = "RU",
        ["RS"] = "RS",
        ["SERBIA"] = "RS",
        ["SR"] = "RS",
        ["SG"] = "SG",
        ["SINGAPORE"] = "SG",
        ["SI"] = "SI",
        ["SLOVENIA"] = "SI",
        ["SN"] = "SN",
        ["SEN"] = "SN",
        ["SENEGAL"] = "SN",
        ["SO"] = "SO",
        ["SOM"] = "SO",
        ["SOMALIA"] = "SO",
        ["TG"] = "TG",
        ["TOG"] = "TG",
        ["TOGO"] = "TG",
        ["MALTA"] = "MT",
        ["MT"] = "MT",
        ["TZ"] = "TZ",
        ["TANZANIA"] = "TZ",
        ["CYPRIOT"] = "CY",
        ["CYPRUS"] = "CY",
        ["CY"] = "CY",
        ["AZ"] = "AZ",
        ["AZE"] = "AZ",
        ["AZERBAIJAN"] = "AZ",
        ["AM"] = "AM",
        ["ARM"] = "AM",
        ["ARMENIA"] = "AM",
        ["AF"] = "AF",
        ["AFG"] = "AF",
        ["AG"] = "AF",
        ["AFGHANISTAN"] = "AF",
        ["ET"] = "ET",
        ["ETH"] = "ET",
        ["ETHIOPIA"] = "ET",
        ["ETHO"] = "ET",
        ["GH"] = "GH",
        ["GHA"] = "GH",
        ["GHANA"] = "GH",
        ["KE"] = "KE",
        ["KEN"] = "KE",
        ["KN"] = "KE",
        ["KENYA"] = "KE",
        ["KU"] = "KU",
        ["KURDISTAN"] = "KU",
        ["TH"] = "TH",
        ["THAILAND"] = "TH",
        ["UA"] = "UA",
        ["UKRAINE"] = "UA",
        ["UG"] = "UG",
        ["UGA"] = "UG",
        ["UGANDA"] = "UG",
        ["UY"] = "UY",
        ["URUGUAY"] = "UY",
        ["UZ"] = "UZ",
        ["UZBEKISTAN"] = "UZ",
        ["PRTORICO"] = "PR",
        ["PUERTORICO"] = "PR",
        ["PUERTORICAN"] = "PR",
        ["VENEZULA"] = "VE",
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
        if (TryResolveKnownChannelAndGroupException(title, fallbackGroupTitle, out var knownChannelAndGroupResult))
        {
            return knownChannelAndGroupResult;
        }

        var primaryResult = ResolveSingle(title);
        if (string.IsNullOrWhiteSpace(fallbackGroupTitle))
        {
            return primaryResult;
        }

        if (TryResolveLeadingCountry(fallbackGroupTitle, out var explicitFallbackResult))
        {
            return explicitFallbackResult;
        }

        var fallbackResult = ResolveSingle(fallbackGroupTitle);
        if (!string.IsNullOrWhiteSpace(primaryResult.Flag))
        {
            return primaryResult;
        }

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

        if (TryResolveKnownGroupException(trimmedTitle, out var knownExceptionResult))
        {
            return knownExceptionResult;
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

        if (IsArgentinaAlias(normalized))
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

    private static bool IsArgentinaAlias(string normalizedAlias)
    {
        return string.Equals(normalizedAlias, "AR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedAlias, "ARG", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedAlias, "ARGENTINA", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAfricaRegionalGroup(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var trimmedTitle = title.Trim();
        return trimmedTitle.StartsWith("AFR|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR —", StringComparison.CurrentCultureIgnoreCase);
    }

    private bool TryResolveLeadingCountry(string title, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        if (TryResolveArabicRegionalCountry(title, out result) && !string.IsNullOrWhiteSpace(result.Flag))
        {
            return true;
        }

        var countrySegment = ExtractCountrySegment(title);
        if (string.IsNullOrWhiteSpace(countrySegment))
        {
            return false;
        }

        if (TryResolveAlias(countrySegment, out var directCountryCode))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath(directCountryCode), BuildDefaultLabel(countrySegment));
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (TryInferCountryCode(countrySegment, out var inferredCountryCode))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath(inferredCountryCode), BuildDefaultLabel(countrySegment));
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        return false;
    }

    private bool TryResolveKnownChannelAndGroupException(string? title, string? fallbackGroupTitle, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, fallbackGroupTitle ?? title ?? string.Empty);

        var trimmedTitle = title?.Trim() ?? string.Empty;
        var trimmedGroup = fallbackGroupTitle?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedGroup))
        {
            return false;
        }

        if (IsDstvVipGroup(trimmedGroup))
        {
            result = new GroupFlagInfo(BuildGroupAssetPath("dstv.png"), "DSTV");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (IsCanalPlusAfricaVipGroup(trimmedGroup))
        {
            result = new GroupFlagInfo(BuildGroupAssetPath("canalplus-africa.png"), "Canal+ Africa");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (IsAsia24SevenGroup(trimmedGroup))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath("IN"), "India");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (IsAfricaRegionalGroup(trimmedGroup)
            && TryResolveAfricaChannelPrefix(trimmedTitle, out result))
        {
            return true;
        }

        var indicatesMalaysiaGroup = trimmedGroup.Contains("MALAYSIA", StringComparison.CurrentCultureIgnoreCase);
        var indicatesMalaysiaChannel = trimmedTitle.StartsWith("MY|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("MY:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("MY/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("MY -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("MY –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("MY —", StringComparison.CurrentCultureIgnoreCase);

        if (indicatesMalaysiaGroup && indicatesMalaysiaChannel)
        {
            result = new GroupFlagInfo(BuildFlagAssetPath("MY"), "Malaysia");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (trimmedTitle.StartsWith("SOM:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("SOM|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("SOM/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("SOM -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("SOM –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("SOM —", StringComparison.CurrentCultureIgnoreCase))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath("SO"), "Somalia");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (trimmedTitle.StartsWith("ETH:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ETH|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ETH/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ETH -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ETH –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ETH —", StringComparison.CurrentCultureIgnoreCase))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath("ET"), "Ethiopia");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        return false;
    }

    private bool TryResolveAfricaChannelPrefix(string title, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string? countryCode = null;
        string? label = null;

        if (StartsWithAny(title, "CAM"))
        {
            countryCode = "CM";
            label = "Cameroon";
        }
        else if (StartsWithAny(title, "NIG"))
        {
            countryCode = "NG";
            label = "Nigeria";
        }
        else if (StartsWithAny(title, "ETHO"))
        {
            countryCode = "ET";
            label = "Ethiopia";
        }
        else if (StartsWithAny(title, "GHA"))
        {
            countryCode = "GH";
            label = "Ghana";
        }
        else if (StartsWithAny(title, "KN"))
        {
            countryCode = "KE";
            label = "Kenya";
        }
        else if (StartsWithAny(title, "MALI"))
        {
            countryCode = "ML";
            label = "Mali";
        }
        else if (StartsWithAny(title, "MOZ"))
        {
            countryCode = "MZ";
            label = "Mozambique";
        }
        else if (StartsWithAny(title, "SEN"))
        {
            countryCode = "SN";
            label = "Senegal";
        }
        else if (StartsWithAny(title, "TOG"))
        {
            countryCode = "TG";
            label = "Togo";
        }
        else if (StartsWithAny(title, "TZ"))
        {
            countryCode = "TZ";
            label = "Tanzania";
        }
        else if (StartsWithAny(title, "UGA"))
        {
            countryCode = "UG";
            label = "Uganda";
        }

        if (string.IsNullOrWhiteSpace(countryCode) || string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        result = new GroupFlagInfo(BuildFlagAssetPath(countryCode), label);
        return !string.IsNullOrWhiteSpace(result.Flag);
    }

    private static bool StartsWithAny(string value, string prefix)
    {
        return value.StartsWith($"{prefix}:", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{prefix}|", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{prefix}/", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{prefix} -", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{prefix} –", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{prefix} —", StringComparison.CurrentCultureIgnoreCase);
    }

    private bool TryResolveKnownGroupException(string title, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        var trimmedTitle = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedTitle))
        {
            return false;
        }

        if (IsDstvVipGroup(trimmedTitle))
        {
            result = new GroupFlagInfo(BuildGroupAssetPath("dstv.png"), "DSTV");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (IsCanalPlusAfricaVipGroup(trimmedTitle))
        {
            result = new GroupFlagInfo(BuildGroupAssetPath("canalplus-africa.png"), "Canal+ Africa");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (IsAsia24SevenGroup(trimmedTitle))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath("IN"), "India");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (trimmedTitle.StartsWith("ASIA|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ASIA:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ASIA/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ASIA -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ASIA –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("ASIA —", StringComparison.CurrentCultureIgnoreCase))
        {
            result = new GroupFlagInfo(BuildFlagAssetPath("IN"), "India");
            return !string.IsNullOrWhiteSpace(result.Flag);
        }

        if (TryResolveArabicBrandGroup(trimmedTitle, out result))
        {
            return true;
        }

        if (TryResolveLatinAmericaArgentinaGroup(trimmedTitle, out result))
        {
            return true;
        }

        if (TryResolveRegionalCountryFromSegments(trimmedTitle, "LA", out result))
        {
            return true;
        }

        if (TryResolveRegionalCountryFromSegments(trimmedTitle, "AFR", out result))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveArabicBrandGroup(string title, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        if (TryResolveArabicBrandGroup(title, "NETFLIX", "netflix.png", "Netflix", out result)
            || TryResolveArabicBrandGroup(title, "BEIN", "bein.png", "beIN", out result)
            || TryResolveArabicBrandGroup(title, "MBC", "mbc.png", "MBC", out result)
            || TryResolveArabicBrandGroup(title, "ALWAN", "alwan.png", "ALWAN", out result))
        {
            return true;
        }

        return false;
    }

    private bool TryResolveArabicBrandGroup(string title, string segmentPrefix, string assetFileName, string label, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        if (!TryGetRegionalGroupFirstSegment(title, "AR", out var segment) ||
            !segment.StartsWith(segmentPrefix, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        result = new GroupFlagInfo(BuildGroupAssetPath(assetFileName), label);
        return !string.IsNullOrWhiteSpace(result.Flag);
    }

    private bool TryResolveLatinAmericaArgentinaGroup(string title, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        var segments = title
            .Split(GroupSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (segments.Count != 2 ||
            !string.Equals(segments[0], "LA", StringComparison.CurrentCultureIgnoreCase) ||
            !string.Equals(segments[1], "ARGENTINA", StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        result = new GroupFlagInfo(BuildFlagAssetPath("AR"), "Argentina");
        return !string.IsNullOrWhiteSpace(result.Flag);
    }

    private static bool TryGetRegionalGroupFirstSegment(string title, string regionMarker, out string segment)
    {
        segment = string.Empty;
        var segments = title
            .Split(GroupSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        if (segments.Count < 2 || !string.Equals(segments[0], regionMarker, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        segment = segments[1];
        return !string.IsNullOrWhiteSpace(segment);
    }

    private static bool IsAsia24SevenGroup(string title)
    {
        return (title ?? string.Empty).Trim().StartsWith("ASIA| 24/7", StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsDstvVipGroup(string title)
    {
        var trimmedTitle = (title ?? string.Empty).Trim();
        return trimmedTitle.StartsWith("AFR| DSTV VIP HD/4K", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR|DSTV VIP HD/4K", StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsCanalPlusAfricaVipGroup(string title)
    {
        var trimmedTitle = (title ?? string.Empty).Trim();
        return trimmedTitle.StartsWith("AFR| CANAL+ VIP HD/4K", StringComparison.CurrentCultureIgnoreCase)
            || trimmedTitle.StartsWith("AFR|CANAL+ VIP HD/4K", StringComparison.CurrentCultureIgnoreCase);
    }

    private bool TryResolveRegionalCountryFromSegments(string title, string regionMarker, out GroupFlagInfo result)
    {
        result = new GroupFlagInfo(string.Empty, title);
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(regionMarker))
        {
            return false;
        }

        if (!title.StartsWith($"{regionMarker}|", StringComparison.CurrentCultureIgnoreCase)
            && !title.StartsWith($"{regionMarker}:", StringComparison.CurrentCultureIgnoreCase)
            && !title.StartsWith($"{regionMarker}/", StringComparison.CurrentCultureIgnoreCase)
            && !title.StartsWith($"{regionMarker} -", StringComparison.CurrentCultureIgnoreCase)
            && !title.StartsWith($"{regionMarker} –", StringComparison.CurrentCultureIgnoreCase)
            && !title.StartsWith($"{regionMarker} —", StringComparison.CurrentCultureIgnoreCase))
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
                if (!string.IsNullOrWhiteSpace(result.Flag))
                {
                    return true;
                }
            }

            if (TryInferCountryCode(segment, out var inferredCountryCode))
            {
                result = new GroupFlagInfo(BuildFlagAssetPath(inferredCountryCode), BuildDefaultLabel(segment));
                if (!string.IsNullOrWhiteSpace(result.Flag))
                {
                    return true;
                }
            }
        }

        return false;
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

    private static string BuildGroupAssetPath(string fileName)
    {
        var normalizedFileName = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedFileName))
        {
            return string.Empty;
        }

        var assetPath = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Flags",
            normalizedFileName);

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
