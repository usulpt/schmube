using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Schmube;

public partial class MainWindow : Window
{
    private const string AllGroupsLabel = "All groups";
    private const string AllSubGroupsLabel = "All subgroups";
    private const string FavoritesGroupLabel = "Favorites";
    private const string RecentlyWatchedGroupLabel = "Recently Watched";
    private const string FavoritesGroupValue = "__favorites";
    private const string RecentlyWatchedGroupValue = "__recent";
    private const string DefaultChannelSortKey = "Clean Name";
    private const string DefaultHintText = "Focus a control to see what it does.";
    private const int RecentChannelLimit = 12;
    private const int GuideWarmupLimit = 18;

    private enum ChannelColumnPreset
    {
        Compact,
        Standard,
        Detail
    }

    private readonly SettingsStore _settingsStore = new();
    private readonly AppConfigStore _appConfigStore = new();
    private readonly GroupFlagStore _groupFlagStore = new();
    private readonly CanonicalGroupService _canonicalGroupService = new();
    private readonly PlaylistService _playlistService = new();
    private readonly LogoCacheService _logoCacheService = new();
    private readonly EpgService _epgService = new();
    private readonly List<PlaylistChannel> _allChannels = [];
    private readonly ObservableCollection<PlaylistChannel> _visibleChannels = [];
    private readonly ObservableCollection<GroupFilterOption> _groupOptions = [BuildAllGroupsOption()];
    private readonly ObservableCollection<GroupFilterOption> _subGroupOptions = [BuildAllSubGroupsOption()];
    private readonly ObservableCollection<ProgramGuideEntry> _programGuideEntries = [];
    private readonly HashSet<string> _favoriteChannelKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recentChannelKeys = [];
    private readonly Dictionary<int, IReadOnlyList<ProgramGuideEntry>> _guideCache = [];
    private readonly Dictionary<string, int> _temporaryChannelOrder = new(StringComparer.OrdinalIgnoreCase);

    private readonly SchmubeAppConfig _appConfig;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly DispatcherTimer _guideWarmupDebounceTimer;
    private CancellationTokenSource? _logoWarmupCts;
    private CancellationTokenSource? _epgLoadCts;
    private CancellationTokenSource? _guideWarmupCts;
    private bool _autoLoadAttempted;
    private string _defaultGroup = string.Empty;
    private string _lastPlayedChannelKey = string.Empty;
    private string _pendingSelectionChannelKey = string.Empty;
    private string _temporaryListLabel = string.Empty;
    private XtreamConnectionInfo? _currentXtreamConnection;
    private PlayerWindow? _playerWindow;
    private bool _keepPlayerOnTop;
    private bool _useDarkMode;
    private bool _searchAllGroups;
    private bool _favoritesOnly;
    private bool _recentOnly;
    private bool _suppressFilterRefresh;
    private int _recordingDefaultDurationMinutes = 60;
    private int _recordingStartPaddingMinutes;
    private int _recordingEndPaddingMinutes;
    private string _recordingFileNameFormat = "{timestamp}_{channel}";
    private ChannelColumnPreset _columnPreset = ChannelColumnPreset.Standard;
    private string _channelSortKey = DefaultChannelSortKey;
    private ListSortDirection _channelSortDirection = ListSortDirection.Ascending;
    private string _savedGroupFilter = string.Empty;
    private string _savedSubGroupFilter = string.Empty;
    private readonly Dictionary<string, string> _customChannelGroups = new(StringComparer.OrdinalIgnoreCase);
    private List<RecordingScheduleEntry> _recordingSchedules = [];
    private List<RecordingHistoryEntry> _recordingHistory = [];

    public MainWindow()
    {
        _appConfig = _appConfigStore.Load();
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        _guideWarmupDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _guideWarmupDebounceTimer.Tick += GuideWarmupDebounceTimer_Tick;

        InitializeComponent();

        ChannelsListView.ItemsSource = _visibleChannels;
        GroupFilterComboBox.ItemsSource = _groupOptions;
        GroupFilterComboBox.SelectedIndex = 0;
        SubGroupFilterComboBox.ItemsSource = _subGroupOptions;
        SubGroupFilterComboBox.SelectedIndex = 0;
        ProgramGuideListBox.ItemsSource = _programGuideEntries;
        Loaded += MainWindow_Loaded;
        PreviewKeyDown += MainWindow_PreviewKeyDown;

        RegisterFocusHints();
        SetHintText(DefaultHintText);
        LoadSettings();
        InitializeSelectedRecordingInputs();
        UpdateChannelSummary();
        ClearProgramGuide("Select a channel to load the guide.");
        UpdateSelectedChannelDetails(null);
        ApplyColumnPreset(_columnPreset);
    }

    private static GroupFilterOption BuildAllGroupsOption()
    {
        return new(AllGroupsLabel, AllGroupsLabel);
    }

    private static GroupFilterOption BuildAllSubGroupsOption()
    {
        return new(AllSubGroupsLabel, AllSubGroupsLabel);
    }

    private static GroupFilterOption BuildFavoritesGroupOption()
    {
        return new(FavoritesGroupValue, FavoritesGroupLabel);
    }

    private static GroupFilterOption BuildRecentlyWatchedGroupOption()
    {
        return new(RecentlyWatchedGroupValue, RecentlyWatchedGroupLabel);
    }

    private PlaylistChannel? SelectedChannel => ChannelsListView.SelectedItem as PlaylistChannel;
    private GroupFilterOption? SelectedGroupOption => GroupFilterComboBox.SelectedItem as GroupFilterOption;
    private GroupFilterOption? SelectedSubGroupOption => SubGroupFilterComboBox.SelectedItem as GroupFilterOption;
    private bool HasTemporaryChannelList => _temporaryChannelOrder.Count > 0;

    private void RegisterFocusHints()
    {
        RegisterFocusHint(StreamUrlTextBox, "Source URL for your playlist or direct stream. Paste it here or keep using the saved config value.");
        RegisterFocusHint(DarkModeButton, "Toggle the application theme.");
        RegisterFocusHint(KeepOnTopButton, "Keep the separate player window above your other apps while you watch.");
        RegisterFocusHint(ChannelSearchTextBox, "Filter the loaded channels by name, group, channel ID, or the live guide preview.");
        RegisterFocusHint(GroupFilterComboBox, "Limit the channel list to one main country or type group after the playlist finishes loading.");
        RegisterFocusHint(SubGroupFilterComboBox, "Limit the selected main group to one source subgroup from the playlist.");
        RegisterFocusHint(SearchAllGroupsButton, "When search text is present, search across all groups instead of limiting results to the selected group.");
        RegisterFocusHint(FavoritesOnlyButton, "Show only channels you have marked as favorites.");
        RegisterFocusHint(RecentOnlyButton, "Show only channels you played recently.");
        RegisterFocusHint(ClearFiltersButton, "Clear search, group, favorites, recents, and temporary list filters.");
        RegisterFocusHint(RecordingDefaultDurationTextBox, "Default duration for manually scheduled player recordings.");
        RegisterFocusHint(RecordingStartPaddingTextBox, "Minutes to start before a program guide recording.");
        RegisterFocusHint(RecordingEndPaddingTextBox, "Minutes to keep recording after a program guide entry ends.");
        RegisterFocusHint(RecordingFileNameFormatTextBox, "Filename format for recordings. Use {timestamp}, {channel}, and {program}.");
        RegisterFocusHint(ColumnsButton, "Switch the channel list between compact, standard, and detail column layouts.");
        RegisterFocusHint(LoadChannelsButton, "Load or refresh the channel list from the configured source.");
        RegisterFocusHint(TvListingsButton, "Open a football match page, generate compatible playlist channels from its broadcaster listing, and build a temporary channel list from the current playlist.");
        RegisterFocusHint(ClearTempListButton, "Clear the active temporary channel list created from TV listings results and return to normal browsing.");
        RegisterFocusHint(ExportM3uButton, "Export loaded channels as an M3U playlist. Use the menu for the complete list, selected group, current visible list, or temporary list.");
        RegisterFocusHint(PlayUrlButton, "Play the raw URL directly when it points to a single stream rather than a playlist account.");
        RegisterFocusHint(SaveSettingsButton, "Persist the current URL, favorites, recents, and playback window setting locally.");
        RegisterFocusHint(ChannelsListView, "Browse channels here. Single-click selects a channel and double-click starts playback.");
        RegisterFocusHint(ToggleFavoriteButton, "Add or remove the selected channel from your favorites list.");
        RegisterFocusHint(RecordSelectedChannelButton, "Start or stop recording the selected channel without opening the player window.");
        RegisterFocusHint(SelectedRecordingDatePicker, "Date for a scheduled recording of the selected channel.");
        RegisterFocusHint(SelectedRecordingTimeTextBox, "Start time for a scheduled recording of the selected channel, for example 21:30.");
        RegisterFocusHint(SelectedRecordingDurationTextBox, "Scheduled recording length in minutes.");
        RegisterFocusHint(ScheduleSelectedChannelButton, "Schedule the selected channel using the date, time, and length fields.");
        RegisterFocusHint(CustomGroupTextBox, "Assign the selected channel to a custom group.");
        RegisterFocusHint(ApplyCustomGroupButton, "Save the selected channel's custom group.");
        RegisterFocusHint(ClearCustomGroupButton, "Remove the selected channel's custom group.");
    }

    private void RegisterFocusHint(Control control, string hint)
    {
        control.GotKeyboardFocus += (_, _) => SetHintText(hint);
    }

    private void SetHintText(string hint)
    {
        HintTextBlock.Text = string.IsNullOrWhiteSpace(hint) ? DefaultHintText : hint;
    }

    private void LoadSettings()
    {
        var settings = _settingsStore.Load();

        _favoriteChannelKeys.Clear();
        foreach (var key in settings.FavoriteChannelKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            _favoriteChannelKeys.Add(key);
        }

        _recentChannelKeys.Clear();
        foreach (var key in settings.RecentChannelKeys
                     .Where(key => !string.IsNullOrWhiteSpace(key))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(RecentChannelLimit))
        {
            _recentChannelKeys.Add(key);
        }

        _lastPlayedChannelKey = settings.LastChannelKey?.Trim() ?? string.Empty;
        _pendingSelectionChannelKey = _lastPlayedChannelKey;
        _defaultGroup = _appConfig.DefaultGroup?.Trim() ?? string.Empty;
        _savedGroupFilter = settings.SelectedGroupFilter?.Trim() ?? string.Empty;
        _savedSubGroupFilter = settings.SelectedSubGroupFilter?.Trim() ?? string.Empty;
        _customChannelGroups.Clear();
        foreach (var pair in settings.CustomChannelGroups.Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)))
        {
            _customChannelGroups[pair.Key] = pair.Value.Trim();
        }

        StreamUrlTextBox.Text = !string.IsNullOrWhiteSpace(_appConfig.SubscriptionUrl)
            ? _appConfig.SubscriptionUrl
            : settings.StreamUrl;
        _keepPlayerOnTop = settings.KeepPlayerOnTop;
        _useDarkMode = settings.UseDarkMode;
        _searchAllGroups = settings.SearchAllGroups;
        _favoritesOnly = settings.FavoritesOnly;
        _recentOnly = settings.RecentOnly;
        _recordingDefaultDurationMinutes = Math.Clamp(settings.RecordingDefaultDurationMinutes, 1, 1440);
        _recordingStartPaddingMinutes = Math.Clamp(settings.RecordingStartPaddingMinutes, 0, 240);
        _recordingEndPaddingMinutes = Math.Clamp(settings.RecordingEndPaddingMinutes, 0, 240);
        _recordingFileNameFormat = string.IsNullOrWhiteSpace(settings.RecordingFileNameFormat)
            ? "{timestamp}_{channel}"
            : settings.RecordingFileNameFormat.Trim();
        _recordingSchedules = settings.RecordingSchedules
            .Where(schedule => schedule.EndLocal > DateTime.Now)
            .ToList();
        _recordingHistory = settings.RecordingHistory
            .OrderByDescending(entry => entry.StartedLocal)
            .Take(50)
            .ToList();
        ChannelSearchTextBox.Text = settings.SearchText ?? string.Empty;
        RecordingDefaultDurationTextBox.Text = _recordingDefaultDurationMinutes.ToString(CultureInfo.CurrentCulture);
        RecordingStartPaddingTextBox.Text = _recordingStartPaddingMinutes.ToString(CultureInfo.CurrentCulture);
        RecordingEndPaddingTextBox.Text = _recordingEndPaddingMinutes.ToString(CultureInfo.CurrentCulture);
        RecordingFileNameFormatTextBox.Text = _recordingFileNameFormat;
        _columnPreset = ParseColumnPreset(settings.ColumnPreset);
        ThemeService.ApplyTheme(_useDarkMode);
        UpdateToggleButtonStates();
        UpdateTemporaryListUi();
        UpdateExportM3uUi();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_recordingSchedules.Count > 0)
        {
            EnsurePlayerWindow(focusWindow: false, showWindow: false);
            StatusTextBlock.Text = $"{_recordingSchedules.Count} scheduled recording(s) loaded.";
        }

        if (_autoLoadAttempted || !_appConfig.AutoLoadOnStartup || string.IsNullOrWhiteSpace(StreamUrlTextBox.Text))
        {
            return;
        }

        _autoLoadAttempted = true;
        LoadChannelsButton_Click(this, new RoutedEventArgs());
    }

    private StreamSettings CollectSettingsFromUi()
    {
        RefreshRecordingSettingsFromUi();
        return new StreamSettings
        {
            StreamUrl = StreamUrlTextBox.Text.Trim(),
            KeepPlayerOnTop = _keepPlayerOnTop,
            UseDarkMode = _useDarkMode,
            FavoriteChannelKeys = _favoriteChannelKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            RecentChannelKeys = _recentChannelKeys.ToList(),
            LastChannelKey = _lastPlayedChannelKey,
            SelectedGroupFilter = SelectedGroupOption?.Value ?? string.Empty,
            SelectedSubGroupFilter = SelectedSubGroupOption?.Value ?? string.Empty,
            SearchText = ChannelSearchTextBox.Text,
            SearchAllGroups = _searchAllGroups,
            FavoritesOnly = _favoritesOnly,
            RecentOnly = _recentOnly,
            ColumnPreset = _columnPreset.ToString(),
            CustomChannelGroups = _customChannelGroups.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            RecordingDefaultDurationMinutes = ReadPositiveIntSetting(RecordingDefaultDurationTextBox, _recordingDefaultDurationMinutes, 1, 1440),
            RecordingStartPaddingMinutes = ReadPositiveIntSetting(RecordingStartPaddingTextBox, _recordingStartPaddingMinutes, 0, 240),
            RecordingEndPaddingMinutes = ReadPositiveIntSetting(RecordingEndPaddingTextBox, _recordingEndPaddingMinutes, 0, 240),
            RecordingFileNameFormat = string.IsNullOrWhiteSpace(RecordingFileNameFormatTextBox.Text)
                ? "{timestamp}_{channel}"
                : RecordingFileNameFormatTextBox.Text.Trim(),
            RecordingSchedules = _recordingSchedules.ToList(),
            RecordingHistory = _recordingHistory.ToList()
        };
    }

    private void RefreshRecordingSettingsFromUi()
    {
        _recordingDefaultDurationMinutes = ReadPositiveIntSetting(RecordingDefaultDurationTextBox, _recordingDefaultDurationMinutes, 1, 1440);
        _recordingStartPaddingMinutes = ReadPositiveIntSetting(RecordingStartPaddingTextBox, _recordingStartPaddingMinutes, 0, 240);
        _recordingEndPaddingMinutes = ReadPositiveIntSetting(RecordingEndPaddingTextBox, _recordingEndPaddingMinutes, 0, 240);
        _recordingFileNameFormat = string.IsNullOrWhiteSpace(RecordingFileNameFormatTextBox.Text)
            ? "{timestamp}_{channel}"
            : RecordingFileNameFormatTextBox.Text.Trim();
    }

    private void InitializeSelectedRecordingInputs()
    {
        var defaultStart = DateTime.Now.AddMinutes(5);
        SelectedRecordingDatePicker.SelectedDate = defaultStart.Date;
        SelectedRecordingTimeTextBox.Text = defaultStart.ToString("HH:mm", CultureInfo.CurrentCulture);
        SelectedRecordingDurationTextBox.Text = _recordingDefaultDurationMinutes.ToString(CultureInfo.CurrentCulture);
    }

    private static int ReadPositiveIntSetting(TextBox textBox, int fallback, int min, int max)
    {
        return int.TryParse(textBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private bool TryGetConfiguredUri(out Uri? uri)
    {
        uri = null;
        var value = StreamUrlTextBox.Text.Trim();

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsedUri))
        {
            StatusTextBlock.Text = "Please provide a valid absolute playlist or stream URL.";
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private PlaybackRequest BuildPlaybackRequest(Uri streamUri, string displayName, bool allowReconnect, string logoSource = "", string fallbackLogoSource = "")
    {
        var settings = CollectSettingsFromUi();
        return new PlaybackRequest(streamUri, settings.KeepPlayerOnTop, displayName, allowReconnect, ResolveRecordingsDirectory(), logoSource, fallbackLogoSource);
    }

    private bool TryBuildDirectRequest(out PlaybackRequest? request)
    {
        request = null;

        if (!TryGetConfiguredUri(out var streamUri) || streamUri is null)
        {
            return false;
        }

        if (PlaylistService.IsXtreamPlaylistUri(streamUri))
        {
            StatusTextBlock.Text = "This URL is an Xtream account/playlist endpoint. Click Load Channels, then play a selected channel.";
            return false;
        }

        _settingsStore.Save(CollectSettingsFromUi());
        request = BuildPlaybackRequest(streamUri, BuildDisplayName(streamUri), allowReconnect: false);
        return true;
    }

    private PlayerWindow EnsurePlayerWindow(bool focusWindow, bool showWindow = true)
    {
        RefreshRecordingSettingsFromUi();
        if (_playerWindow is null)
        {
            _playerWindow = new PlayerWindow();
            _playerWindow.ChannelStepRequested += PlayerWindow_ChannelStepRequested;
            _playerWindow.PlaybackStopped += PlayerWindow_PlaybackStopped;
            _playerWindow.RecordingStateChanged += PlayerWindow_RecordingStateChanged;
            _playerWindow.Closed += PlayerWindow_Closed;
            _playerWindow.ConfigureRecordingDefaults(_recordingDefaultDurationMinutes, _recordingFileNameFormat);
            _playerWindow.LoadRecordingState(_recordingSchedules, _recordingHistory);
        }

        _playerWindow.SetAlwaysOnTop(_keepPlayerOnTop);
        _playerWindow.ConfigureRecordingDefaults(_recordingDefaultDurationMinutes, _recordingFileNameFormat);

        if ((showWindow || focusWindow) && !_playerWindow.IsVisible)
        {
            _playerWindow.Show();
        }

        if (focusWindow)
        {
            _playerWindow.Activate();
        }

        return _playerWindow;
    }

    private void PlayerWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is PlayerWindow playerWindow)
        {
            PlayerWindow_RecordingStateChanged(playerWindow, EventArgs.Empty);
            playerWindow.ChannelStepRequested -= PlayerWindow_ChannelStepRequested;
            playerWindow.PlaybackStopped -= PlayerWindow_PlaybackStopped;
            playerWindow.RecordingStateChanged -= PlayerWindow_RecordingStateChanged;
        }

        if (ReferenceEquals(_playerWindow, sender))
        {
            _playerWindow = null;
            ClearNowPlaying();
            UpdateSelectedChannelRecordingButton();
        }
    }

    private async void PlayerWindow_ChannelStepRequested(object? sender, int delta)
    {
        await StepVisibleChannelAsync(delta);
    }

    private void PlayerWindow_PlaybackStopped(object? sender, EventArgs e)
    {
        ClearNowPlaying();
        StatusTextBlock.Text = "Playback stopped.";
    }

    private void PlayerWindow_RecordingStateChanged(object? sender, EventArgs e)
    {
        if (sender is not PlayerWindow playerWindow)
        {
            return;
        }

        _recordingSchedules = playerWindow.GetRecordingSchedulesSnapshot().ToList();
        _recordingHistory = playerWindow.GetRecordingHistorySnapshot().ToList();
        UpdateSelectedChannelRecordingButton();
        _settingsStore.Save(CollectSettingsFromUi());
    }

    private async Task StepVisibleChannelAsync(int delta)
    {
        if (_visibleChannels.Count == 0)
        {
            StatusTextBlock.Text = "Load channels first to use next/previous channel controls.";
            return;
        }

        var currentIndex = SelectedChannel is null ? -1 : _visibleChannels.IndexOf(SelectedChannel);
        int targetIndex;
        if (currentIndex < 0)
        {
            targetIndex = delta >= 0 ? 0 : _visibleChannels.Count - 1;
        }
        else
        {
            targetIndex = (currentIndex + delta) % _visibleChannels.Count;
            if (targetIndex < 0)
            {
                targetIndex += _visibleChannels.Count;
            }
        }

        var targetChannel = _visibleChannels[targetIndex];
        ChannelsListView.SelectedItem = targetChannel;
        ChannelsListView.ScrollIntoView(targetChannel);
        await PlaySelectedChannelAsync();
    }

    private async Task<bool> PlayRequestAsync(PlaybackRequest request, string successMessage, string nowPlayingText = "")
    {
        try
        {
            var playerWindow = EnsurePlayerWindow(true);
            await playerWindow.PlayAsync(request);
            SetNowPlaying(string.IsNullOrWhiteSpace(nowPlayingText) ? request.DisplayName : nowPlayingText, ResolveLogoFallback(request.LogoSource, request.FallbackLogoSource));
            StatusTextBlock.Text = successMessage;
            return true;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Playback error: {ex.Message}";
            return false;
        }
    }

    private async Task PlaySelectedChannelAsync()
    {
        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
            StatusTextBlock.Text = "Select a channel from the list first.";
            return;
        }

        var request = BuildPlaybackRequest(selectedChannel.StreamUri, selectedChannel.Name, allowReconnect: true, selectedChannel.LogoSource, selectedChannel.GroupFlag);
        if (await PlayRequestAsync(request, $"Playing {selectedChannel.Name}.", BuildNowPlayingText(selectedChannel)))
        {
            RememberPlayedChannel(selectedChannel);
        }
    }

    private async void LoadChannelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetConfiguredUri(out var playlistUri) || playlistUri is null)
        {
            return;
        }

        _settingsStore.Save(CollectSettingsFromUi());
        SetLoadingState(true);
        CancelGuideWarmup();
        _guideCache.Clear();
        _currentXtreamConnection = PlaylistService.TryGetXtreamConnection(playlistUri, out var connection) ? connection : null;
        _pendingSelectionChannelKey = _lastPlayedChannelKey;
        StatusTextBlock.Text = _currentXtreamConnection is not null
            ? "Loading channels via Xtream API..."
            : "Loading playlist...";

        try
        {
            var channels = (await _playlistService.LoadChannelsAsync(playlistUri)).ToList();
            ApplyFavoriteStates(channels);
            ApplyRecentStates(channels);
            ApplyGroupFlags(channels);
            _logoCacheService.RefreshResolvedLogoSources(channels);

            _allChannels.Clear();
            _allChannels.AddRange(channels
                .OrderBy(channel => channel.CleanName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase));

            RefreshGroupOptions();
            SelectDefaultGroupIfAvailable();
            ApplyChannelFilter(selectFirstChannel: true);
            StartLogoWarmup(channels);

            StatusTextBlock.Text = _allChannels.Count == 0
                ? "Channel source loaded, but no playable channels were found."
                : string.IsNullOrWhiteSpace(_lastPlayedChannelKey)
                    ? $"Loaded {_allChannels.Count} channels."
                    : $"Loaded {_allChannels.Count} channels. Last played channel will be reselected when available.";
        }
        catch (Exception ex)
        {
            _allChannels.Clear();
            _visibleChannels.Clear();
            _currentXtreamConnection = null;
            _guideCache.Clear();
            RefreshGroupOptions();
            RefreshSubGroupOptions();
            ClearProgramGuide("Select a channel to load the guide.");
            UpdateSelectedChannelDetails(null);
            UpdateChannelSummary();
            StatusTextBlock.Text = $"Channel load failed: {ex.Message}";
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private void ApplyFavoriteStates(IEnumerable<PlaylistChannel> channels)
    {
        foreach (var channel in channels)
        {
            channel.IsFavorite = _favoriteChannelKeys.Contains(channel.FavoriteKey);
        }
    }

    private void ApplyRecentStates(IEnumerable<PlaylistChannel> channels)
    {
        var recentLookup = _recentChannelKeys
            .Select((key, index) => new { key, index })
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);

        foreach (var channel in channels)
        {
            channel.RecentRank = recentLookup.TryGetValue(channel.FavoriteKey, out var index) ? index : -1;
        }
    }

    private void ApplyGroupFlags(IEnumerable<PlaylistChannel> channels)
    {
        foreach (var channel in channels)
        {
            var groupInfo = _groupFlagStore.Resolve(channel.Name, channel.GroupTitle);
            var canonicalGroup = _canonicalGroupService.Resolve(channel, groupInfo);
            var canonicalSubGroup = _canonicalGroupService.ResolveSubGroup(channel);
            channel.GroupFlag = groupInfo.Flag;
            channel.GroupDisplayTitle = canonicalGroup.Label;
            channel.CanonicalGroupKey = canonicalGroup.Key;
            channel.SubGroupDisplayTitle = canonicalSubGroup.Label;
            channel.CanonicalSubGroupKey = canonicalSubGroup.Key;
            if (_customChannelGroups.TryGetValue(channel.FavoriteKey, out var customGroup) && !string.IsNullOrWhiteSpace(customGroup))
            {
                var label = customGroup.Trim();
                channel.GroupDisplayTitle = label;
                channel.CanonicalGroupKey = BuildCustomGroupKey(label);
                channel.GroupFlag = string.Empty;
            }
        }
    }

    private void RememberPlayedChannel(PlaylistChannel channel)
    {
        _lastPlayedChannelKey = channel.FavoriteKey;
        _recentChannelKeys.RemoveAll(key => string.Equals(key, _lastPlayedChannelKey, StringComparison.OrdinalIgnoreCase));
        _recentChannelKeys.Insert(0, _lastPlayedChannelKey);

        if (_recentChannelKeys.Count > RecentChannelLimit)
        {
            _recentChannelKeys.RemoveRange(RecentChannelLimit, _recentChannelKeys.Count - RecentChannelLimit);
        }

        ApplyRecentStates(_allChannels);
        RefreshGroupOptions();
        RefreshSubGroupOptions();
        ApplyChannelFilter(selectFirstChannel: false);
        UpdateSelectedChannelDetails(channel);
        _settingsStore.Save(CollectSettingsFromUi());
    }

    private void StartLogoWarmup(IReadOnlyList<PlaylistChannel> channels)
    {
        _logoWarmupCts?.Cancel();
        _logoWarmupCts = new CancellationTokenSource();
        var token = _logoWarmupCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await _logoCacheService.WarmCacheAsync(channels.OrderByDescending(channel => channel.IsFavorite), 200, token);
                await Dispatcher.InvokeAsync(() =>
                {
                    _logoCacheService.RefreshResolvedLogoSources(channels);
                    if (SelectedChannel is not null)
                    {
                        UpdateSelectedChannelDetails(SelectedChannel);
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }, token);
    }

    private async Task EnsureSelectedLogoCachedAsync(PlaylistChannel? channel)
    {
        if (channel is null || string.IsNullOrWhiteSpace(channel.TvgLogo))
        {
            return;
        }

        try
        {
            await _logoCacheService.EnsureChannelLogoAsync(channel);
            if (ReferenceEquals(SelectedChannel, channel))
            {
                UpdateSelectedChannelDetails(channel);
            }
        }
        catch
        {
        }
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void GuideWarmupDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _guideWarmupDebounceTimer.Stop();
        StartGuideWarmupForVisibleChannelsCore();
    }

    private void ScheduleGuideWarmupForVisibleChannels()
    {
        _guideWarmupDebounceTimer.Stop();
        _guideWarmupDebounceTimer.Start();
    }

    private void StartGuideWarmupForVisibleChannelsCore()
    {
        CancelGuideWarmup();

        if (_currentXtreamConnection is null)
        {
            return;
        }

        var visibleCandidates = _visibleChannels
            .Where(channel => channel.StreamId is not null)
            .Take(GuideWarmupLimit)
            .ToList();

        foreach (var channel in visibleCandidates)
        {
            if (channel.StreamId is int streamId && _guideCache.TryGetValue(streamId, out var cachedEntries))
            {
                ApplyGuidePreview(channel, cachedEntries);
            }
        }

        var uncachedCandidates = visibleCandidates
            .Where(channel => channel.StreamId is int streamId && !_guideCache.ContainsKey(streamId))
            .ToList();

        if (uncachedCandidates.Count == 0)
        {
            return;
        }

        var connection = _currentXtreamConnection;
        var cts = new CancellationTokenSource();
        _guideWarmupCts = cts;

        _ = Task.Run(async () =>
        {
            foreach (var channel in uncachedCandidates)
            {
                try
                {
                    cts.Token.ThrowIfCancellationRequested();

                    if (channel.StreamId is not int streamId)
                    {
                        continue;
                    }

                    var entries = await _epgService.LoadShortGuideAsync(connection, streamId, cts.Token);
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (cts.IsCancellationRequested || channel.StreamId is not int confirmedStreamId)
                        {
                            return;
                        }

                        _guideCache[confirmedStreamId] = entries;
                        ApplyGuidePreview(channel, entries);
                    });
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
            }
        }, cts.Token);
    }

    private async Task LoadProgramGuideAsync(PlaylistChannel? channel)
    {
        _epgLoadCts?.Cancel();
        _epgLoadCts = null;

        if (channel is null)
        {
            ClearProgramGuide("Select a channel to load the guide.");
            return;
        }

        if (channel.StreamId is not int streamId || _currentXtreamConnection is null)
        {
            ClearProgramGuide("EPG unavailable for this source.");
            return;
        }

        if (_guideCache.TryGetValue(streamId, out var cachedEntries))
        {
            ApplyGuidePreview(channel, cachedEntries);
            PopulateProgramGuide(cachedEntries);
            return;
        }

        var cts = new CancellationTokenSource();
        _epgLoadCts = cts;
        ProgramGuideStatusTextBox.Text = "Loading program guide...";

        try
        {
            var entries = await _epgService.LoadShortGuideAsync(_currentXtreamConnection, streamId, cts.Token);
            if (!ReferenceEquals(_epgLoadCts, cts))
            {
                return;
            }

            _guideCache[streamId] = entries;
            ApplyGuidePreview(channel, entries);
            PopulateProgramGuide(entries);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_epgLoadCts, cts))
            {
                ClearProgramGuide($"EPG load failed: {ex.Message}");
            }
        }
    }

    private void PopulateProgramGuide(IReadOnlyList<ProgramGuideEntry> entries)
    {
        _programGuideEntries.Clear();
        foreach (var entry in entries.OrderBy(entry => entry.StartLocal))
        {
            _programGuideEntries.Add(entry);
        }

        ProgramGuideStatusTextBox.Text = entries.Count == 0
            ? "No guide data available."
            : $"{entries.Count} upcoming programs.";
    }

    private void RecordProgramGuideEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProgramGuideEntry entry)
        {
            StatusTextBlock.Text = "Choose a program guide entry first.";
            return;
        }

        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
            StatusTextBlock.Text = "Select a channel before scheduling a program recording.";
            return;
        }

        RefreshRecordingSettingsFromUi();
        var startAt = entry.StartLocal.AddMinutes(-_recordingStartPaddingMinutes);
        var endAt = entry.EndLocal.AddMinutes(_recordingEndPaddingMinutes);
        if (endAt <= DateTime.Now)
        {
            StatusTextBlock.Text = "That program has already ended.";
            return;
        }

        if (startAt < DateTime.Now)
        {
            startAt = DateTime.Now;
        }

        try
        {
            var request = BuildPlaybackRequest(
                selectedChannel.StreamUri,
                selectedChannel.Name,
                allowReconnect: true,
                selectedChannel.LogoSource,
                selectedChannel.GroupFlag);
            var playerWindow = EnsurePlayerWindow(focusWindow: false, showWindow: false);
            playerWindow.ScheduleRecording(request, startAt, endAt, entry.Title);
            _recordingSchedules = playerWindow.GetRecordingSchedulesSnapshot().ToList();
            _recordingHistory = playerWindow.GetRecordingHistorySnapshot().ToList();
            _settingsStore.Save(CollectSettingsFromUi());
            StatusTextBlock.Text = $"Scheduled {entry.Title} on {selectedChannel.Name} at {startAt:g}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not schedule recording: {ex.Message}";
        }
    }

    private async void RecordSelectedChannelButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        if (_playerWindow?.IsRecording == true)
        {
            if (!IsSelectedChannelActiveRecording(selectedChannel))
            {
                StatusTextBlock.Text = "Another channel is recording. Select that channel to stop it.";
                UpdateSelectedChannelRecordingButton();
                return;
            }

            RecordSelectedChannelButton.IsEnabled = false;
            try
            {
                var completedFile = await _playerWindow.StopRecordingAsync();
                _recordingSchedules = _playerWindow.GetRecordingSchedulesSnapshot().ToList();
                _recordingHistory = _playerWindow.GetRecordingHistorySnapshot().ToList();
                _settingsStore.Save(CollectSettingsFromUi());
                StatusTextBlock.Text = $"Recording stopped. Saved to {completedFile}";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Could not stop recording: {ex.Message}";
            }
            finally
            {
                UpdateSelectedChannelRecordingButton();
            }

            return;
        }

        if (selectedChannel is null)
        {
            StatusTextBlock.Text = "Select a channel before starting a recording.";
            return;
        }

        RecordSelectedChannelButton.IsEnabled = false;
        try
        {
            var request = BuildPlaybackRequest(
                selectedChannel.StreamUri,
                selectedChannel.Name,
                allowReconnect: true,
                selectedChannel.LogoSource,
                selectedChannel.GroupFlag);
            var playerWindow = EnsurePlayerWindow(focusWindow: false, showWindow: false);
            await playerWindow.StartRecordingAsync(request, backgroundRecording: !playerWindow.IsVisible);
            _recordingSchedules = playerWindow.GetRecordingSchedulesSnapshot().ToList();
            _recordingHistory = playerWindow.GetRecordingHistorySnapshot().ToList();
            _settingsStore.Save(CollectSettingsFromUi());
            StatusTextBlock.Text = $"Recording {selectedChannel.Name}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not start recording: {ex.Message}";
        }
        finally
        {
            UpdateSelectedChannelRecordingButton();
        }
    }

    private void ScheduleSelectedChannelButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
            StatusTextBlock.Text = "Select a channel before scheduling a recording.";
            return;
        }

        if (!TryReadSelectedRecordingSchedule(out var startAt, out var endAt, out var errorMessage))
        {
            StatusTextBlock.Text = errorMessage;
            return;
        }

        try
        {
            RefreshRecordingSettingsFromUi();
            var request = BuildPlaybackRequest(
                selectedChannel.StreamUri,
                selectedChannel.Name,
                allowReconnect: true,
                selectedChannel.LogoSource,
                selectedChannel.GroupFlag);
            var playerWindow = EnsurePlayerWindow(focusWindow: false, showWindow: false);
            playerWindow.ScheduleRecording(request, startAt, endAt, string.Empty);
            _recordingSchedules = playerWindow.GetRecordingSchedulesSnapshot().ToList();
            _recordingHistory = playerWindow.GetRecordingHistorySnapshot().ToList();
            _settingsStore.Save(CollectSettingsFromUi());
            StatusTextBlock.Text = $"Scheduled {selectedChannel.Name} at {startAt:g} for {endAt - startAt:hh\\:mm}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Could not schedule recording: {ex.Message}";
        }
    }

    private bool TryReadSelectedRecordingSchedule(out DateTime startAt, out DateTime endAt, out string errorMessage)
    {
        startAt = default;
        endAt = default;
        errorMessage = string.Empty;

        if (SelectedRecordingDatePicker.SelectedDate is not { } selectedDate)
        {
            errorMessage = "Choose a recording date.";
            return false;
        }

        if (!TimeSpan.TryParse(SelectedRecordingTimeTextBox.Text.Trim(), CultureInfo.CurrentCulture, out var startTime))
        {
            errorMessage = "Enter the recording start time, for example 21:30.";
            return false;
        }

        if (!int.TryParse(SelectedRecordingDurationTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var durationMinutes)
            || durationMinutes < 1)
        {
            errorMessage = "Enter a recording length greater than 0 minutes.";
            return false;
        }

        startAt = selectedDate.Date.Add(startTime);
        if (startAt <= DateTime.Now)
        {
            errorMessage = "Choose a future recording start time.";
            return false;
        }

        endAt = startAt.AddMinutes(Math.Clamp(durationMinutes, 1, 1440));
        return true;
    }

    private void ApplyGuidePreview(PlaylistChannel channel, IReadOnlyList<ProgramGuideEntry> entries)
    {
        if (entries.Count == 0)
        {
            channel.NowTitle = string.Empty;
            channel.NextTitle = string.Empty;
            Dispatcher.BeginInvoke(RefreshHighlightedChannelTextBlocks, DispatcherPriority.Background);
            return;
        }

        var orderedEntries = entries.OrderBy(entry => entry.StartLocal).ToList();
        var now = DateTime.Now;
        var currentEntry = orderedEntries.FirstOrDefault(entry => entry.StartLocal <= now && now < entry.EndLocal)
            ?? orderedEntries.FirstOrDefault(entry => entry.EndLocal > now)
            ?? orderedEntries[0];

        var currentIndex = orderedEntries.IndexOf(currentEntry);
        var nextEntry = currentIndex >= 0 && currentIndex + 1 < orderedEntries.Count
            ? orderedEntries[currentIndex + 1]
            : null;

        channel.NowTitle = currentEntry is null ? string.Empty : $"{currentEntry.StartLocal:HH:mm} {currentEntry.Title}";
        channel.NextTitle = nextEntry is null ? string.Empty : $"{nextEntry.StartLocal:HH:mm} {nextEntry.Title}";
        Dispatcher.BeginInvoke(RefreshHighlightedChannelTextBlocks, DispatcherPriority.Background);
    }

    private void ClearProgramGuide(string message)
    {
        _programGuideEntries.Clear();
        ProgramGuideStatusTextBox.Text = message;
    }

    private void RefreshGroupOptions()
    {
        var selectedGroupValue = SelectedGroupOption?.Value;

        _groupOptions.Clear();
        _groupOptions.Add(BuildAllGroupsOption());
        if (_allChannels.Any(channel => channel.IsFavorite))
        {
            _groupOptions.Add(BuildFavoritesGroupOption());
        }

        if (_allChannels.Any(channel => channel.RecentRank >= 0))
        {
            _groupOptions.Add(BuildRecentlyWatchedGroupOption());
        }

        var options = _allChannels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.CanonicalGroupKey))
            .GroupBy(channel => channel.CanonicalGroupKey.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new GroupFilterOption(
                group.Key,
                group.Select(channel => channel.GroupDisplayTitle?.Trim())
                    .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
                    ?? group.Key))
            .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.Value, StringComparer.CurrentCultureIgnoreCase);

        foreach (var option in options)
        {
            _groupOptions.Add(option);
        }

        GroupFilterComboBox.SelectedItem = _groupOptions.FirstOrDefault(option =>
            string.Equals(option.Value, selectedGroupValue, StringComparison.CurrentCultureIgnoreCase))
            ?? _groupOptions[0];
    }

    private void RefreshSubGroupOptions(string? preferredSubGroupValue = null)
    {
        var selectedSubGroupValue = preferredSubGroupValue ?? SelectedSubGroupOption?.Value;
        var selectedGroup = SelectedGroupOption?.Value;
        var previousSuppressFilterRefresh = _suppressFilterRefresh;

        _suppressFilterRefresh = true;
        try
        {
            _subGroupOptions.Clear();
            _subGroupOptions.Add(BuildAllSubGroupsOption());

            var options = _allChannels
                .Where(channel => MatchesGroup(channel, selectedGroup))
                .Where(channel => !string.IsNullOrWhiteSpace(channel.CanonicalSubGroupKey))
                .GroupBy(channel => channel.CanonicalSubGroupKey.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new GroupFilterOption(
                    group.Key,
                    group.Select(channel => channel.SubGroupDisplayTitle?.Trim())
                        .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
                        ?? group.Key))
                .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(option => option.Value, StringComparer.CurrentCultureIgnoreCase);

            foreach (var option in options)
            {
                _subGroupOptions.Add(option);
            }

            SubGroupFilterComboBox.SelectedItem = _subGroupOptions.FirstOrDefault(option =>
                string.Equals(option.Value, selectedSubGroupValue, StringComparison.CurrentCultureIgnoreCase)
                || string.Equals(option.Label, selectedSubGroupValue, StringComparison.CurrentCultureIgnoreCase))
                ?? _subGroupOptions[0];
        }
        finally
        {
            _suppressFilterRefresh = previousSuppressFilterRefresh;
        }
    }

    private void SelectDefaultGroupIfAvailable()
    {
        if (string.IsNullOrWhiteSpace(_defaultGroup))
        {
            GroupFilterComboBox.SelectedItem = ResolvePreferredGroupOption(_savedGroupFilter) ?? _groupOptions[0];
            RefreshSubGroupOptions(_savedSubGroupFilter);
            return;
        }

        var matchedGroup = ResolvePreferredGroupOption(_savedGroupFilter) ?? _groupOptions.FirstOrDefault(option =>
            string.Equals(option.Value, _defaultGroup, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(option.Label, _defaultGroup, StringComparison.CurrentCultureIgnoreCase));
        GroupFilterComboBox.SelectedItem = matchedGroup ?? _groupOptions[0];
        RefreshSubGroupOptions(_savedSubGroupFilter);
    }

    private GroupFilterOption? ResolvePreferredGroupOption(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return _groupOptions.FirstOrDefault(option =>
            string.Equals(option.Value, value, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(option.Label, value, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ApplyChannelFilter(bool selectFirstChannel)
    {
        var selectedStream = SelectedChannel?.StreamUri.ToString();
        var preferredChannelKey = selectFirstChannel ? _pendingSelectionChannelKey : string.Empty;
        var searchText = ChannelSearchTextBox.Text.Trim();
        var selectedGroup = SelectedGroupOption?.Value;
        var selectedSubGroup = SelectedSubGroupOption?.Value;
        var searchAllGroups = _searchAllGroups && !string.IsNullOrWhiteSpace(searchText);

        var filteredChannels = _allChannels
            .Where(channel => !HasTemporaryChannelList || _temporaryChannelOrder.ContainsKey(channel.StreamUri.ToString()))
            .Where(channel => MatchesSearch(channel, searchText))
            .Where(channel => searchAllGroups || (MatchesGroup(channel, selectedGroup) && MatchesSubGroup(channel, selectedSubGroup)))
            .Where(channel => !_favoritesOnly || channel.IsFavorite)
            .Where(channel => !_recentOnly || channel.RecentRank >= 0);

        var orderedChannels = SortChannels(filteredChannels);

        _visibleChannels.Clear();
        foreach (var channel in orderedChannels)
        {
            _visibleChannels.Add(channel);
        }

        ChannelsListView.SelectedItem = null;

        if (!string.IsNullOrWhiteSpace(selectedStream))
        {
            ChannelsListView.SelectedItem = _visibleChannels.FirstOrDefault(channel => string.Equals(channel.StreamUri.ToString(), selectedStream, StringComparison.OrdinalIgnoreCase));
        }

        if (ChannelsListView.SelectedItem is null && !string.IsNullOrWhiteSpace(preferredChannelKey))
        {
            ChannelsListView.SelectedItem = _visibleChannels.FirstOrDefault(channel => string.Equals(channel.FavoriteKey, preferredChannelKey, StringComparison.OrdinalIgnoreCase));
        }

        if (selectFirstChannel)
        {
            _pendingSelectionChannelKey = string.Empty;
        }

        if (ChannelsListView.SelectedItem is null && selectFirstChannel && _visibleChannels.Count > 0)
        {
            ChannelsListView.SelectedIndex = 0;
        }

        if (ChannelsListView.SelectedItem is null)
        {
            UpdateSelectedChannelDetails(null);
            ClearProgramGuide("Select a channel to load the guide.");
        }

        UpdateChannelSummary();
        Dispatcher.BeginInvoke(RefreshHighlightedChannelTextBlocks, DispatcherPriority.Background);
        ScheduleGuideWarmupForVisibleChannels();
    }

    private IOrderedEnumerable<PlaylistChannel> SortChannels(IEnumerable<PlaylistChannel> channels)
    {
        var sortedChannels = _channelSortDirection == ListSortDirection.Descending
            ? channels.OrderByDescending(channel => GetChannelSortText(channel, _channelSortKey), StringComparer.CurrentCultureIgnoreCase)
            : channels.OrderBy(channel => GetChannelSortText(channel, _channelSortKey), StringComparer.CurrentCultureIgnoreCase);

        return sortedChannels
            .ThenBy(channel => channel.CleanName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private static string GetChannelSortText(PlaylistChannel channel, string sortKey)
    {
        return sortKey switch
        {
            "Fav" => channel.IsFavorite ? "0" : "1",
            "Recent" => channel.RecentRank >= 0 ? channel.RecentRank.ToString("D6", CultureInfo.InvariantCulture) : "999999",
            "Flag" => channel.GroupDisplayTitle,
            "Logo" => channel.LogoSource,
            "Channel" => channel.Name,
            "Clean Name" => channel.CleanName,
            "Now" => channel.NowTitle,
            "Next" => channel.NextTitle,
            "Group" => channel.GroupDisplayTitle,
            "Source Group" => channel.GroupTitle,
            _ => channel.CleanName
        };
    }

    private static bool MatchesSearch(PlaylistChannel channel, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return channel.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.CleanName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.GroupTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.GroupDisplayTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.SubGroupDisplayTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.TvgId.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.NowTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.NextTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesGroup(PlaylistChannel channel, string? selectedGroup)
    {
        if (string.Equals(selectedGroup, FavoritesGroupValue, StringComparison.Ordinal))
        {
            return channel.IsFavorite;
        }

        if (string.Equals(selectedGroup, RecentlyWatchedGroupValue, StringComparison.Ordinal))
        {
            return channel.RecentRank >= 0;
        }

        return string.IsNullOrWhiteSpace(selectedGroup)
            || string.Equals(selectedGroup, AllGroupsLabel, StringComparison.Ordinal)
            || string.Equals(channel.CanonicalGroupKey.Trim(), selectedGroup, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesSubGroup(PlaylistChannel channel, string? selectedSubGroup)
    {
        return string.IsNullOrWhiteSpace(selectedSubGroup)
            || string.Equals(selectedSubGroup, AllSubGroupsLabel, StringComparison.Ordinal)
            || string.Equals(channel.CanonicalSubGroupKey.Trim(), selectedSubGroup, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateChannelSummary()
    {
        if (_allChannels.Count == 0)
        {
            ChannelSummaryTextBox.Text = "No channels loaded.";
            UpdateExportM3uUi();
            return;
        }

        var summary = $"Showing {_visibleChannels.Count} of {_allChannels.Count} channels.";

        if (_favoritesOnly)
        {
            summary += " Favorites only.";
        }

        if (_recentOnly)
        {
            summary += " Recent only.";
        }

        if (SelectedGroupOption is { Value: not "" } selectedGroupOption
            && !string.Equals(selectedGroupOption.Value, AllGroupsLabel, StringComparison.Ordinal))
        {
            var searchAllGroups = _searchAllGroups && !string.IsNullOrWhiteSpace(ChannelSearchTextBox.Text.Trim());
            summary += searchAllGroups
                ? $" Group: {selectedGroupOption.Label} (ignored while searching)."
                : $" Group: {selectedGroupOption.Label}.";
        }

        if (SelectedSubGroupOption is { Value: not "" } selectedSubGroupOption
            && !string.Equals(selectedSubGroupOption.Value, AllSubGroupsLabel, StringComparison.Ordinal))
        {
            var searchAllGroups = _searchAllGroups && !string.IsNullOrWhiteSpace(ChannelSearchTextBox.Text.Trim());
            summary += searchAllGroups
                ? $" Subgroup: {selectedSubGroupOption.Label} (ignored while searching)."
                : $" Subgroup: {selectedSubGroupOption.Label}.";
        }

        if (_searchAllGroups && !string.IsNullOrWhiteSpace(ChannelSearchTextBox.Text.Trim()))
        {
            summary += " Searching across all groups.";
        }

        if (HasTemporaryChannelList)
        {
            summary += $" Temporary list: {_temporaryListLabel} ({_temporaryChannelOrder.Count} channels).";
        }

        summary += _recentChannelKeys.Count > 0 ? $" Recents tracked: {_recentChannelKeys.Count}." : string.Empty;
        ChannelSummaryTextBox.Text = summary;
        UpdateExportM3uUi();
    }

    private void ApplyTemporaryChannelList(TemporaryChannelListSelection selection)
    {
        _temporaryChannelOrder.Clear();
        for (var index = 0; index < selection.StreamKeys.Count; index++)
        {
            var streamKey = selection.StreamKeys[index];
            if (!string.IsNullOrWhiteSpace(streamKey) && !_temporaryChannelOrder.ContainsKey(streamKey))
            {
                _temporaryChannelOrder[streamKey] = index;
            }
        }

        _temporaryListLabel = string.IsNullOrWhiteSpace(selection.Label) ? "TV listings" : selection.Label.Trim();
        GroupFilterComboBox.SelectedIndex = 0;
        RefreshSubGroupOptions(AllSubGroupsLabel);
        ChannelSearchTextBox.Text = string.Empty;
        _favoritesOnly = false;
        _recentOnly = false;
        UpdateToggleButtonStates();
        UpdateTemporaryListUi();
        ApplyChannelFilter(selectFirstChannel: true);
    }

    private void ClearTemporaryChannelList(string statusMessage = "Temporary channel list cleared.")
    {
        if (!HasTemporaryChannelList)
        {
            UpdateTemporaryListUi();
            return;
        }

        _temporaryChannelOrder.Clear();
        _temporaryListLabel = string.Empty;
        UpdateTemporaryListUi();
        ApplyChannelFilter(selectFirstChannel: false);
        StatusTextBlock.Text = statusMessage;
    }

    private void UpdateTemporaryListUi()
    {
        ClearTempListButton.IsEnabled = HasTemporaryChannelList;
        UpdateExportM3uUi();
    }

    private void ClearFilters(string statusMessage = "Filters cleared.")
    {
        _searchDebounceTimer.Stop();
        _temporaryChannelOrder.Clear();
        _temporaryListLabel = string.Empty;
        _searchAllGroups = false;
        _favoritesOnly = false;
        _recentOnly = false;

        _suppressFilterRefresh = true;
        try
        {
            ChannelSearchTextBox.Text = string.Empty;
            GroupFilterComboBox.SelectedIndex = 0;
            RefreshSubGroupOptions();
            SubGroupFilterComboBox.SelectedIndex = 0;
        }
        finally
        {
            _suppressFilterRefresh = false;
        }

        UpdateToggleButtonStates();
        UpdateTemporaryListUi();
        ApplyChannelFilter(selectFirstChannel: false);
        StatusTextBlock.Text = statusMessage;
    }

    private void UpdateExportM3uUi()
    {
        var hasChannels = _allChannels.Count > 0;
        var hasSpecificGroup = SelectedGroupOption is { } selectedGroupOption
            && !string.Equals(selectedGroupOption.Value, AllGroupsLabel, StringComparison.Ordinal);

        ExportM3uButton.IsEnabled = hasChannels;
        ExportAllM3uMenuItem.IsEnabled = hasChannels;
        ExportFavoritesM3uMenuItem.IsEnabled = hasChannels && _allChannels.Any(channel => channel.IsFavorite);
        ExportSelectedGroupM3uMenuItem.IsEnabled = hasChannels && hasSpecificGroup;
        ExportVisibleM3uMenuItem.IsEnabled = _visibleChannels.Count > 0;
        ExportTemporaryM3uMenuItem.IsEnabled = HasTemporaryChannelList;
    }

    private void UpdateSelectedChannelDetails(PlaylistChannel? channel)
    {
        if (channel is null)
        {
            SelectedChannelNameTextBox.Text = "No channel selected";
            SelectedChannelFavoriteStateTextBox.Text = "Not a favorite";
            SelectedChannelGroupTextBox.Text = "Load a playlist to browse channels.";
            SelectedChannelIdTextBox.Text = "Not available";
        SelectedChannelUrlTextBox.Text = "Load the playlist and choose a channel from the list.";
        SelectedChannelLogoImage.Source = null;
        CustomGroupTextBox.Text = string.Empty;
        CustomGroupTextBox.IsEnabled = false;
            ApplyCustomGroupButton.IsEnabled = false;
            ClearCustomGroupButton.IsEnabled = false;
            ToggleFavoriteButton.IsEnabled = false;
            ToggleFavoriteButton.Content = "Add Favorite";
            UpdateSelectedChannelRecordingButton();
            return;
        }

        SelectedChannelNameTextBox.Text = channel.Name;
        SelectedChannelFavoriteStateTextBox.Text = BuildChannelStateText(channel);
        SelectedChannelGroupTextBox.Text = BuildChannelGroupText(channel);
        SelectedChannelIdTextBox.Text = string.IsNullOrWhiteSpace(channel.TvgId)
            ? channel.StreamId?.ToString() ?? "Not available"
            : channel.TvgId;
        SelectedChannelUrlTextBox.Text = channel.StreamUri.ToString();
        SelectedChannelLogoImage.Source = CreateLogoSource(channel.LogoSource);
        CustomGroupTextBox.IsEnabled = true;
        ApplyCustomGroupButton.IsEnabled = true;
        ClearCustomGroupButton.IsEnabled = true;
        CustomGroupTextBox.Text = _customChannelGroups.TryGetValue(channel.FavoriteKey, out var customGroup)
            ? customGroup
            : string.Empty;
        ToggleFavoriteButton.IsEnabled = true;
        ToggleFavoriteButton.Content = channel.IsFavorite ? "Remove Favorite" : "Add Favorite";
        UpdateSelectedChannelRecordingButton();
    }

    private void UpdateSelectedChannelRecordingButton()
    {
        var selectedChannel = SelectedChannel;
        var hasSelectedChannel = selectedChannel is not null;
        SelectedRecordingDatePicker.IsEnabled = hasSelectedChannel;
        SelectedRecordingTimeTextBox.IsEnabled = hasSelectedChannel;
        SelectedRecordingDurationTextBox.IsEnabled = hasSelectedChannel;
        ScheduleSelectedChannelButton.IsEnabled = hasSelectedChannel;

        if (_playerWindow?.IsRecording == true)
        {
            var displayName = string.IsNullOrWhiteSpace(_playerWindow.ActiveRecordingDisplayName)
                ? "recording"
                : _playerWindow.ActiveRecordingDisplayName;
            if (IsSelectedChannelActiveRecording(selectedChannel))
            {
                RecordSelectedChannelButton.IsEnabled = true;
                RecordSelectedChannelButton.Content = "Stop Rec";
                RecordSelectedChannelButton.ToolTip = $"Stop recording {displayName}.";
            }
            else
            {
                RecordSelectedChannelButton.IsEnabled = false;
                RecordSelectedChannelButton.Content = "Recording";
                RecordSelectedChannelButton.ToolTip = $"Recording {displayName}. Select that channel to stop it.";
            }

            return;
        }

        RecordSelectedChannelButton.IsEnabled = hasSelectedChannel;
        RecordSelectedChannelButton.Content = "Record";
        RecordSelectedChannelButton.ToolTip = "Start recording the selected channel without opening the player window.";
    }

    private bool IsSelectedChannelActiveRecording(PlaylistChannel? selectedChannel)
    {
        return selectedChannel is not null
            && _playerWindow?.IsRecording == true
            && string.Equals(
                _playerWindow.ActiveRecordingStreamUri,
                selectedChannel.StreamUri.ToString(),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNowPlayingText(PlaylistChannel channel)
    {
        return string.IsNullOrWhiteSpace(channel.GroupDisplayTitle)
            ? channel.Name
            : $"{channel.Name} - {channel.GroupDisplayTitle}";
    }

    private void SetNowPlaying(string displayText, string logoSource)
    {
        NowPlayingTextBlock.Text = $"Now Playing: {displayText}";

        var logo = CreateLogoSource(logoSource);
        NowPlayingLogoImage.Source = logo;
        NowPlayingLogoImage.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearNowPlaying()
    {
        NowPlayingTextBlock.Text = "Now Playing: None";
        NowPlayingLogoImage.Source = null;
        NowPlayingLogoImage.Visibility = Visibility.Collapsed;
    }

    private static string BuildChannelStateText(PlaylistChannel channel)
    {
        var stateParts = new List<string>
        {
            channel.IsFavorite ? "Favorite" : "Not a favorite"
        };

        if (channel.RecentRank == 0)
        {
            stateParts.Add("Last played");
        }
        else if (channel.RecentRank > 0)
        {
            stateParts.Add($"Recent #{channel.RecentRank + 1}");
        }

        return string.Join(" | ", stateParts);
    }

    private static string BuildChannelGroupText(PlaylistChannel channel)
    {
        var canonicalGroup = string.IsNullOrWhiteSpace(channel.GroupDisplayTitle)
            ? "Ungrouped"
            : channel.GroupDisplayTitle.Trim();
        var subGroup = channel.SubGroupDisplayTitle?.Trim() ?? string.Empty;
        var rawGroup = channel.GroupTitle?.Trim() ?? string.Empty;
        var displayGroup = string.IsNullOrWhiteSpace(subGroup) || string.Equals(subGroup, canonicalGroup, StringComparison.CurrentCultureIgnoreCase)
            ? canonicalGroup
            : $"{canonicalGroup} / {subGroup}";

        return string.IsNullOrWhiteSpace(rawGroup) || string.Equals(rawGroup, displayGroup, StringComparison.CurrentCultureIgnoreCase)
            ? displayGroup
            : $"{displayGroup} (source: {rawGroup})";
    }

    private static ImageSource? CreateLogoSource(string logoSource)
    {
        if (!Uri.TryCreate(logoSource, UriKind.RelativeOrAbsolute, out var uri))
        {
            return null;
        }

        try
        {
            return new BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveLogoFallback(string logoSource, string fallbackLogoSource)
    {
        return string.IsNullOrWhiteSpace(logoSource) ? fallbackLogoSource : logoSource;
    }

    private void HighlightedChannelTextBlock_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            UpdateHighlightedChannelTextBlock(textBlock);
        }
    }

    private void HighlightedChannelTextBlock_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBlock textBlock)
        {
            UpdateHighlightedChannelTextBlock(textBlock);
        }
    }

    private void RefreshHighlightedChannelTextBlocks()
    {
        foreach (var textBlock in FindVisualChildren<TextBlock>(ChannelsListView))
        {
            if (textBlock.Tag is string)
            {
                UpdateHighlightedChannelTextBlock(textBlock);
            }
        }
    }

    private void UpdateHighlightedChannelTextBlock(TextBlock textBlock)
    {
        if (textBlock.DataContext is not PlaylistChannel channel || textBlock.Tag is not string fieldName)
        {
            return;
        }

        var text = GetChannelFieldText(channel, fieldName);
        textBlock.Inlines.Clear();

        var searchText = ChannelSearchTextBox.Text.Trim();
        var matchIndex = string.IsNullOrWhiteSpace(searchText)
            ? -1
            : text.IndexOf(searchText, StringComparison.CurrentCultureIgnoreCase);

        if (matchIndex < 0)
        {
            textBlock.Inlines.Add(new Run(text));
            return;
        }

        if (matchIndex > 0)
        {
            textBlock.Inlines.Add(new Run(text[..matchIndex]));
        }

        var match = new Run(text.Substring(matchIndex, searchText.Length))
        {
            Background = TryFindResource("SelectionBrush") as Brush,
            Foreground = TryFindResource("SelectionTextBrush") as Brush,
            FontWeight = FontWeights.SemiBold
        };
        textBlock.Inlines.Add(match);

        var afterMatchIndex = matchIndex + searchText.Length;
        if (afterMatchIndex < text.Length)
        {
            textBlock.Inlines.Add(new Run(text[afterMatchIndex..]));
        }
    }

    private static string GetChannelFieldText(PlaylistChannel channel, string fieldName)
    {
        return fieldName switch
        {
            "Name" => channel.Name,
            "CleanName" => channel.CleanName,
            "NowTitle" => channel.NowTitle,
            "NextTitle" => channel.NextTitle,
            "GroupDisplayTitle" => channel.GroupDisplayTitle,
            "GroupTitle" => channel.GroupTitle,
            _ => string.Empty
        };
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadChannelsButton.IsEnabled = !isLoading;
    }

    private string ResolveRecordingsDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_appConfig.RecordingsDirectory))
        {
            return _appConfig.RecordingsDirectory.Trim();
        }

        return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Schmube");
    }

    private static string BuildDisplayName(Uri streamUri)
    {
        var lastSegment = streamUri.Segments.Length > 0 ? streamUri.Segments[^1].Trim('/') : string.Empty;
        return string.IsNullOrWhiteSpace(lastSegment) ? streamUri.Host : lastSegment;
    }

    private void CancelGuideWarmup()
    {
        _guideWarmupDebounceTimer.Stop();
        _guideWarmupCts?.Cancel();
        _guideWarmupCts?.Dispose();
        _guideWarmupCts = null;
    }

    private void ToggleFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleSelectedChannelFavorite();
    }

    private void ApplyCustomGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
            return;
        }

        var groupName = CustomGroupTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(groupName))
        {
            ClearCustomGroupButton_Click(sender, e);
            return;
        }

        _customChannelGroups[selectedChannel.FavoriteKey] = groupName;
        selectedChannel.GroupDisplayTitle = groupName;
        selectedChannel.CanonicalGroupKey = BuildCustomGroupKey(groupName);
        selectedChannel.GroupFlag = string.Empty;
        RefreshGroupOptions();
        GroupFilterComboBox.SelectedItem = ResolvePreferredGroupOption(selectedChannel.CanonicalGroupKey) ?? GroupFilterComboBox.SelectedItem;
        RefreshSubGroupOptions();
        ApplyChannelFilter(selectFirstChannel: false);
        UpdateSelectedChannelDetails(selectedChannel);
        _settingsStore.Save(CollectSettingsFromUi());
        StatusTextBlock.Text = $"Custom group set to {groupName}.";
    }

    private void ClearCustomGroupButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
            return;
        }

        if (!_customChannelGroups.Remove(selectedChannel.FavoriteKey))
        {
            CustomGroupTextBox.Text = string.Empty;
            StatusTextBlock.Text = "No custom group override was set.";
            return;
        }

        ApplyGroupFlags([selectedChannel]);
        RefreshGroupOptions();
        RefreshSubGroupOptions();
        ApplyChannelFilter(selectFirstChannel: false);
        UpdateSelectedChannelDetails(selectedChannel);
        _settingsStore.Save(CollectSettingsFromUi());
        StatusTextBlock.Text = "Custom group override cleared.";
    }

    private void ToggleSelectedChannelFavorite()
    {
        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
            StatusTextBlock.Text = "Select a channel before changing favorites.";
            return;
        }

        if (selectedChannel.IsFavorite)
        {
            _favoriteChannelKeys.Remove(selectedChannel.FavoriteKey);
            selectedChannel.IsFavorite = false;
            StatusTextBlock.Text = $"Removed {selectedChannel.Name} from favorites.";
        }
        else
        {
            _favoriteChannelKeys.Add(selectedChannel.FavoriteKey);
            selectedChannel.IsFavorite = true;
            StatusTextBlock.Text = $"Added {selectedChannel.Name} to favorites.";
        }

        _settingsStore.Save(CollectSettingsFromUi());
        RefreshGroupOptions();
        RefreshSubGroupOptions();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void TvListingsButton_Click(object sender, RoutedEventArgs e)
    {
        var listingsWindow = new ListingsWindow(_allChannels)
        {
            Owner = this
        };

        if (listingsWindow.ShowDialog() != true || listingsWindow.TemporarySelection is null)
        {
            return;
        }

        ApplyTemporaryChannelList(listingsWindow.TemporarySelection);
        StatusTextBlock.Text = $"Temporary channel list loaded from {_temporaryListLabel}.";
    }

    private void ExportM3uButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateExportM3uUi();
        ExportM3uButton.ContextMenu.PlacementTarget = ExportM3uButton;
        ExportM3uButton.ContextMenu.IsOpen = true;
    }

    private void ColumnsButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateColumnPresetUi();
        ColumnsButton.ContextMenu.PlacementTarget = ColumnsButton;
        ColumnsButton.ContextMenu.IsOpen = true;
    }

    private void CompactColumnsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyColumnPreset(ChannelColumnPreset.Compact);
    }

    private void StandardColumnsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyColumnPreset(ChannelColumnPreset.Standard);
    }

    private void DetailColumnsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyColumnPreset(ChannelColumnPreset.Detail);
    }

    private void ApplyColumnPreset(ChannelColumnPreset preset)
    {
        _columnPreset = preset;
        switch (preset)
        {
            case ChannelColumnPreset.Compact:
                SetChannelColumnWidths(favorite: 34, recent: 0, flag: 72, logo: 0, cleanName: 260, channel: 0, now: 220, next: 0, group: 130, sourceGroup: 0);
                break;
            case ChannelColumnPreset.Detail:
                SetChannelColumnWidths(favorite: 38, recent: 52, flag: 96, logo: 64, cleanName: 240, channel: 220, now: 210, next: 210, group: 170, sourceGroup: 210);
                break;
            default:
                SetChannelColumnWidths(favorite: 38, recent: 52, flag: 96, logo: 64, cleanName: 230, channel: 180, now: 180, next: 180, group: 160, sourceGroup: 190);
                break;
        }

        UpdateColumnPresetUi();
    }

    private static ChannelColumnPreset ParseColumnPreset(string? value)
    {
        return Enum.TryParse<ChannelColumnPreset>(value, ignoreCase: true, out var preset)
            ? preset
            : ChannelColumnPreset.Standard;
    }

    private void SetChannelColumnWidths(double favorite, double recent, double flag, double logo, double cleanName, double channel, double now, double next, double group, double sourceGroup)
    {
        FavoriteColumn.Width = favorite;
        RecentColumn.Width = recent;
        FlagColumn.Width = flag;
        LogoColumn.Width = logo;
        CleanNameColumn.Width = cleanName;
        ChannelColumn.Width = channel;
        NowColumn.Width = now;
        NextColumn.Width = next;
        GroupColumn.Width = group;
        SourceGroupColumn.Width = sourceGroup;
    }

    private void UpdateColumnPresetUi()
    {
        CompactColumnsMenuItem.IsChecked = _columnPreset == ChannelColumnPreset.Compact;
        StandardColumnsMenuItem.IsChecked = _columnPreset == ChannelColumnPreset.Standard;
        DetailColumnsMenuItem.IsChecked = _columnPreset == ChannelColumnPreset.Detail;
    }

    private void ExportAllM3uMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExportM3uChannels("complete list", _allChannels, "schmube-complete-list");
    }

    private void ExportFavoritesM3uMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var channels = _allChannels
            .Where(channel => channel.IsFavorite)
            .OrderBy(channel => channel.GroupDisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ExportM3uChannels("favorites", channels, "schmube-favorites");
    }

    private void ExportSelectedGroupM3uMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var selectedGroup = SelectedGroupOption;
        if (selectedGroup is null || string.Equals(selectedGroup.Value, AllGroupsLabel, StringComparison.Ordinal))
        {
            StatusTextBlock.Text = "Select a specific group before exporting a group M3U.";
            return;
        }

        var selectedSubGroup = SelectedSubGroupOption;
        var hasSubGroupFilter = selectedSubGroup is not null
            && !string.Equals(selectedSubGroup.Value, AllSubGroupsLabel, StringComparison.Ordinal);
        var exportLabel = hasSubGroupFilter
            ? $"{selectedGroup.Label} - {selectedSubGroup!.Label}"
            : selectedGroup.Label;
        var channels = _allChannels
            .Where(channel => MatchesGroup(channel, selectedGroup.Value))
            .Where(channel => !hasSubGroupFilter || MatchesSubGroup(channel, selectedSubGroup!.Value))
            .OrderBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ExportM3uChannels(exportLabel, channels, $"schmube-{BuildSafeFileName(exportLabel)}");
    }

    private void ExportVisibleM3uMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ExportM3uChannels("current visible list", _visibleChannels, "schmube-visible-list");
    }

    private void ExportTemporaryM3uMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!HasTemporaryChannelList)
        {
            StatusTextBlock.Text = "No temporary channel list is active.";
            return;
        }

        var channels = _allChannels
            .Where(channel => _temporaryChannelOrder.ContainsKey(channel.StreamUri.ToString()))
            .OrderBy(channel => _temporaryChannelOrder[channel.StreamUri.ToString()])
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var label = string.IsNullOrWhiteSpace(_temporaryListLabel) ? "temporary list" : _temporaryListLabel;

        ExportM3uChannels(label, channels, $"schmube-temp-{BuildSafeFileName(label)}");
    }

    private void ExportM3uChannels(string label, IEnumerable<PlaylistChannel> channels, string defaultFileName)
    {
        var channelList = channels.ToList();
        if (channelList.Count == 0)
        {
            StatusTextBlock.Text = $"No channels available to export for {label}.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".m3u",
            FileName = $"{BuildSafeFileName(defaultFileName)}.m3u",
            Filter = "M3U playlists (*.m3u)|*.m3u|M3U8 playlists (*.m3u8)|*.m3u8|All files (*.*)|*.*",
            OverwritePrompt = true,
            Title = $"Export {label} to M3U"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            M3uExportService.Export(dialog.FileName, channelList);
            StatusTextBlock.Text = $"Exported {channelList.Count} channels to {dialog.FileName}.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"M3U export failed: {ex.Message}";
        }
    }

    private static string BuildSafeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((value ?? string.Empty)
            .Select(ch => invalidCharacters.Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim(' ', '.', '-');

        return string.IsNullOrWhiteSpace(sanitized) ? "schmube-export" : sanitized;
    }

    private static string BuildCustomGroupKey(string groupName)
    {
        var trimmed = groupName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "CUSTOM";
        }

        var normalized = new string(trimmed
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        return string.IsNullOrWhiteSpace(normalized) ? "CUSTOM" : $"CUSTOM_{normalized}";
    }

    private void ClearTempListButton_Click(object sender, RoutedEventArgs e)
    {
        ClearTemporaryChannelList();
    }

    private void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ClearFilters();
    }

    private async void PlayUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildDirectRequest(out var request) || request is null)
        {
            return;
        }

        await PlayRequestAsync(request, $"Playing {request.DisplayName}.");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRecordingSettingsFromUi();
        _playerWindow?.ConfigureRecordingDefaults(_recordingDefaultDurationMinutes, _recordingFileNameFormat);
        _settingsStore.Save(CollectSettingsFromUi());
        StatusTextBlock.Text = "Settings saved.";
    }

    private void KeepOnTopButton_Click(object sender, RoutedEventArgs e)
    {
        _keepPlayerOnTop = !_keepPlayerOnTop;
        _playerWindow?.SetAlwaysOnTop(_keepPlayerOnTop);
        _settingsStore.Save(CollectSettingsFromUi());
        UpdateToggleButtonStates();
        StatusTextBlock.Text = _keepPlayerOnTop ? "Player window stays on top." : "Player window on-top disabled.";
    }

    private void DarkModeButton_Click(object sender, RoutedEventArgs e)
    {
        _useDarkMode = !_useDarkMode;
        ThemeService.ApplyTheme(_useDarkMode);
        _settingsStore.Save(CollectSettingsFromUi());
        UpdateToggleButtonStates();
        StatusTextBlock.Text = _useDarkMode ? "Dark mode enabled." : "Dark mode disabled.";
    }

    private async void ChannelsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await PlaySelectedChannelAsync();
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (ExportM3uButton.IsEnabled)
            {
                ExportM3uButton_Click(ExportM3uButton, new RoutedEventArgs());
            }

            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if ((e.Key == Key.OemQuestion || e.Key == Key.Divide) && !IsTextInputFocused())
        {
            ChannelSearchTextBox.Focus();
            ChannelSearchTextBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && (ReferenceEquals(Keyboard.FocusedElement, ChannelSearchTextBox) || !IsTextInputFocused()))
        {
            await PlaySelectedChannelAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && !IsTextInputFocused())
        {
            ToggleSelectedChannelFavorite();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            ClearFilters();
            e.Handled = true;
        }
    }

    private async void ChannelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        UpdateSelectedChannelDetails(selectedChannel);
        await EnsureSelectedLogoCachedAsync(selectedChannel);
        await LoadProgramGuideAsync(selectedChannel);
    }

    private void ChannelsGridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } header)
        {
            return;
        }

        var sortKey = (column.Header as string ?? header.Content as string)?.Trim();
        if (string.IsNullOrWhiteSpace(sortKey))
        {
            return;
        }

        if (string.Equals(_channelSortKey, sortKey, StringComparison.Ordinal))
        {
            _channelSortDirection = _channelSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            _channelSortKey = sortKey;
            _channelSortDirection = ListSortDirection.Ascending;
        }

        ApplyChannelFilter(selectFirstChannel: false);
        StatusTextBlock.Text = $"Sorted channels by {sortKey} {(_channelSortDirection == ListSortDirection.Ascending ? "ascending" : "descending")}.";
    }

    private void ChannelSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFilterRefresh)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void GroupFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterRefresh)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        RefreshSubGroupOptions();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void SubGroupFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterRefresh)
        {
            return;
        }

        _searchDebounceTimer.Stop();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void FavoritesOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        _favoritesOnly = !_favoritesOnly;
        UpdateToggleButtonStates();
        _searchDebounceTimer.Stop();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void SearchAllGroupsButton_Click(object sender, RoutedEventArgs e)
    {
        _searchAllGroups = !_searchAllGroups;
        UpdateToggleButtonStates();
        _searchDebounceTimer.Stop();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void RecentOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        _recentOnly = !_recentOnly;
        UpdateToggleButtonStates();
        _searchDebounceTimer.Stop();
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void UpdateToggleButtonStates()
    {
        KeepOnTopButton.Tag = _keepPlayerOnTop ? "Active" : null;
        DarkModeButton.Tag = _useDarkMode ? "Active" : null;
        SearchAllGroupsButton.Tag = _searchAllGroups ? "Active" : null;
        FavoritesOnlyButton.Tag = _favoritesOnly ? "Active" : null;
        RecentOnlyButton.Tag = _recentOnly ? "Active" : null;
    }

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBox or PasswordBox;
    }

    protected override void OnClosed(EventArgs e)
    {
        _searchDebounceTimer.Stop();
        _guideWarmupDebounceTimer.Stop();
        _logoWarmupCts?.Cancel();
        _epgLoadCts?.Cancel();
        CancelGuideWarmup();
        _settingsStore.Save(CollectSettingsFromUi());
        _playerWindow?.Close();
        base.OnClosed(e);
    }
}
