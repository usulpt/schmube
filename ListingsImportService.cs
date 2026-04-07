using System;
using System.Collections.Generic;
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

    private readonly IReadOnlyList<ChannelCandidate> _channelCandidates;

    public ListingsImportService(IReadOnlyList<PlaylistChannel> availableChannels)
    {
        _channelCandidates = availableChannels
            .Select(channel => new ChannelCandidate(channel, BuildCandidateTokens(channel)))
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
        var matchedChannels = _channelCandidates
            .Where(candidate => candidate.Matches(normalizedBroadcasters))
            .Select(candidate => candidate.Channel)
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

    private static HashSet<string> BuildCandidateTokens(PlaylistChannel channel)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        AddToken(tokens, Normalize(channel.Name));
        AddToken(tokens, Normalize(CleanupRegex.Replace(channel.Name, " ")));
        AddToken(tokens, Normalize(channel.TvgId));

        foreach (var rawToken in CleanupRegex.Replace(channel.Name, " ")
                     .Split([' ', '-', '_', '/', '|', '.', '+', '(', ')', '[', ']', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedToken = Normalize(rawToken);
            if (normalizedToken.Length >= 4 && !IgnoredChannelTokens.Contains(normalizedToken))
            {
                AddToken(tokens, normalizedToken);
            }
        }

        return tokens;
    }

    private static void AddToken(ISet<string> tokens, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tokens.Add(value);
        }
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

    private sealed record ChannelCandidate(PlaylistChannel Channel, HashSet<string> Tokens)
    {
        public bool Matches(string normalizedBroadcasters)
        {
            if (string.IsNullOrWhiteSpace(normalizedBroadcasters))
            {
                return false;
            }

            foreach (var token in Tokens.OrderByDescending(token => token.Length))
            {
                if (token.Length >= 3 && normalizedBroadcasters.Contains(token, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
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
