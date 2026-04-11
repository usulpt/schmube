using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Schmube;

public static class M3uExportService
{
    public static void Export(string filePath, IEnumerable<PlaylistChannel> channels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine("#EXTM3U");

        foreach (var channel in channels)
        {
            writer.Write("#EXTINF:-1");
            WriteAttribute(writer, "tvg-id", channel.TvgId);
            WriteAttribute(writer, "tvg-name", channel.Name);
            WriteAttribute(writer, "tvg-logo", channel.TvgLogo);
            WriteAttribute(writer, "group-title", ResolveExportGroupTitle(channel));
            writer.Write(',');
            writer.WriteLine(SanitizeLineValue(channel.Name));
            writer.WriteLine(channel.StreamUri);
        }
    }

    private static string ResolveExportGroupTitle(PlaylistChannel channel)
    {
        return string.IsNullOrWhiteSpace(channel.GroupTitle)
            ? channel.GroupDisplayTitle
            : channel.GroupTitle;
    }

    private static void WriteAttribute(TextWriter writer, string name, string value)
    {
        var sanitizedValue = SanitizeAttributeValue(value);
        if (string.IsNullOrWhiteSpace(sanitizedValue))
        {
            return;
        }

        writer.Write(' ');
        writer.Write(name);
        writer.Write("=\"");
        writer.Write(sanitizedValue);
        writer.Write('"');
    }

    private static string SanitizeAttributeValue(string value)
    {
        return SanitizeLineValue(value).Replace('"', '\'');
    }

    private static string SanitizeLineValue(string value)
    {
        return (value ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }
}
