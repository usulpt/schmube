using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Schmube;

public sealed class StreamSettings
{
    public string StreamUrl { get; set; } = string.Empty;

    public bool KeepPlayerOnTop { get; set; }

    public List<string> FavoriteChannelKeys { get; set; } = [];

    public List<string> RecentChannelKeys { get; set; } = [];

    public string LastChannelKey { get; set; } = string.Empty;
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
    public PlaybackRequest(Uri streamUri, bool keepPlayerOnTop, string displayName, bool allowReconnect, string recordingsDirectory)
    {
        StreamUri = streamUri;
        KeepPlayerOnTop = keepPlayerOnTop;
        DisplayName = displayName;
        AllowReconnect = allowReconnect;
        RecordingsDirectory = recordingsDirectory;
    }

    public Uri StreamUri { get; }

    public bool KeepPlayerOnTop { get; }

    public string DisplayName { get; }

    public bool AllowReconnect { get; }

    public string RecordingsDirectory { get; }
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

