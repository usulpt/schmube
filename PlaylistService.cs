using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Schmube;

public sealed class PlaylistService
{
    private static readonly Regex AttributeRegex = new("(?<key>[A-Za-z0-9-]+)=\"(?<value>[^\"]*)\"", RegexOptions.Compiled);
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static bool IsXtreamPlaylistUri(Uri uri)
    {
        return TryGetXtreamConnection(uri, out _);
    }

    public static bool TryGetXtreamConnection(Uri uri, out XtreamConnectionInfo? connection)
    {
        connection = null;

        if (!string.Equals(Path.GetFileName(uri.AbsolutePath), "get.php", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var query = ParseQueryString(uri.Query);
        if (!query.TryGetValue("username", out var username) || string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        if (!query.TryGetValue("password", out var password) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var outputFormat = query.TryGetValue("output", out var output) && !string.IsNullOrWhiteSpace(output)
            ? output
            : "ts";

        var baseAddressBuilder = new UriBuilder(uri.Scheme, uri.Host)
        {
            Port = uri.IsDefaultPort ? -1 : uri.Port,
            Path = "/"
        };

        connection = new XtreamConnectionInfo(baseAddressBuilder.Uri, username, password, outputFormat);
        return true;
    }

    public async Task<IReadOnlyList<PlaylistChannel>> LoadChannelsAsync(Uri sourceUri, CancellationToken cancellationToken = default)
    {
        if (TryGetXtreamConnection(sourceUri, out var connection) && connection is not null)
        {
            return await LoadXtreamChannelsAsync(connection, cancellationToken);
        }

        return await LoadM3uChannelsAsync(sourceUri, cancellationToken);
    }

    private async Task<IReadOnlyList<PlaylistChannel>> LoadM3uChannelsAsync(Uri playlistUri, CancellationToken cancellationToken)
    {
        var content = await SendGetAsync(playlistUri, cancellationToken);
        return ParsePlaylist(playlistUri, content);
    }

    private async Task<IReadOnlyList<PlaylistChannel>> LoadXtreamChannelsAsync(XtreamConnectionInfo connection, CancellationToken cancellationToken)
    {
        var categoriesUri = new Uri(connection.BaseAddress, $"player_api.php?username={Uri.EscapeDataString(connection.Username)}&password={Uri.EscapeDataString(connection.Password)}&action=get_live_categories");
        var streamsUri = new Uri(connection.BaseAddress, $"player_api.php?username={Uri.EscapeDataString(connection.Username)}&password={Uri.EscapeDataString(connection.Password)}&action=get_live_streams");

        var categoriesJson = await SendGetAsync(categoriesUri, cancellationToken);
        var streamsJson = await SendGetAsync(streamsUri, cancellationToken);

        var categories = ParseXtreamCategories(categoriesJson);
        var channels = ParseXtreamStreams(streamsJson, categories, connection);

        return channels
            .OrderBy(channel => channel.GroupTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<string> SendGetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static IReadOnlyList<PlaylistChannel> ParsePlaylist(Uri playlistUri, string content)
    {
        var channels = new List<PlaylistChannel>();
        PlaylistMetadata? currentMetadata = null;

        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                currentMetadata = ParseMetadata(line);
                continue;
            }

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var streamUri = ResolveUri(playlistUri, line);
            if (streamUri is null)
            {
                currentMetadata = null;
                continue;
            }

            var logo = currentMetadata?.TvgLogo ?? string.Empty;

            channels.Add(new PlaylistChannel
            {
                Name = string.IsNullOrWhiteSpace(currentMetadata?.Name) ? BuildChannelName(streamUri) : currentMetadata.Name,
                GroupTitle = currentMetadata?.GroupTitle ?? string.Empty,
                TvgId = currentMetadata?.TvgId ?? string.Empty,
                TvgLogo = logo,
                LogoSource = logo,
                StreamUri = streamUri
            });

            currentMetadata = null;
        }

        return channels;
    }

    private static PlaylistMetadata ParseMetadata(string line)
    {
        var payload = line["#EXTINF:".Length..];
        var commaIndex = payload.IndexOf(',');
        var attributesText = commaIndex >= 0 ? payload[..commaIndex] : payload;
        var title = commaIndex >= 0 ? payload[(commaIndex + 1)..].Trim() : string.Empty;

        string GetAttribute(string key)
        {
            foreach (Match match in AttributeRegex.Matches(attributesText))
            {
                if (string.Equals(match.Groups["key"].Value, key, StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups["value"].Value.Trim();
                }
            }

            return string.Empty;
        }

        var tvgName = GetAttribute("tvg-name");

        return new PlaylistMetadata
        {
            Name = string.IsNullOrWhiteSpace(title) ? tvgName : title,
            GroupTitle = GetAttribute("group-title"),
            TvgId = GetAttribute("tvg-id"),
            TvgLogo = GetAttribute("tvg-logo")
        };
    }

    private static IReadOnlyDictionary<string, string> ParseXtreamCategories(string json)
    {
        var categories = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return categories;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var categoryId = GetJsonValue(element, "category_id");
            var categoryName = GetJsonValue(element, "category_name");

            if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(categoryName))
            {
                continue;
            }

            categories[categoryId] = categoryName;
        }

        return categories;
    }

    private static IReadOnlyList<PlaylistChannel> ParseXtreamStreams(string json, IReadOnlyDictionary<string, string> categories, XtreamConnectionInfo connection)
    {
        var channels = new List<PlaylistChannel>();

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return channels;
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var streamIdText = GetJsonValue(element, "stream_id");
            if (!int.TryParse(streamIdText, out var streamId))
            {
                continue;
            }

            var name = GetJsonValue(element, "name");
            var categoryId = GetJsonValue(element, "category_id");
            var tvgId = GetJsonValue(element, "epg_channel_id");
            var streamIcon = GetJsonValue(element, "stream_icon");

            categories.TryGetValue(categoryId, out var categoryName);

            channels.Add(new PlaylistChannel
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"Channel {streamId}" : name,
                GroupTitle = categoryName ?? string.Empty,
                TvgId = tvgId,
                TvgLogo = streamIcon,
                LogoSource = streamIcon,
                StreamUri = BuildXtreamLiveUri(connection, streamId),
                StreamId = streamId
            });
        }

        return channels;
    }

    private static string GetJsonValue(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };
    }

    private static Uri BuildXtreamLiveUri(XtreamConnectionInfo connection, int streamId)
    {
        var path = $"live/{Uri.EscapeDataString(connection.Username)}/{Uri.EscapeDataString(connection.Password)}/{streamId}.{connection.OutputFormat}";
        return new Uri(connection.BaseAddress, path);
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmedQuery = query.StartsWith('?') ? query[1..] : query;

        foreach (var pair in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private static Uri? ResolveUri(Uri playlistUri, string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return Uri.TryCreate(playlistUri, value, out var relativeUri) ? relativeUri : null;
    }

    private static string BuildChannelName(Uri streamUri)
    {
        var lastSegment = streamUri.Segments.Length > 0 ? streamUri.Segments[^1].Trim('/') : string.Empty;
        return string.IsNullOrWhiteSpace(lastSegment) ? streamUri.Host : lastSegment;
    }

    private sealed class PlaylistMetadata
    {
        public string Name { get; init; } = string.Empty;

        public string GroupTitle { get; init; } = string.Empty;

        public string TvgId { get; init; } = string.Empty;

        public string TvgLogo { get; init; } = string.Empty;
    }
}

