using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Schmube;

public sealed class CanonicalGroupService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex KeyNormalizationRegex = new("[^A-Z0-9]+", RegexOptions.Compiled);

    public CanonicalGroupInfo Resolve(PlaylistChannel channel, GroupFlagInfo groupFlagInfo)
    {
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
