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
        var countryCode = GetCountryCodeFromFlag(groupFlagInfo.Flag);
        if (!string.IsNullOrWhiteSpace(countryCode))
        {
            var countryLabel = GetCountryLabel(countryCode, groupFlagInfo.DisplayTitle);
            return new CanonicalGroupInfo(countryCode, countryLabel);
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
