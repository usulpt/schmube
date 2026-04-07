using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Schmube;

public sealed class ListingsImportService
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NormalizeRegex = new("[^A-Z0-9]+", RegexOptions.Compiled);
    private static readonly Regex CleanupRegex = new(@"\b(HD|FHD|UHD|4K|SD|HEVC|H264|H265)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> IgnoredLeftSideValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "international coverage",
        "live stream and tv schedule",
        "tv schedule",
        "content disclaimer"
    };
    private static readonly HashSet<string> IgnoredChannelTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "TV",
        "LIVE",
        "SPORT",
        "SPORTS",
        "CHANNEL",
        "NETWORK"
    };

    private static readonly IReadOnlyDictionary<string, string> CountryAliasMap = BuildCountryAliasMap();
    private readonly IReadOnlyList<ChannelCandidate> _channelCandidates;

    public ListingsImportService(IReadOnlyList<PlaylistChannel> availableChannels)
    {
        _channelCandidates = availableChannels
            .Select(channel => new ChannelCandidate(channel, BuildCandidateTokens(channel), BuildCountryCodes(channel), BuildNumberTokens(channel)))
            .Where(candidate => candidate.Tokens.Count > 0)
            .ToList();
    }

    public IReadOnlyList<ListingChannelMatch> BuildMatches(IEnumerable<string>? extractedRows, string pageText, string? html = null)
    {
        return ParseListingEntries(extractedRows, pageText, html)
            .Select(MatchEntry)
            .ToList();
    }

    private ListingChannelMatch MatchEntry(ListingRow entry)
    {
        var normalizedBroadcasters = Normalize(entry.BroadcasterText);
        var entryCountryCodes = ResolveCountryCodes(entry.Country);
        if (entryCountryCodes.Count == 0)
        {
            return new ListingChannelMatch(entry.Country, entry.BroadcasterText, []);
        }

        var broadcasterMatches = _channelCandidates
            .Select(candidate => new ScoredCandidate(candidate, candidate.MatchScore(normalizedBroadcasters)))
            .Where(item => item.Score > 0)
            .ToList();

        var countryFilteredMatches = broadcasterMatches
            .Where(item => item.Candidate.CountryCodes.Overlaps(entryCountryCodes))
            .ToList();

        var entryNumberTokens = ExtractNumberTokens(entry.BroadcasterText);
        var numberFilteredMatches = entryNumberTokens.Count == 0
            ? []
            : countryFilteredMatches
                .Where(item => item.Candidate.NumberTokens.Overlaps(entryNumberTokens))
                .ToList();

        var effectiveMatches = numberFilteredMatches.Count > 0
            ? numberFilteredMatches
            : countryFilteredMatches;

        var matchedChannels = effectiveMatches
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.Channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Candidate.Channel)
            .DistinctBy(channel => channel.StreamUri.ToString())
            .ToList();

        return new ListingChannelMatch(entry.Country, entry.BroadcasterText, matchedChannels);
    }

    private static IReadOnlyList<ListingRow> ParseListingEntries(IEnumerable<string>? extractedRows, string pageText, string? html)
    {
        var candidateLines = new List<string>();
        if (extractedRows is not null)
        {
            candidateLines.AddRange(extractedRows.Where(row => !string.IsNullOrWhiteSpace(row)));
        }

        if (string.IsNullOrWhiteSpace(pageText) && candidateLines.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ListingRow>();

        if (!string.IsNullOrWhiteSpace(html))
        {
            foreach (var htmlRow in ParseListingEntriesFromHtml(html))
            {
                var dedupeKey = $"{Normalize(htmlRow.Country)}|{Normalize(htmlRow.BroadcasterText)}";
                if (seen.Add(dedupeKey))
                {
                    rows.Add(htmlRow);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(pageText))
        {
            candidateLines.AddRange(pageText.Replace("\r", string.Empty).Split('\n'));
        }

        foreach (var rawLine in candidateLines)
        {
            if (!TryParseListingRow(rawLine, out var row))
            {
                continue;
            }

            var dedupeKey = $"{Normalize(row.Country)}|{Normalize(row.BroadcasterText)}";
            if (seen.Add(dedupeKey))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static IReadOnlyList<ListingRow> ParseListingEntriesFromHtml(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var rows = new List<ListingRow>();
        foreach (var heading in document.DocumentNode
                     .Descendants()
                     .Where(node => IsHeading(node.Name)))
        {
            var headingText = CleanDisplay(HtmlEntity.DeEntitize(heading.InnerText));
            if (headingText.Length == 0)
            {
                continue;
            }

            if (headingText.Contains("International Coverage", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var row in ExtractCountryRowsFromSection(heading))
                {
                    rows.Add(row);
                }
            }
            else if (headingText.Contains("Live Broadcasts", StringComparison.OrdinalIgnoreCase))
            {
                var channels = ExtractLiveBroadcastChannelsFromSection(heading);
                if (channels.Count > 0)
                {
                    rows.Add(new ListingRow("Live Broadcasts", string.Join(" | ", channels)));
                }
            }
        }

        return rows;
    }

    private static List<ListingRow> ExtractCountryRowsFromSection(HtmlNode heading)
    {
        var rows = new List<ListingRow>();
        foreach (var sectionNode in EnumerateSectionNodes(heading))
        {
            foreach (var candidate in sectionNode.DescendantsAndSelf().Where(node => node.NodeType == HtmlNodeType.Element))
            {
                var links = candidate.Descendants("a")
                    .Select(node => CleanDisplay(HtmlEntity.DeEntitize(node.InnerText)))
                    .Where(text => !string.IsNullOrWhiteSpace(text) && !text.Equals("Watch", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (links.Count == 0)
                {
                    continue;
                }

                var ownText = CleanDisplay(string.Concat(candidate.ChildNodes
                    .Where(node => node.NodeType == HtmlNodeType.Text)
                    .Select(node => HtmlEntity.DeEntitize(node.InnerText))));

                if (string.IsNullOrWhiteSpace(ownText)
                    || ownText.Length > 80
                    || IgnoredLeftSideValues.Contains(ownText)
                    || ownText.Equals("Watch", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                rows.Add(new ListingRow(ownText, string.Join(" | ", links)));
            }
        }

        return rows;
    }

    private static List<string> ExtractLiveBroadcastChannelsFromSection(HtmlNode heading)
    {
        var channels = new List<string>();
        foreach (var sectionNode in EnumerateSectionNodes(heading))
        {
            foreach (var link in sectionNode.Descendants("a"))
            {
                var text = CleanDisplay(HtmlEntity.DeEntitize(link.InnerText));
                if (string.IsNullOrWhiteSpace(text)
                    || text.Equals("Watch", StringComparison.OrdinalIgnoreCase)
                    || text.Length > 80)
                {
                    continue;
                }

                channels.Add(text);
            }
        }

        return channels
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<HtmlNode> EnumerateSectionNodes(HtmlNode heading)
    {
        var sibling = heading.NextSibling;
        while (sibling is not null)
        {
            if (sibling.NodeType == HtmlNodeType.Element && IsHeading(sibling.Name))
            {
                yield break;
            }

            if (sibling.NodeType == HtmlNodeType.Element)
            {
                yield return sibling;
            }

            sibling = sibling.NextSibling;
        }
    }

    private static bool IsHeading(string nodeName)
    {
        return string.Equals(nodeName, "h1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "h2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "h3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "h4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "h5", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nodeName, "h6", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseListingRow(string rawLine, out ListingRow row)
    {
        row = default;

        if (string.IsNullOrWhiteSpace(rawLine) || !rawLine.Contains('|'))
        {
            return false;
        }

        var parts = rawLine.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        var country = CleanDisplay(parts[0]);
        var broadcasters = CleanDisplay(parts[1]);

        if (string.IsNullOrWhiteSpace(country)
            || string.IsNullOrWhiteSpace(broadcasters)
            || country.Length > 60
            || broadcasters.Length > 260
            || country.Any(char.IsDigit)
            || broadcasters.Contains("http", StringComparison.OrdinalIgnoreCase)
            || broadcasters.Contains("www.", StringComparison.OrdinalIgnoreCase)
            || IgnoredLeftSideValues.Contains(country))
        {
            return false;
        }

        row = new ListingRow(country, broadcasters);
        return true;
    }

    private static List<WeightedToken> BuildCandidateTokens(PlaylistChannel channel)
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);
        var cleanedName = CleanupRegex.Replace(channel.Name, " ");
        var normalizedName = Normalize(channel.Name);
        var normalizedCleanName = Normalize(cleanedName);

        AddToken(tokens, normalizedName, 100);
        if (!string.Equals(normalizedName, normalizedCleanName, StringComparison.Ordinal))
        {
            AddToken(tokens, normalizedCleanName, 90);
        }

        AddToken(tokens, Normalize(channel.TvgId), 70);

        var rawTokens = cleanedName
            .Split([' ', '-', '_', '/', '|', '.', '+', '(', ')', '[', ']', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => Normalize(token))
            .Where(token => token.Length >= 2)
            .ToList();

        var meaningfulTokens = rawTokens
            .Where(token => !IsCountryAlias(token) && !IgnoredChannelTokens.Contains(token))
            .ToList();

        if (meaningfulTokens.Count > 0)
        {
            AddToken(tokens, string.Concat(meaningfulTokens), 80);
        }

        foreach (var normalizedToken in meaningfulTokens)
        {
            if (normalizedToken.Length >= 4)
            {
                AddToken(tokens, normalizedToken, 50 + Math.Min(20, normalizedToken.Length));
            }
            else if (normalizedToken.Length == 3)
            {
                AddToken(tokens, normalizedToken, 35);
            }
        }

        return tokens
            .Select(pair => new WeightedToken(pair.Key, pair.Value))
            .OrderByDescending(token => token.Weight)
            .ThenByDescending(token => token.Text.Length)
            .ToList();
    }

    private static HashSet<string> BuildCountryCodes(PlaylistChannel channel)
    {
        var countryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in EnumerateCountrySources(channel))
        {
            foreach (var token in EnumerateCountryTokens(source))
            {
                foreach (var code in ResolveCountryCodes(token))
                {
                    countryCodes.Add(code);
                }
            }
        }

        if (HasArabicRegionalGroupContext(channel))
        {
            countryCodes.Remove("AR");
        }

        return countryCodes;
    }

    private static HashSet<string> BuildNumberTokens(PlaylistChannel channel)
    {
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in EnumerateCountrySources(channel))
        {
            foreach (var number in ExtractNumberTokens(source))
            {
                numbers.Add(number);
            }
        }

        foreach (var number in ExtractNumberTokens(channel.Name))
        {
            numbers.Add(number);
        }

        return numbers;
    }

    private static void AddToken(IDictionary<string, int> tokens, string value, int weight)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!tokens.TryGetValue(value, out var existingWeight) || weight > existingWeight)
            {
                tokens[value] = weight;
            }
        }
    }

    private static bool IsCountryAlias(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && CountryAliasMap.ContainsKey(value);
    }

    private static HashSet<string> ExtractNumberTokens(string value)
    {
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return numbers;
        }

        foreach (Match match in Regex.Matches(value, @"\b\d{1,4}\b"))
        {
            var token = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                numbers.Add(token);
            }
        }

        return numbers;
    }

    private static IEnumerable<string> EnumerateCountrySources(PlaylistChannel channel)
    {
        if (!string.IsNullOrWhiteSpace(channel.GroupTitle))
        {
            yield return channel.GroupTitle;
        }

        if (!string.IsNullOrWhiteSpace(channel.GroupDisplayTitle))
        {
            yield return channel.GroupDisplayTitle;
        }

        if (!string.IsNullOrWhiteSpace(channel.Name))
        {
            var prefix = channel.Name.Split([':', '|', '-', '–', '—', '/'], 2, StringSplitOptions.TrimEntries)[0];
            if (!IsArabicRegionalMarker(prefix, channel.Name))
            {
                yield return prefix;
            }
        }

        if (!string.IsNullOrWhiteSpace(channel.GroupFlag))
        {
            var fileName = Path.GetFileNameWithoutExtension(channel.GroupFlag);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                yield return fileName;
            }
        }
    }

    private static IEnumerable<string> EnumerateCountryTokens(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            yield break;
        }

        yield return source;

        foreach (var token in source.Split([' ', '\t', ':', '|', '-', '–', '—', '/', '(', ')', '[', ']', ',', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return token;
        }
    }

    private static HashSet<string> ResolveCountryCodes(string source)
    {
        if (TryResolveArabicRegionalCountryCodes(source, out var arabicRegionalCodes))
        {
            return arabicRegionalCodes;
        }

        var segmentedCountryCodes = ResolveCountryCodesFromSegments(source);
        if (segmentedCountryCodes.Count > 0)
        {
            return segmentedCountryCodes;
        }

        var countryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in EnumerateCountryTokens(source))
        {
            var normalized = Normalize(token);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (CountryAliasMap.TryGetValue(normalized, out var code))
            {
                countryCodes.Add(code);
            }
        }

        return countryCodes;
    }

    private static bool TryResolveArabicRegionalCountryCodes(string source, out HashSet<string> countryCodes)
    {
        countryCodes = [];
        if (!IsArabicRegionalMarker(source, source))
        {
            return false;
        }

        var segments = source
            .Split(['|', ':', '-', '–', '—', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1);

        foreach (var segment in segments)
        {
            foreach (var code in ResolveCountryCodesFromTokens(segment))
            {
                countryCodes.Add(code);
            }
        }

        return true;
    }

    private static HashSet<string> ResolveCountryCodesFromSegments(string source)
    {
        if (string.IsNullOrWhiteSpace(source) || !source.Contains('|'))
        {
            return [];
        }

        var segments = source
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => new
            {
                Segment = segment,
                Codes = ResolveCountryCodesFromTokens(segment)
            })
            .Where(item => item.Codes.Count > 0)
            .ToList();

        if (segments.Count == 0)
        {
            return [];
        }

        var explicitSegmentCodes = segments
            .Where(item => HasExplicitCountrySignal(item.Segment))
            .SelectMany(item => item.Codes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (explicitSegmentCodes.Count > 0)
        {
            return explicitSegmentCodes;
        }

        var laterSegmentCodes = segments
            .Skip(1)
            .SelectMany(item => item.Codes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return laterSegmentCodes.Count > 0
            ? laterSegmentCodes
            : segments.SelectMany(item => item.Codes).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ResolveCountryCodesFromTokens(string source)
    {
        var countryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in EnumerateCountryTokens(source))
        {
            var normalized = Normalize(token);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (CountryAliasMap.TryGetValue(normalized, out var code))
            {
                countryCodes.Add(code);
            }
        }

        return countryCodes;
    }

    private static bool HasExplicitCountrySignal(string source)
    {
        var normalized = Normalize(source);
        return normalized.Length >= 3 || source.Any(char.IsWhiteSpace);
    }

    private static bool HasArabicRegionalGroupContext(PlaylistChannel channel)
    {
        return IsArabicRegionalMarker(channel.GroupTitle, channel.GroupTitle)
            || IsArabicRegionalMarker(channel.GroupDisplayTitle, channel.GroupDisplayTitle);
    }

    private static bool IsArabicRegionalMarker(string value, string fullSource)
    {
        if (string.IsNullOrWhiteSpace(fullSource))
        {
            return false;
        }

        var trimmedSource = fullSource.Trim();
        if (trimmedSource.StartsWith("AR|", StringComparison.CurrentCultureIgnoreCase)
            || trimmedSource.StartsWith("AR:", StringComparison.CurrentCultureIgnoreCase)
            || trimmedSource.StartsWith("AR/", StringComparison.CurrentCultureIgnoreCase)
            || trimmedSource.StartsWith("AR -", StringComparison.CurrentCultureIgnoreCase)
            || trimmedSource.StartsWith("AR –", StringComparison.CurrentCultureIgnoreCase)
            || trimmedSource.StartsWith("AR —", StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }

        return string.Equals(Normalize(value), "AR", StringComparison.Ordinal)
            && (trimmedSource.Contains('|') || trimmedSource.Contains(':') || trimmedSource.Contains('/'));
    }

    private static IReadOnlyDictionary<string, string> BuildCountryAliasMap()
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

        AddCountryAlias(aliases, "UK", "GB");
        AddCountryAlias(aliases, "United Kingdom", "GB");
        AddCountryAlias(aliases, "England", "GB");
        AddCountryAlias(aliases, "USA", "US");
        AddCountryAlias(aliases, "United States", "US");
        AddCountryAlias(aliases, "Congo", "CG");
        AddCountryAlias(aliases, "Congo DR", "CD");
        AddCountryAlias(aliases, "DR Congo", "CD");
        AddCountryAlias(aliases, "Cape Verde Islands", "CV");
        AddCountryAlias(aliases, "Cape Verde", "CV");
        AddCountryAlias(aliases, "Cabo Verde", "CV");
        AddCountryAlias(aliases, "Bosnia and Herzegovina", "BA");
        AddCountryAlias(aliases, "Ivory Coast", "CI");

        return aliases;
    }

    private static void AddCountryAlias(IDictionary<string, string> aliases, string alias, string code)
    {
        var normalizedAlias = Normalize(alias);
        var normalizedCode = Normalize(code);
        if (normalizedAlias.Length == 0 || normalizedCode.Length != 2)
        {
            return;
        }

        aliases[normalizedAlias] = normalizedCode;
    }

    private static string CleanDisplay(string value)
    {
        return WhitespaceRegex.Replace((value ?? string.Empty).Trim(), " ");
    }

    private static string Normalize(string value)
    {
        return NormalizeRegex.Replace((value ?? string.Empty).ToUpperInvariant(), string.Empty).Trim();
    }

    private readonly record struct ListingRow(string Country, string BroadcasterText);

    private sealed record ChannelCandidate(PlaylistChannel Channel, List<WeightedToken> Tokens, HashSet<string> CountryCodes, HashSet<string> NumberTokens)
    {
        public int MatchScore(string normalizedBroadcasters)
        {
            if (string.IsNullOrWhiteSpace(normalizedBroadcasters))
            {
                return 0;
            }

            var score = 0;
            foreach (var token in Tokens)
            {
                if (token.Text.Length >= 3 && normalizedBroadcasters.Contains(token.Text, StringComparison.Ordinal))
                {
                    score += token.Weight;
                }
            }

            return score;
        }
    }

    private sealed record WeightedToken(string Text, int Weight);

    private sealed record ScoredCandidate(ChannelCandidate Candidate, int Score);
}

public sealed class ListingChannelMatch
{
    public ListingChannelMatch(string country, string broadcasterText, IReadOnlyList<PlaylistChannel> matchedChannels)
    {
        Country = country;
        BroadcasterText = broadcasterText;
        MatchedChannels = matchedChannels;
    }

    public string Country { get; }

    public string BroadcasterText { get; }

    public IReadOnlyList<PlaylistChannel> MatchedChannels { get; }

    public int MatchCount => MatchedChannels.Count;

    public string MatchPreview => MatchCount == 0
        ? "No playlist match"
        : string.Join(", ", MatchedChannels.Take(3).Select(channel => channel.Name))
          + (MatchCount > 3 ? $" (+{MatchCount - 3} more)" : string.Empty);
}

public sealed class TemporaryChannelListSelection
{
    public TemporaryChannelListSelection(string label, IReadOnlyList<string> streamKeys)
    {
        Label = label;
        StreamKeys = streamKeys;
    }

    public string Label { get; }

    public IReadOnlyList<string> StreamKeys { get; }
}
