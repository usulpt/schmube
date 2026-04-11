using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Schmube;

public sealed class CompatibleChannelListService
{
    private readonly MatchBroadcastSourceCatalog _sourceCatalog;
    private readonly PlaylistChannelMatcher _matcher;

    public CompatibleChannelListService(IReadOnlyList<PlaylistChannel> availableChannels, ChannelMatchingConfig config)
    {
        _sourceCatalog = new MatchBroadcastSourceCatalog();
        _matcher = new PlaylistChannelMatcher(availableChannels, config);
    }

    public CompatibleChannelListResult Generate(MatchPageSnapshot snapshot)
    {
        var extraction = _sourceCatalog.Extract(snapshot);
        var offerMatches = extraction.Offers
            .Select(offer => new BroadcastOfferMatch(offer, _matcher.Match(offer)))
            .ToList();

        var generatedChannels = offerMatches
            .SelectMany(match => match.Matches.Select(channelMatch => new { match.Offer, ChannelMatch = channelMatch }))
            .GroupBy(item => item.ChannelMatch.Channel.StreamUri.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(item => item.ChannelMatch.Score)
                    .First();

                var sourceOffers = group
                    .Select(item => item.Offer)
                    .DistinctBy(offer => $"{offer.CountryCode}|{offer.StationName}", StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new CompatiblePlaylistChannel(
                    best.ChannelMatch.Channel,
                    sourceOffers,
                    best.ChannelMatch.Score,
                    best.ChannelMatch.Reason);
            })
            .OrderByDescending(channel => channel.BestScore)
            .ThenByDescending(channel => channel.SourceOffers.Count)
            .ThenBy(channel => channel.ChannelName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var diagnostics = extraction.Diagnostics
            .Concat([ $"Matched {offerMatches.Count(match => match.MatchCount > 0)} of {offerMatches.Count} broadcaster offers against the current playlist." ])
            .ToList();

        return new CompatibleChannelListResult(
            string.IsNullOrWhiteSpace(extraction.MatchTitle) ? "TV listings" : extraction.MatchTitle.Trim(),
            extraction.SourceName,
            offerMatches,
            generatedChannels,
            diagnostics);
    }
}

public sealed class MatchBroadcastSourceCatalog
{
    private readonly IReadOnlyList<IMatchBroadcastSource> _sources =
    [
        new LiveSoccerTvBroadcastSource(),
        new GenericBroadcastPageSource()
    ];

    public BroadcastOfferExtractionResult Extract(MatchPageSnapshot snapshot)
    {
        var pageUri = Uri.TryCreate(snapshot.Url, UriKind.Absolute, out var parsedUri) ? parsedUri : null;

        if (pageUri is not null)
        {
            foreach (var source in _sources.Where(source => source.CanHandle(pageUri)))
            {
                var result = source.Extract(snapshot);
                if (result.Offers.Count > 0)
                {
                    return result;
                }
            }
        }

        return _sources[^1].Extract(snapshot);
    }
}

public interface IMatchBroadcastSource
{
    string Name { get; }

    bool CanHandle(Uri uri);

    BroadcastOfferExtractionResult Extract(MatchPageSnapshot snapshot);
}

public sealed class LiveSoccerTvBroadcastSource : IMatchBroadcastSource
{
    public string Name => "Live Soccer TV";

    public bool CanHandle(Uri uri)
    {
        return uri.Host.Contains("livesoccertv.com", StringComparison.OrdinalIgnoreCase);
    }

    public BroadcastOfferExtractionResult Extract(MatchPageSnapshot snapshot)
    {
        var diagnostics = new List<string>
        {
            $"Source adapter: {Name}"
        };

        var document = LoadDocument(snapshot.Html);
        if (document is null)
        {
            diagnostics.Add("Snapshot HTML was empty, so source extraction could not inspect the DOM.");
            return new BroadcastOfferExtractionResult(Name, BuildMatchTitle(snapshot.Title), snapshot.Url, [], diagnostics);
        }

        var offers = new List<BroadcastOffer>();
        offers.AddRange(ExtractFromSection(document, "Live Broadcasts", "Live Broadcasts"));
        offers.AddRange(ExtractFromSection(document, "International Coverage", "International Coverage"));

        if (offers.Count == 0)
        {
            diagnostics.Add("No offers were detected from the Live Broadcasts or International Coverage sections.");
        }
        else
        {
            diagnostics.Add($"Extracted {offers.Count} broadcaster offers from the rendered page DOM.");
        }

        return new BroadcastOfferExtractionResult(
            Name,
            BuildMatchTitle(ExtractHeadingText(document) ?? snapshot.Title),
            snapshot.Url,
            DeduplicateOffers(offers),
            diagnostics);
    }

    private IEnumerable<BroadcastOffer> ExtractFromSection(HtmlDocument document, string headingText, string sectionLabel)
    {
        var offers = new List<BroadcastOffer>();
        foreach (var heading in document.DocumentNode
                     .Descendants()
                     .Where(node => IsHeading(node.Name) && Clean(node.InnerText).Contains(headingText, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var sectionNode in EnumerateSectionNodes(heading))
            {
                offers.AddRange(ExtractCountryOffersFromNode(sectionNode, sectionLabel));

                if (string.Equals(sectionLabel, "Live Broadcasts", StringComparison.Ordinal))
                {
                    offers.AddRange(ExtractLooseStationOffers(sectionNode, sectionLabel));
                }
            }
        }

        return offers;
    }

    private static IEnumerable<BroadcastOffer> ExtractCountryOffersFromNode(HtmlNode node, string sectionLabel)
    {
        foreach (var candidate in node
                     .DescendantsAndSelf()
                     .Where(current => current.NodeType == HtmlNodeType.Element))
        {
            var links = candidate.Descendants("a")
                .Select(link => new
                {
                    Text = Clean(HtmlEntity.DeEntitize(link.InnerText)),
                    Href = link.GetAttributeValue("href", string.Empty)
                })
                .Where(link => IsStationName(link.Text))
                .DistinctBy(link => link.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (links.Count == 0)
            {
                continue;
            }

            var ownText = Clean(string.Concat(candidate.ChildNodes
                .Where(child => child.NodeType == HtmlNodeType.Text)
                .Select(child => HtmlEntity.DeEntitize(child.InnerText))));

            if (!IsCountryName(ownText))
            {
                continue;
            }

            var countryCode = CountryResolver.ResolveCountryCode(ownText);
            foreach (var link in links)
            {
                yield return new BroadcastOffer(
                    "Live Soccer TV",
                    sectionLabel,
                    ownText,
                    countryCode,
                    link.Text,
                    link.Href,
                    $"{ownText} | {link.Text}");
            }
        }
    }

    private static IEnumerable<BroadcastOffer> ExtractLooseStationOffers(HtmlNode node, string sectionLabel)
    {
        foreach (var link in node.Descendants("a"))
        {
            var text = Clean(HtmlEntity.DeEntitize(link.InnerText));
            if (!IsStationName(text))
            {
                continue;
            }

            yield return new BroadcastOffer(
                "Live Soccer TV",
                sectionLabel,
                string.Empty,
                string.Empty,
                text,
                link.GetAttributeValue("href", string.Empty),
                text);
        }
    }

    private static IReadOnlyList<BroadcastOffer> DeduplicateOffers(IEnumerable<BroadcastOffer> offers)
    {
        return offers
            .DistinctBy(offer => $"{Normalize(offer.CountryName)}|{Normalize(offer.StationName)}", StringComparer.Ordinal)
            .ToList();
    }

    private static string? ExtractHeadingText(HtmlDocument document)
    {
        return document.DocumentNode
            .Descendants()
            .Where(node => string.Equals(node.Name, "h1", StringComparison.OrdinalIgnoreCase))
            .Select(node => Clean(HtmlEntity.DeEntitize(node.InnerText)))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static HtmlDocument? LoadDocument(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document;
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

    private static string BuildMatchTitle(string value)
    {
        var cleaned = Clean(value);
        cleaned = cleaned.Replace("stream and TV schedule", string.Empty, StringComparison.OrdinalIgnoreCase);
        cleaned = cleaned.Replace("Where to Watch", string.Empty, StringComparison.OrdinalIgnoreCase);
        return Clean(cleaned);
    }

    private static bool IsCountryName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 60 || value.Any(char.IsDigit))
        {
            return false;
        }

        return !StationNoiseTokens.Contains(Normalize(value));
    }

    private static bool IsStationName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 90)
        {
            return false;
        }

        return !IgnoredStationValues.Contains(value)
               && !value.Contains("Image:", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("Watch", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("All Channels", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("let us know", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string value)
    {
        return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
    }

    private static string Normalize(string value)
    {
        return Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", string.Empty);
    }

    private static readonly HashSet<string> IgnoredStationValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "Watch",
        "Live",
        "Image",
        "All Channels"
    };

    private static readonly HashSet<string> StationNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "LIVEBROADCASTS",
        "INTERNATIONALCOVERAGE",
        "MATCHDETAILS",
        "CONTENTDISCLAIMER"
    };
}

public sealed class GenericBroadcastPageSource : IMatchBroadcastSource
{
    public string Name => "Generic Match Page";

    public bool CanHandle(Uri uri)
    {
        return true;
    }

    public BroadcastOfferExtractionResult Extract(MatchPageSnapshot snapshot)
    {
        var diagnostics = new List<string>
        {
            $"Source adapter: {Name}"
        };

        var document = string.IsNullOrWhiteSpace(snapshot.Html) ? null : new HtmlDocument();
        if (document is not null)
        {
            document.LoadHtml(snapshot.Html);
        }

        var offers = new List<BroadcastOffer>();
        if (document is not null)
        {
            offers.AddRange(ExtractTableOffers(document));
            offers.AddRange(ExtractDefinitionListOffers(document));
        }

        var deduped = offers
            .Where(offer => !string.IsNullOrWhiteSpace(offer.StationName))
            .DistinctBy(offer => $"{Normalize(offer.CountryName)}|{Normalize(offer.StationName)}", StringComparer.Ordinal)
            .ToList();

        diagnostics.Add(deduped.Count == 0
            ? "No structured broadcaster rows were found by the generic extractor."
            : $"Extracted {deduped.Count} broadcaster offers from generic table and definition-list parsing.");

        return new BroadcastOfferExtractionResult(
            Name,
            Clean(snapshot.Title),
            snapshot.Url,
            deduped,
            diagnostics);
    }

    private static IEnumerable<BroadcastOffer> ExtractTableOffers(HtmlDocument document)
    {
        foreach (var row in document.DocumentNode.Descendants("tr"))
        {
            var cells = row.ChildNodes
                .Where(node => node.NodeType == HtmlNodeType.Element
                            && (string.Equals(node.Name, "th", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(node.Name, "td", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (cells.Count < 2)
            {
                continue;
            }

            var market = Clean(HtmlEntity.DeEntitize(cells[0].InnerText));
            if (string.IsNullOrWhiteSpace(market) || market.Length > 60)
            {
                continue;
            }

            var stations = cells
                .Skip(1)
                .SelectMany(cell => cell.Descendants("a").Any()
                    ? cell.Descendants("a").Select(link => (Text: Clean(HtmlEntity.DeEntitize(link.InnerText)), Url: link.GetAttributeValue("href", string.Empty)))
                    : [ (Text: Clean(HtmlEntity.DeEntitize(cell.InnerText)), Url: string.Empty) ])
                .Where(station => station.Text.Length > 1)
                .DistinctBy(station => station.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var station in stations)
            {
                yield return new BroadcastOffer(
                    "Generic Match Page",
                    "Table",
                    market,
                    CountryResolver.ResolveCountryCode(market),
                    station.Text,
                    station.Url,
                    $"{market} | {station.Text}");
            }
        }
    }

    private static IEnumerable<BroadcastOffer> ExtractDefinitionListOffers(HtmlDocument document)
    {
        foreach (var list in document.DocumentNode.Descendants("dl"))
        {
            var terms = list.ChildNodes
                .Where(node => node.NodeType == HtmlNodeType.Element)
                .ToList();
            for (var index = 0; index < terms.Count - 1; index++)
            {
                if (!string.Equals(terms[index].Name, "dt", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(terms[index + 1].Name, "dd", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var market = Clean(HtmlEntity.DeEntitize(terms[index].InnerText));
                if (string.IsNullOrWhiteSpace(market))
                {
                    continue;
                }

                var stations = terms[index + 1].Descendants("a")
                    .Select(link => new
                    {
                        Text = Clean(HtmlEntity.DeEntitize(link.InnerText)),
                        Url = link.GetAttributeValue("href", string.Empty)
                    })
                    .Where(link => link.Text.Length > 1)
                    .DistinctBy(link => link.Text, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var station in stations)
                {
                    yield return new BroadcastOffer(
                        "Generic Match Page",
                        "Definition List",
                        market,
                        CountryResolver.ResolveCountryCode(market),
                        station.Text,
                        station.Url,
                        $"{market} | {station.Text}");
                }
            }
        }
    }

    private static string Clean(string value)
    {
        return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
    }

    private static string Normalize(string value)
    {
        return Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", string.Empty);
    }
}

public sealed class PlaylistChannelMatcher
{
    private static readonly Regex CleanupRegex = new(@"\b(HD|FHD|UHD|4K|SD|HEVC|H264|H265|FULLHD)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> IgnoredTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "TV",
        "HD",
        "LIVE",
        "SPORT",
        "SPORTS",
        "CHANNEL",
        "NETWORK",
        "DIGITAL",
        "ONLINE"
    };

    private readonly IReadOnlyList<ChannelCandidate> _candidates;
    private readonly ChannelMatchingConfig _config;

    public PlaylistChannelMatcher(IReadOnlyList<PlaylistChannel> availableChannels, ChannelMatchingConfig config)
    {
        _config = config;
        _candidates = availableChannels
            .Select(channel => new ChannelCandidate(
                channel,
                Normalize(channel.Name),
                Normalize(CleanupRegex.Replace(channel.Name, " ")),
                Tokenize(channel.Name),
                Tokenize(channel.TvgId),
                BuildCountryCodes(channel),
                ExtractNumberTokens(channel.Name)))
            .ToList();
    }

    public IReadOnlyList<PlaylistChannelMatchResult> Match(BroadcastOffer offer)
    {
        if (ShouldIgnoreOffer(offer))
        {
            return [];
        }

        var applicableAliases = _config.Aliases
            .Where(alias => AliasMatches(alias, offer))
            .ToList();

        var results = _candidates
            .Select(candidate => ScoreCandidate(candidate, offer, applicableAliases))
            .Where(result => result is not null)
            .Cast<PlaylistChannelMatchResult>()
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(8)
            .ToList();

        return results;
    }

    private PlaylistChannelMatchResult? ScoreCandidate(
        ChannelCandidate candidate,
        BroadcastOffer offer,
        IReadOnlyList<ChannelAliasRule> aliases)
    {
        var normalizedOffer = Normalize(offer.StationName);
        if (normalizedOffer.Length == 0)
        {
            return null;
        }

        var offerTokens = Tokenize(offer.StationName);
        var offerNumberTokens = ExtractNumberTokens(offer.StationName);
        var score = 0;
        var reasons = new List<string>();

        if (string.Equals(candidate.NormalizedName, normalizedOffer, StringComparison.Ordinal)
            || string.Equals(candidate.NormalizedCleanName, normalizedOffer, StringComparison.Ordinal))
        {
            score += 280;
            reasons.Add("exact station name");
        }
        else if (candidate.NormalizedName.Contains(normalizedOffer, StringComparison.Ordinal)
                 || normalizedOffer.Contains(candidate.NormalizedCleanName, StringComparison.Ordinal))
        {
            if (Math.Min(candidate.NormalizedCleanName.Length, normalizedOffer.Length) >= 5)
            {
                score += 150;
                reasons.Add("strong name overlap");
            }
        }

        var tvgIdNormalized = Normalize(candidate.Channel.TvgId);
        if (!string.IsNullOrWhiteSpace(tvgIdNormalized)
            && (tvgIdNormalized.Contains(normalizedOffer, StringComparison.Ordinal)
                || normalizedOffer.Contains(tvgIdNormalized, StringComparison.Ordinal)))
        {
            score += 170;
            reasons.Add("tvg-id similarity");
        }

        var overlappingTokens = candidate.NameTokens.Intersect(offerTokens, StringComparer.OrdinalIgnoreCase).ToList();
        if (overlappingTokens.Count > 0)
        {
            score += 35 * overlappingTokens.Count;
            reasons.Add($"shared tokens: {string.Join(", ", overlappingTokens.Take(3))}");
        }

        if (offerTokens.Count > 0 && offerTokens.All(token => candidate.NameTokens.Contains(token)))
        {
            score += 50;
            reasons.Add("all broadcaster tokens present");
        }

        if (offerNumberTokens.Count > 0 && candidate.NumberTokens.Overlaps(offerNumberTokens))
        {
            score += 45;
            reasons.Add("channel number overlap");
        }

        if (!string.IsNullOrWhiteSpace(offer.CountryCode) && candidate.CountryCodes.Contains(offer.CountryCode))
        {
            score += 35;
            reasons.Add($"country hint {offer.CountryCode}");
        }

        var aliasBoost = 0;
        foreach (var alias in aliases)
        {
            if (CandidateMatchesAlias(alias, candidate))
            {
                aliasBoost = Math.Max(aliasBoost, alias.ScoreBoost);
            }
        }

        if (aliasBoost > 0)
        {
            score += aliasBoost;
            reasons.Add("alias rule");
        }

        var minimumScore = aliasBoost > 0 ? 70 : 110;
        if (score < minimumScore)
        {
            return null;
        }

        return new PlaylistChannelMatchResult(candidate.Channel, score, string.Join("; ", reasons));
    }

    private bool ShouldIgnoreOffer(BroadcastOffer offer)
    {
        var normalizedStation = Normalize(offer.StationName);
        return _config.IgnoredOfferNames
            .Select(Normalize)
            .Contains(normalizedStation);
    }

    private static bool AliasMatches(ChannelAliasRule alias, BroadcastOffer offer)
    {
        if (string.IsNullOrWhiteSpace(alias.OfferPattern))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(alias.CountryCode)
            && !string.Equals(alias.CountryCode.Trim(), offer.CountryCode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (alias.IsRegex)
        {
            return Regex.IsMatch(offer.StationName, alias.OfferPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return offer.StationName.Contains(alias.OfferPattern, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool CandidateMatchesAlias(ChannelAliasRule alias, ChannelCandidate candidate)
    {
        var channelName = candidate.Channel.Name;
        var groupName = string.IsNullOrWhiteSpace(candidate.Channel.GroupDisplayTitle)
            ? candidate.Channel.GroupTitle
            : candidate.Channel.GroupDisplayTitle;

        if (alias.ExcludeChannelNameContains.Any(exclude =>
                channelName.Contains(exclude, StringComparison.CurrentCultureIgnoreCase)))
        {
            return false;
        }

        var matchesName = alias.ChannelNameContains.Count == 0
                          || alias.ChannelNameContains.Any(term => channelName.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        var matchesId = alias.ChannelIds.Count == 0
                        || alias.ChannelIds.Any(term => candidate.Channel.TvgId.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        var matchesGroup = alias.ChannelGroupContains.Count == 0
                           || alias.ChannelGroupContains.Any(term => groupName.Contains(term, StringComparison.CurrentCultureIgnoreCase));

        return matchesName && matchesId && matchesGroup;
    }

    private static HashSet<string> BuildCountryCodes(PlaylistChannel channel)
    {
        if (IsAsia24SevenGroup(channel.GroupTitle))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IN" };
        }

        var countryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in ResolveRegionalCountryCodes(channel.GroupTitle, "LA"))
        {
            countryCodes.Add(code);
        }

        foreach (var source in EnumerateCountrySources(channel))
        {
            foreach (var code in CountryResolver.ResolveCountryCodes(source))
            {
                countryCodes.Add(code);
            }
        }

        return countryCodes;
    }

    private static IEnumerable<string> ResolveRegionalCountryCodes(string groupTitle, string regionMarker)
    {
        var trimmedGroup = groupTitle?.Trim() ?? string.Empty;
        if (!StartsWithRegionalMarker(trimmedGroup, regionMarker))
        {
            yield break;
        }

        foreach (var segment in trimmedGroup
                     .Split([':', '|', '-', '–', '—', '/'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                     .Skip(1))
        {
            foreach (var code in CountryResolver.ResolveCountryCodes(segment))
            {
                yield return code;
            }
        }
    }

    private static bool IsAsia24SevenGroup(string groupTitle)
    {
        return (groupTitle ?? string.Empty).Trim().StartsWith("ASIA| 24/7", StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool StartsWithRegionalMarker(string value, string regionMarker)
    {
        return value.StartsWith($"{regionMarker}|", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{regionMarker}:", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{regionMarker}/", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{regionMarker} -", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{regionMarker} –", StringComparison.CurrentCultureIgnoreCase)
            || value.StartsWith($"{regionMarker} —", StringComparison.CurrentCultureIgnoreCase);
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
            yield return prefix;
        }

        if (!string.IsNullOrWhiteSpace(channel.GroupFlag))
        {
            var flagCode = Path.GetFileNameWithoutExtension(channel.GroupFlag);
            if (!string.IsNullOrWhiteSpace(flagCode))
            {
                yield return flagCode;
            }
        }
    }

    private static HashSet<string> Tokenize(string value)
    {
        var cleaned = CleanupRegex.Replace(value ?? string.Empty, " ");
        return cleaned
            .Split([' ', '-', '_', '/', '|', '.', '+', '(', ')', '[', ']', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(token => token.Length >= 2 && !IgnoredTokens.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> ExtractNumberTokens(string value)
    {
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(value ?? string.Empty, @"\b\d{1,4}\b"))
        {
            numbers.Add(match.Value);
        }

        return numbers;
    }

    private static string Normalize(string value)
    {
        return Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", string.Empty);
    }

    private sealed record ChannelCandidate(
        PlaylistChannel Channel,
        string NormalizedName,
        string NormalizedCleanName,
        HashSet<string> NameTokens,
        HashSet<string> TvgTokens,
        HashSet<string> CountryCodes,
        HashSet<string> NumberTokens);
}

public static class CountryResolver
{
    private static readonly IReadOnlyDictionary<string, string> CountryAliasMap = BuildCountryAliasMap();

    public static string ResolveCountryCode(string source)
    {
        return ResolveCountryCodes(source).FirstOrDefault() ?? string.Empty;
    }

    public static IReadOnlyCollection<string> ResolveCountryCodes(string source)
    {
        var countryCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(source))
        {
            return countryCodes;
        }

        foreach (var token in EnumerateTokens(source))
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

    private static IEnumerable<string> EnumerateTokens(string source)
    {
        yield return source;

        foreach (var token in source.Split([' ', '\t', ':', '|', '-', '–', '—', '/', '(', ')', '[', ']', ',', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return token;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildCountryAliasMap()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                AddAlias(aliases, region.TwoLetterISORegionName, region.TwoLetterISORegionName);
                AddAlias(aliases, region.ThreeLetterISORegionName, region.TwoLetterISORegionName);
                AddAlias(aliases, region.EnglishName, region.TwoLetterISORegionName);
                AddAlias(aliases, region.NativeName, region.TwoLetterISORegionName);
                AddAlias(aliases, region.Name, region.TwoLetterISORegionName);
            }
            catch
            {
            }
        }

        AddAlias(aliases, "UK", "GB");
        AddAlias(aliases, "United Kingdom", "GB");
        AddAlias(aliases, "England", "GB");
        AddAlias(aliases, "Great Britain", "GB");
        AddAlias(aliases, "USA", "US");
        AddAlias(aliases, "United States", "US");
        AddAlias(aliases, "Congo", "CG");
        AddAlias(aliases, "Congo DR", "CD");
        AddAlias(aliases, "DR Congo", "CD");
        AddAlias(aliases, "Cabo Verde", "CV");
        AddAlias(aliases, "Cape Verde", "CV");
        AddAlias(aliases, "Cape Verde Islands", "CV");
        AddAlias(aliases, "Bosnia and Herzegovina", "BA");
        AddAlias(aliases, "Ivory Coast", "CI");

        return aliases;
    }

    private static void AddAlias(IDictionary<string, string> aliases, string alias, string code)
    {
        var normalizedAlias = Normalize(alias);
        var normalizedCode = Normalize(code);
        if (normalizedAlias.Length == 0 || normalizedCode.Length != 2)
        {
            return;
        }

        aliases[normalizedAlias] = normalizedCode;
    }

    private static string Normalize(string value)
    {
        return Regex.Replace((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+", string.Empty);
    }
}
