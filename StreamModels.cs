using System;
using System.Collections.Generic;
using System.Linq;

namespace Schmube;

public sealed class StreamSettings
{
    public string StreamUrl { get; set; } = string.Empty;

    public string UserAgent { get; set; } = string.Empty;

    public string Referer { get; set; } = string.Empty;

    public bool KeepPlayerOnTop { get; set; }

    public List<string> FavoriteChannelKeys { get; set; } = [];
}

public sealed class PlaybackRequest
{
    public PlaybackRequest(Uri streamUri, string userAgent, string referer, bool keepPlayerOnTop, string displayName)
    {
        StreamUri = streamUri;
        UserAgent = userAgent;
        Referer = referer;
        KeepPlayerOnTop = keepPlayerOnTop;
        DisplayName = displayName;
    }

    public Uri StreamUri { get; }

    public string UserAgent { get; }

    public string Referer { get; }

    public bool KeepPlayerOnTop { get; }

    public string DisplayName { get; }
}

public sealed class PlaylistChannel
{
    public required string Name { get; init; }

    public string GroupTitle { get; init; } = string.Empty;

    public string TvgId { get; init; } = string.Empty;

    public string TvgLogo { get; init; } = string.Empty;

    public required Uri StreamUri { get; init; }

    public bool IsFavorite { get; set; }

    public string FavoriteKey => string.IsNullOrWhiteSpace(TvgId) ? StreamUri.ToString() : TvgId;

    public string FavoriteMarker => IsFavorite ? "*" : string.Empty;
}

public sealed class SchmubeAppConfig
{
    public string SubscriptionUrl { get; set; } = string.Empty;

    public List<string> DefaultGroups { get; set; } = [];

    public bool ApplyDefaultGroupsOnLoad { get; set; }

    public bool AutoLoadOnStartup { get; set; } = true;
}
