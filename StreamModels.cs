using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Schmube;

public sealed class StreamSettings
{
    public string StreamUrl { get; set; } = string.Empty;

    public bool KeepPlayerOnTop { get; set; }

    public bool UseDarkMode { get; set; }

    public List<string> FavoriteChannelKeys { get; set; } = [];

    public List<string> RecentChannelKeys { get; set; } = [];

    public string LastChannelKey { get; set; } = string.Empty;

    public string SelectedGroupFilter { get; set; } = string.Empty;

    public string SelectedSubGroupFilter { get; set; } = string.Empty;

    public string SearchText { get; set; } = string.Empty;

    public bool SearchAllGroups { get; set; }

    public bool FavoritesOnly { get; set; }

    public bool RecentOnly { get; set; }

    public string ColumnPreset { get; set; } = string.Empty;

    public Dictionary<string, string> CustomChannelGroups { get; set; } = [];

    public int RecordingDefaultDurationMinutes { get; set; } = 60;

    public int RecordingStartPaddingMinutes { get; set; }

    public int RecordingEndPaddingMinutes { get; set; }

    public string RecordingFileNameFormat { get; set; } = "{timestamp}_{channel}";

    public List<RecordingScheduleEntry> RecordingSchedules { get; set; } = [];

    public List<RecordingHistoryEntry> RecordingHistory { get; set; } = [];
}

public sealed class GroupFilterOption
{
    public GroupFilterOption(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }

    public string Label { get; }
}

public sealed class PlaybackRequest
{
    public PlaybackRequest(Uri streamUri, bool keepPlayerOnTop, string displayName, bool allowReconnect, string recordingsDirectory, string logoSource = "", string fallbackLogoSource = "")
    {
        StreamUri = streamUri;
        KeepPlayerOnTop = keepPlayerOnTop;
        DisplayName = displayName;
        AllowReconnect = allowReconnect;
        RecordingsDirectory = recordingsDirectory;
        LogoSource = logoSource;
        FallbackLogoSource = fallbackLogoSource;
    }

    public Uri StreamUri { get; }

    public bool KeepPlayerOnTop { get; }

    public string DisplayName { get; }

    public bool AllowReconnect { get; }

    public string RecordingsDirectory { get; }

    public string LogoSource { get; }

    public string FallbackLogoSource { get; }
}

public sealed class RecordingScheduleEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string StreamUri { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool KeepPlayerOnTop { get; set; }

    public bool AllowReconnect { get; set; } = true;

    public string RecordingsDirectory { get; set; } = string.Empty;

    public string LogoSource { get; set; } = string.Empty;

    public string FallbackLogoSource { get; set; } = string.Empty;

    public string ProgramTitle { get; set; } = string.Empty;

    public DateTime StartLocal { get; set; }

    public DateTime EndLocal { get; set; }

    public DateTime CreatedLocal { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string Summary => string.IsNullOrWhiteSpace(ProgramTitle)
        ? $"{DisplayName} | {StartLocal:g} - {EndLocal:t}"
        : $"{DisplayName} | {ProgramTitle} | {StartLocal:g} - {EndLocal:t}";

    [JsonIgnore]
    public string DurationText => EndLocal > StartLocal
        ? $"{EndLocal - StartLocal:hh\\:mm}"
        : "00:00";

    public PlaybackRequest ToPlaybackRequest()
    {
        return new PlaybackRequest(
            new Uri(StreamUri, UriKind.Absolute),
            KeepPlayerOnTop,
            DisplayName,
            AllowReconnect,
            RecordingsDirectory,
            LogoSource,
            FallbackLogoSource);
    }
}

public sealed class RecordingHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string DisplayName { get; set; } = string.Empty;

    public string ProgramTitle { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public DateTime StartedLocal { get; set; }

    public DateTime EndedLocal { get; set; }

    public string Status { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    [JsonIgnore]
    public string FileName => string.IsNullOrWhiteSpace(FilePath) ? string.Empty : System.IO.Path.GetFileName(FilePath);

    [JsonIgnore]
    public string SizeText
    {
        get
        {
            if (FileSizeBytes <= 0)
            {
                return "Unknown size";
            }

            var megabytes = FileSizeBytes / 1024d / 1024d;
            return megabytes >= 1024
                ? $"{megabytes / 1024d:0.00} GB"
                : $"{megabytes:0.0} MB";
        }
    }

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(ProgramTitle) ? DisplayName : $"{DisplayName} | {ProgramTitle}";
            return $"{Status}: {title} | {StartedLocal:g} - {EndedLocal:t} | {SizeText}";
        }
    }
}

public sealed class PlaylistChannel : INotifyPropertyChanged
{
    private string _logoSource = string.Empty;
    private bool _isFavorite;
    private int _recentRank = -1;
    private string _nowTitle = string.Empty;
    private string _nextTitle = string.Empty;
    private string _groupFlag = string.Empty;
    private string _groupDisplayTitle = string.Empty;
    private string _canonicalGroupKey = string.Empty;
    private string _subGroupDisplayTitle = string.Empty;
    private string _canonicalSubGroupKey = string.Empty;

    public required string Name { get; init; }

    public string GroupTitle { get; init; } = string.Empty;

    public string TvgId { get; init; } = string.Empty;

    public string TvgLogo { get; init; } = string.Empty;

    public string LogoSource
    {
        get => _logoSource;
        set => SetField(ref _logoSource, value);
    }

    public required Uri StreamUri { get; init; }

    public int? StreamId { get; init; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetField(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteMarker));
            }
        }
    }

    public int RecentRank
    {
        get => _recentRank;
        set
        {
            if (SetField(ref _recentRank, value))
            {
                OnPropertyChanged(nameof(RecentMarker));
            }
        }
    }

    public string NowTitle
    {
        get => _nowTitle;
        set => SetField(ref _nowTitle, value);
    }

    public string NextTitle
    {
        get => _nextTitle;
        set => SetField(ref _nextTitle, value);
    }

    public string GroupFlag
    {
        get => _groupFlag;
        set => SetField(ref _groupFlag, value);
    }

    public string GroupDisplayTitle
    {
        get => _groupDisplayTitle;
        set => SetField(ref _groupDisplayTitle, value);
    }

    public string CanonicalGroupKey
    {
        get => _canonicalGroupKey;
        set => SetField(ref _canonicalGroupKey, value);
    }

    public string SubGroupDisplayTitle
    {
        get => _subGroupDisplayTitle;
        set => SetField(ref _subGroupDisplayTitle, value);
    }

    public string CanonicalSubGroupKey
    {
        get => _canonicalSubGroupKey;
        set => SetField(ref _canonicalSubGroupKey, value);
    }

    public string CleanName => BuildCleanName(Name);

    public string FavoriteKey => string.IsNullOrWhiteSpace(TvgId) ? StreamUri.ToString() : TvgId;

    public string FavoriteMarker => IsFavorite ? "*" : string.Empty;

    public string RecentMarker => RecentRank >= 0 ? "R" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string BuildCleanName(string name)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        var separatorIndex = trimmedName.IndexOf(':');
        if (separatorIndex <= 0 || separatorIndex >= trimmedName.Length - 1)
        {
            return trimmedName;
        }

        var prefix = trimmedName[..separatorIndex].Trim();
        if (prefix.Length > 16
            || prefix.IndexOfAny(['|', '(', ')', '[', ']']) >= 0
            || !prefix.Any(char.IsLetter))
        {
            return trimmedName;
        }

        var cleanedName = trimmedName[(separatorIndex + 1)..].Trim();
        return string.IsNullOrWhiteSpace(cleanedName) ? trimmedName : cleanedName;
    }
}

public sealed class SchmubeAppConfig
{
    public string SubscriptionUrl { get; set; } = string.Empty;

    public string DefaultGroup { get; set; } = string.Empty;

    public bool AutoLoadOnStartup { get; set; } = true;

    public string RecordingsDirectory { get; set; } = string.Empty;
}

public sealed class XtreamConnectionInfo
{
    public XtreamConnectionInfo(Uri baseAddress, string username, string password, string outputFormat)
    {
        BaseAddress = baseAddress;
        Username = username;
        Password = password;
        OutputFormat = outputFormat;
    }

    public Uri BaseAddress { get; }

    public string Username { get; }

    public string Password { get; }

    public string OutputFormat { get; }
}

public sealed class ProgramGuideEntry
{
    public required string Title { get; init; }

    public string Description { get; init; } = string.Empty;

    public DateTime StartLocal { get; init; }

    public DateTime EndLocal { get; init; }

    public string TimeRange => $"{StartLocal:ddd HH:mm} - {EndLocal:HH:mm}";
}

