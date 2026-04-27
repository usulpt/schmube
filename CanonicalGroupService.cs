using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Schmube;

public sealed class CanonicalGroupService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex KeyNormalizationRegex = new("[^A-Z0-9]+", RegexOptions.Compiled);
    private static readonly char[] GroupSeparators = ['|', ':', '/', '-'];
    private static readonly IReadOnlyDictionary<string, string> PrefixLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["4K"] = "4K / UHD",
        ["AFR"] = "Africa",
        ["AG"] = "Afghanistan",
        ["AL"] = "Albania",
        ["AM"] = "Armenia",
        ["AR"] = "Arabic / MENA",
        ["ASIA"] = "Asia",
        ["AT"] = "Austria",
        ["AU"] = "Australia",
        ["AZ"] = "Azerbaijan",
        ["BE"] = "Belgium",
        ["BG"] = "Bulgaria",
        ["BH"] = "Bosnia",
        ["BO"] = "Bolivia",
        ["BR"] = "Brazil",
        ["CA"] = "Canada",
        ["CG"] = "Montenegro",
        ["CH"] = "Switzerland",
        ["CR"] = "Caribbean",
        ["CY"] = "Cyprus",
        ["CZ"] = "Czech Republic",
        ["DE"] = "Germany",
        ["DK"] = "Denmark",
        ["ES"] = "Spain",
        ["FI"] = "Finland",
        ["FR"] = "France",
        ["GE"] = "Georgia",
        ["GR"] = "Greece",
        ["HK"] = "Hong Kong",
        ["HR"] = "Croatia",
        ["HU"] = "Hungary",
        ["ID"] = "Indonesia",
        ["IE"] = "Ireland",
        ["IL"] = "Israel",
        ["IR"] = "Iran",
        ["IS"] = "Iceland",
        ["IT"] = "Italy",
        ["JP"] = "Japan",
        ["KR"] = "Korea",
        ["KU"] = "Kurdish",
        ["KZ"] = "Kazakhstan",
        ["LA"] = "Latin America",
        ["LT"] = "Lithuania",
        ["MA"] = "Morocco",
        ["MC"] = "Radio",
        ["MK"] = "North Macedonia",
        ["NL"] = "Netherlands",
        ["NO"] = "Norway",
        ["NZ"] = "New Zealand",
        ["PH"] = "Philippines",
        ["PL"] = "Poland",
        ["PT"] = "Portugal",
        ["RO"] = "Romania",
        ["RU"] = "Russia",
        ["RX"] = "Relax",
        ["SE"] = "Sweden",
        ["SG"] = "Singapore",
        ["SI"] = "Slovenia",
        ["SR"] = "Serbia",
        ["SU"] = "Suriname",
        ["TH"] = "Thailand",
        ["TN"] = "Taiwan",
        ["TR"] = "Turkey",
        ["TS"] = "Tennis",
        ["UK"] = "United Kingdom",
        ["US"] = "United States",
        ["UZ"] = "Uzbekistan",
        ["VE"] = "Venezuela",
        ["VI"] = "Vietnam",
        ["WT"] = "World Cricket"
    };

    public CanonicalGroupInfo Resolve(PlaylistChannel channel, GroupFlagInfo groupFlagInfo)
    {
        if (IsPpvGroup(channel.GroupTitle))
        {
            return new CanonicalGroupInfo("PPV", "PPV");
        }

        if (IsTwentyFourSevenGroup(channel.GroupTitle))
        {
            return new CanonicalGroupInfo("247", "24/7");
        }

        if (TryResolvePrefixGroup(channel.GroupTitle, out var prefixGroup))
        {
            return prefixGroup;
        }

        if (IsRelaxChannel(channel))
        {
            return new CanonicalGroupInfo("RELAX", "Relax");
        }

        var countryCode = GetCountryCodeFromFlag(groupFlagInfo.Flag);
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            var countryLabel = GetCountryLabel(countryCode, groupFlagInfo.DisplayTitle);
            return new CanonicalGroupInfo(countryCode, countryLabel);
        }

        if (IsAfricaRegionalGroup(channel.GroupTitle))
        {
            return new CanonicalGroupInfo("AFRICA", "Africa");
        }

        var rawGroupLabel = CleanLabel(channel.GroupTitle);
        if (!string.IsNullOrWhiteSpace(rawGroupLabel))
        {
            return new CanonicalGroupInfo(NormalizeKey(rawGroupLabel), rawGroupLabel);
        }

        return new CanonicalGroupInfo("UNGROUPED", "Ungrouped");
    }

    public CanonicalGroupInfo ResolveSubGroup(PlaylistChannel channel)
    {
        var rawGroupLabel = CleanLabel(channel.GroupTitle);
        if (string.IsNullOrWhiteSpace(rawGroupLabel))
        {
            return new CanonicalGroupInfo("UNGROUPED", "Ungrouped");
        }

        var subGroupLabel = CleanLabel(RemoveGroupPrefix(rawGroupLabel));
        if (string.IsNullOrWhiteSpace(subGroupLabel))
        {
            subGroupLabel = rawGroupLabel;
        }

        return new CanonicalGroupInfo(rawGroupLabel, subGroupLabel);
    }

    private static bool TryResolvePrefixGroup(string? groupTitle, out CanonicalGroupInfo groupInfo)
    {
        groupInfo = new CanonicalGroupInfo("UNGROUPED", "Ungrouped");
        var group = groupTitle?.Trim() ?? string.Empty;
        var separatorIndex = group.IndexOfAny(GroupSeparators);
        if (separatorIndex <= 0)
        {
            return false;
        }

        var prefix = group[..separatorIndex].Trim();
        if (!PrefixLabels.TryGetValue(prefix, out var label))
        {
            return false;
        }

        var remainder = group[(separatorIndex + 1)..];
        if (string.Equals(prefix, "CH", StringComparison.OrdinalIgnoreCase)
            && remainder.Contains("CHINA", StringComparison.CurrentCultureIgnoreCase))
        {
            groupInfo = new CanonicalGroupInfo("CN", "China");
            return true;
        }

        groupInfo = new CanonicalGroupInfo(NormalizeKey(label), label);
        return true;
    }

    private static string RemoveGroupPrefix(string groupTitle)
    {
        var separatorIndex = groupTitle.IndexOfAny(GroupSeparators);
        return separatorIndex > 0 && separatorIndex < groupTitle.Length - 1
            ? groupTitle[(separatorIndex + 1)..].Trim()
            : groupTitle;
    }

    private static bool IsPpvGroup(string? groupTitle)
    {
        return (groupTitle ?? string.Empty).Contains("PPV", StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsTwentyFourSevenGroup(string? groupTitle)
    {
        return (groupTitle ?? string.Empty).Contains("24/7", StringComparison.CurrentCultureIgnoreCase);
    }

    private static string GetCountryCodeFromFlag(string flagPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(flagPath ?? string.Empty);
        return fileName.Length == 2 ? fileName.ToUpperInvariant() : string.Empty;
    }

    private static string GetCountryLabel(string countryCode, string fallback)
    {
        if (string.Equals(countryCode, "KU", StringComparison.OrdinalIgnoreCase))
        {
            return "Kurdistan";
        }

        try
        {
            var region = new RegionInfo(countryCode);
            return region.EnglishName;
        }
        catch
        {
            var cleanedFallback = CleanLabel(fallback);
            return string.IsNullOrWhiteSpace(cleanedFallback)
                ? countryCode.ToUpperInvariant()
                : cleanedFallback;
        }
    }

    private static bool IsRelaxChannel(PlaylistChannel channel)
    {
        var name = channel.Name?.Trim() ?? string.Empty;
        var group = channel.GroupTitle?.Trim() ?? string.Empty;

        return name.StartsWith("RX:", StringComparison.CurrentCultureIgnoreCase)
            || name.StartsWith("RX|", StringComparison.CurrentCultureIgnoreCase)
            || name.StartsWith("RX/", StringComparison.CurrentCultureIgnoreCase)
            || name.StartsWith("RX -", StringComparison.CurrentCultureIgnoreCase)
            || name.StartsWith("RX –", StringComparison.CurrentCultureIgnoreCase)
            || name.StartsWith("RX —", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("4K| RELAX", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("4K: RELAX", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("4K/ RELAX", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("4K - RELAX", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("4K – RELAX", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("4K — RELAX", StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsAfricaRegionalGroup(string? groupTitle)
    {
        var group = groupTitle?.Trim() ?? string.Empty;
        return group.StartsWith("AFR|", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("AFR:", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("AFR/", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("AFR -", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("AFR –", StringComparison.CurrentCultureIgnoreCase)
            || group.StartsWith("AFR —", StringComparison.CurrentCultureIgnoreCase);
    }

    private static string CleanLabel(string value)
    {
        return WhitespaceRegex.Replace((value ?? string.Empty).Trim(), " ");
    }

    private static string NormalizeKey(string value)
    {
        return KeyNormalizationRegex.Replace((value ?? string.Empty).ToUpperInvariant(), string.Empty).Trim();
    }
}

public sealed record CanonicalGroupInfo(string Key, string Label);
