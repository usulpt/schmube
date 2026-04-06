using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Schmube;

public sealed class EpgService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<IReadOnlyList<ProgramGuideEntry>> LoadShortGuideAsync(XtreamConnectionInfo connection, int streamId, string userAgent, string referer, CancellationToken cancellationToken = default)
    {
        var epgUri = new Uri(connection.BaseAddress, $"player_api.php?username={Uri.EscapeDataString(connection.Username)}&password={Uri.EscapeDataString(connection.Password)}&action=get_short_epg&stream_id={streamId}&limit=8");
        using var request = new HttpRequestMessage(HttpMethod.Get, epgUri);

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        }

        if (!string.IsNullOrWhiteSpace(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseShortGuide(json);
    }

    private static IReadOnlyList<ProgramGuideEntry> ParseShortGuide(string json)
    {
        var entries = new List<ProgramGuideEntry>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("epg_listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
        {
            return entries;
        }

        foreach (var listing in listings.EnumerateArray())
        {
            var start = ParseTimestamp(listing, "start_timestamp", "start");
            var end = ParseTimestamp(listing, "stop_timestamp", "end");
            var title = DecodeMaybeBase64(GetString(listing, "title"));
            var description = DecodeMaybeBase64(GetString(listing, "description"));

            entries.Add(new ProgramGuideEntry
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Untitled program" : title,
                Description = description,
                StartLocal = start,
                EndLocal = end
            });
        }

        return entries;
    }

    private static DateTime ParseTimestamp(JsonElement listing, string unixName, string textName)
    {
        var unixValue = GetString(listing, unixName);
        if (long.TryParse(unixValue, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        }

        var textValue = GetString(listing, textName);
        return DateTime.TryParseExact(textValue, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
    }

    private static string DecodeMaybeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return value;
        }
    }
}
