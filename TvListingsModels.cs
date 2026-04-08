using System;
using System.Collections.Generic;
using System.Linq;

namespace Schmube;

public sealed class MatchPageSnapshot
{
    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Html { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string ReadyState { get; set; } = string.Empty;
}

public sealed class BroadcastOffer
{
    public BroadcastOffer(
        string sourceName,
        string sectionLabel,
        string countryName,
        string countryCode,
        string stationName,
        string stationUrl,
        string rawText)
    {
        SourceName = sourceName;
        SectionLabel = sectionLabel;
        CountryName = countryName;
        CountryCode = countryCode;
        StationName = stationName;
        StationUrl = stationUrl;
        RawText = rawText;
    }

    public string SourceName { get; }

    public string SectionLabel { get; }

    public string CountryName { get; }

    public string CountryCode { get; }

    public string StationName { get; }

    public string StationUrl { get; }

    public string RawText { get; }

    public string CountryDisplay => string.IsNullOrWhiteSpace(CountryName) ? "Unknown" : CountryName;
}

public sealed class BroadcastOfferExtractionResult
{
    public BroadcastOfferExtractionResult(
        string sourceName,
        string matchTitle,
        string pageUrl,
        IReadOnlyList<BroadcastOffer> offers,
        IReadOnlyList<string> diagnostics)
    {
        SourceName = sourceName;
        MatchTitle = matchTitle;
        PageUrl = pageUrl;
        Offers = offers;
        Diagnostics = diagnostics;
    }

    public string SourceName { get; }

    public string MatchTitle { get; }

    public string PageUrl { get; }

    public IReadOnlyList<BroadcastOffer> Offers { get; }

    public IReadOnlyList<string> Diagnostics { get; }
}

public sealed class PlaylistChannelMatchResult
{
    public PlaylistChannelMatchResult(PlaylistChannel channel, int score, string reason)
    {
        Channel = channel;
        Score = score;
        Reason = reason;
    }

    public PlaylistChannel Channel { get; }

    public int Score { get; }

    public string Reason { get; }
}

public sealed class BroadcastOfferMatch
{
    public BroadcastOfferMatch(BroadcastOffer offer, IReadOnlyList<PlaylistChannelMatchResult> matches)
    {
        Offer = offer;
        Matches = matches;
    }

    public BroadcastOffer Offer { get; }

    public IReadOnlyList<PlaylistChannelMatchResult> Matches { get; }

    public string Country => Offer.CountryDisplay;

    public string Broadcaster => Offer.StationName;

    public string Source => Offer.SourceName;

    public int MatchCount => Matches.Count;

    public string TopMatch => MatchCount == 0 ? "No playlist match" : Matches[0].Channel.Name;

    public string MatchReason => MatchCount == 0 ? "No compatible channel found" : Matches[0].Reason;
}

public sealed class CompatiblePlaylistChannel
{
    public CompatiblePlaylistChannel(
        PlaylistChannel channel,
        IReadOnlyList<BroadcastOffer> sourceOffers,
        int bestScore,
        string matchReason)
    {
        Channel = channel;
        SourceOffers = sourceOffers;
        BestScore = bestScore;
        MatchReason = matchReason;
    }

    public PlaylistChannel Channel { get; }

    public IReadOnlyList<BroadcastOffer> SourceOffers { get; }

    public int BestScore { get; }

    public string MatchReason { get; }

    public string ChannelName => Channel.Name;

    public string ChannelGroup => string.IsNullOrWhiteSpace(Channel.GroupDisplayTitle)
        ? Channel.GroupTitle
        : Channel.GroupDisplayTitle;

    public string StreamKey => Channel.StreamUri.ToString();

    public string OfferPreview
    {
        get
        {
            if (SourceOffers.Count == 0)
            {
                return string.Empty;
            }

            var preview = SourceOffers
                .Take(3)
                .Select(offer => string.IsNullOrWhiteSpace(offer.CountryName)
                    ? offer.StationName
                    : $"{offer.CountryName}: {offer.StationName}")
                .ToList();

            return string.Join(", ", preview)
                   + (SourceOffers.Count > 3 ? $" (+{SourceOffers.Count - 3} more)" : string.Empty);
        }
    }
}

public sealed class CompatibleChannelListResult
{
    public CompatibleChannelListResult(
        string label,
        string sourceName,
        IReadOnlyList<BroadcastOfferMatch> offerMatches,
        IReadOnlyList<CompatiblePlaylistChannel> generatedChannels,
        IReadOnlyList<string> diagnostics)
    {
        Label = label;
        SourceName = sourceName;
        OfferMatches = offerMatches;
        GeneratedChannels = generatedChannels;
        Diagnostics = diagnostics;
    }

    public string Label { get; }

    public string SourceName { get; }

    public IReadOnlyList<BroadcastOfferMatch> OfferMatches { get; }

    public IReadOnlyList<CompatiblePlaylistChannel> GeneratedChannels { get; }

    public IReadOnlyList<string> Diagnostics { get; }

    public IReadOnlyList<BroadcastOfferMatch> UnmatchedOffers => OfferMatches
        .Where(match => match.MatchCount == 0)
        .ToList();
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
