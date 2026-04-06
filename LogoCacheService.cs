using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Schmube;

public sealed class LogoCacheService
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Schmube",
        "logos");

    public string ResolveLogoSource(string logoUrl)
    {
        if (TryGetCachePath(logoUrl, out var cachePath) && File.Exists(cachePath))
        {
            return cachePath;
        }

        return logoUrl;
    }

    public async Task WarmCacheAsync(IEnumerable<PlaylistChannel> channels, int maxCount, CancellationToken cancellationToken = default)
    {
        var urls = channels
            .Select(channel => channel.TvgLogo)
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureCachedAsync(url, cancellationToken);
        }
    }

    public async Task<string> EnsureChannelLogoAsync(PlaylistChannel channel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channel.TvgLogo))
        {
            channel.LogoSource = string.Empty;
            return string.Empty;
        }

        await EnsureCachedAsync(channel.TvgLogo, cancellationToken);
        channel.LogoSource = ResolveLogoSource(channel.TvgLogo);
        return channel.LogoSource;
    }

    public void RefreshResolvedLogoSources(IEnumerable<PlaylistChannel> channels)
    {
        foreach (var channel in channels)
        {
            channel.LogoSource = ResolveLogoSource(channel.TvgLogo);
        }
    }

    private async Task EnsureCachedAsync(string logoUrl, CancellationToken cancellationToken)
    {
        if (!TryGetCachePath(logoUrl, out var cachePath))
        {
            return;
        }

        if (File.Exists(cachePath))
        {
            return;
        }

        Directory.CreateDirectory(_cacheDirectory);

        using var response = await _httpClient.GetAsync(logoUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        var tempPath = cachePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);

        if (!File.Exists(cachePath))
        {
            File.Move(tempPath, cachePath);
        }
        else if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    private bool TryGetCachePath(string logoUrl, out string cachePath)
    {
        cachePath = string.Empty;

        if (!Uri.TryCreate(logoUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        using var sha1 = SHA1.Create();
        var hash = Convert.ToHexString(sha1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(logoUrl))).ToLowerInvariant();
        var extension = Path.GetExtension(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
        {
            extension = ".img";
        }

        cachePath = Path.Combine(_cacheDirectory, hash + extension);
        return true;
    }
}
