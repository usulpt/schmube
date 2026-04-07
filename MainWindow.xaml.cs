using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Schmube;

public partial class MainWindow : Window
{
    private const string AllGroupsLabel = "All groups";
    private const string DefaultHintText = "Focus a control to see what it does.";
    private const int RecentChannelLimit = 12;
    private const int GuideWarmupLimit = 18;

    private readonly SettingsStore _settingsStore = new();
    private readonly AppConfigStore _appConfigStore = new();
    private readonly GroupFlagStore _groupFlagStore = new();
    private readonly PlaylistService _playlistService = new();
    private readonly LogoCacheService _logoCacheService = new();
    private readonly EpgService _epgService = new();
    private readonly List<PlaylistChannel> _allChannels = [];
    private readonly ObservableCollection<PlaylistChannel> _visibleChannels = [];
    private readonly ObservableCollection<string> _groupOptions = [AllGroupsLabel];
    private readonly ObservableCollection<ProgramGuideEntry> _programGuideEntries = [];
    private readonly HashSet<string> _favoriteChannelKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _recentChannelKeys = [];
    private readonly Dictionary<int, IReadOnlyList<ProgramGuideEntry>> _guideCache = [];

    private readonly SchmubeAppConfig _appConfig;
    private CancellationTokenSource? _logoWarmupCts;
    private CancellationTokenSource? _epgLoadCts;
    private CancellationTokenSource? _guideWarmupCts;
    private bool _autoLoadAttempted;
    private string _defaultGroup = string.Empty;
    private string _lastPlayedChannelKey = string.Empty;
    private string _pendingSelectionChannelKey = string.Empty;
    private XtreamConnectionInfo? _currentXtreamConnection;
    private PlayerWindow? _playerWindow;

    public MainWindow()
    {
        _appConfig = _appConfigStore.Load();

        InitializeComponent();

        ChannelsListView.ItemsSource = _visibleChannels;
        GroupFilterComboBox.ItemsSource = _groupOptions;
        GroupFilterComboBox.SelectedIndex = 0;
        ProgramGuideListBox.ItemsSource = _programGuideEntries;
        Loaded += MainWindow_Loaded;

        RegisterFocusHints();
        SetHintText(DefaultHintText);
        LoadSettings();
        UpdateChannelSummary();
        ClearProgramGuide("Select a channel to load the guide.");
        UpdateSelectedChannelDetails(null);
    }

    private PlaylistChannel? SelectedChannel => ChannelsListView.SelectedItem as PlaylistChannel;

    private void RegisterFocusHints()
    {
        RegisterFocusHint(StreamUrlTextBox, "Source URL for your playlist or direct stream. Paste it here or keep using the saved config value.");
        RegisterFocusHint(KeepOnTopCheckBox, "Keep the separate player window above your other apps while you watch.");
        RegisterFocusHint(ChannelSearchTextBox, "Filter the loaded channels by name, group, channel ID, or the live guide preview.");
        RegisterFocusHint(GroupFilterComboBox, "Limit the channel list to one group after the playlist finishes loading.");
        RegisterFocusHint(FavoritesOnlyCheckBox, "Show only channels you have marked as favorites.");
        RegisterFocusHint(RecentOnlyCheckBox, "Show only channels you played recently.");
        RegisterFocusHint(LoadChannelsButton, "Load or refresh the channel list from the configured source.");
        RegisterFocusHint(PlaySelectedButton, "Start playback for the channel currently selected in the list.");
        RegisterFocusHint(PlayUrlButton, "Play the raw URL directly when it points to a single stream rather than a playlist account.");
        RegisterFocusHint(OpenPlayerButton, "Open the separate resizable player window without changing the current stream.");
        RegisterFocusHint(StopPlaybackButton, "Stop playback in the player window.");
        RegisterFocusHint(SaveSettingsButton, "Persist the current URL, favorites, recents, and playback window setting locally.");
        RegisterFocusHint(ChannelsListView, "Browse channels here. Single-click selects a channel and double-click starts playback.");
        RegisterFocusHint(ToggleFavoriteButton, "Add or remove the selected channel from your favorites list.");
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

        StreamUrlTextBox.Text = !string.IsNullOrWhiteSpace(_appConfig.SubscriptionUrl)
            ? _appConfig.SubscriptionUrl
            : settings.StreamUrl;
        KeepOnTopCheckBox.IsChecked = settings.KeepPlayerOnTop;
        FavoritesOnlyCheckBox.IsChecked = false;
        RecentOnlyCheckBox.IsChecked = false;
        ConfiguredDefaultTextBox.Text = FormatDefaultGroup();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_autoLoadAttempted || !_appConfig.AutoLoadOnStartup || string.IsNullOrWhiteSpace(StreamUrlTextBox.Text))
        {
            return;
        }

        _autoLoadAttempted = true;
        LoadChannelsButton_Click(this, new RoutedEventArgs());
    }

    private StreamSettings CollectSettingsFromUi()
    {
        return new StreamSettings
        {
            StreamUrl = StreamUrlTextBox.Text.Trim(),
            KeepPlayerOnTop = KeepOnTopCheckBox.IsChecked == true,
            FavoriteChannelKeys = _favoriteChannelKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList(),
            RecentChannelKeys = _recentChannelKeys.ToList(),
            LastChannelKey = _lastPlayedChannelKey
        };
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

    private PlaybackRequest BuildPlaybackRequest(Uri streamUri, string displayName, bool allowReconnect)
    {
        var settings = CollectSettingsFromUi();
        return new PlaybackRequest(streamUri, settings.KeepPlayerOnTop, displayName, allowReconnect, ResolveRecordingsDirectory());
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

    private PlayerWindow EnsurePlayerWindow(bool focusWindow)
    {
        if (_playerWindow is null || !_playerWindow.IsLoaded)
        {
            _playerWindow = new PlayerWindow();
            _playerWindow.Closed += (_, _) => _playerWindow = null;
            _playerWindow.Show();
        }

        _playerWindow.SetAlwaysOnTop(KeepOnTopCheckBox.IsChecked == true);

        if (!_playerWindow.IsVisible)
        {
            _playerWindow.Show();
        }

        if (focusWindow)
        {
            _playerWindow.Activate();
        }

        return _playerWindow;
    }

    private async Task<bool> PlayRequestAsync(PlaybackRequest request, string successMessage)
    {
        try
        {
            var playerWindow = EnsurePlayerWindow(true);
            await playerWindow.PlayAsync(request);
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

        var request = BuildPlaybackRequest(selectedChannel.StreamUri, selectedChannel.Name, allowReconnect: true);
        if (await PlayRequestAsync(request, $"Playing {selectedChannel.Name}."))
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
                .OrderBy(channel => channel.GroupTitle, StringComparer.CurrentCultureIgnoreCase)
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
            var groupInfo = _groupFlagStore.Resolve(channel.GroupTitle);
            channel.GroupFlag = groupInfo.Flag;
            channel.GroupDisplayTitle = string.IsNullOrWhiteSpace(channel.GroupTitle)
                ? groupInfo.DisplayTitle
                : channel.GroupTitle;
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

    private void StartGuideWarmupForVisibleChannels()
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

    private void ApplyGuidePreview(PlaylistChannel channel, IReadOnlyList<ProgramGuideEntry> entries)
    {
        if (entries.Count == 0)
        {
            channel.NowTitle = string.Empty;
            channel.NextTitle = string.Empty;
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
    }

    private void ClearProgramGuide(string message)
    {
        _programGuideEntries.Clear();
        ProgramGuideStatusTextBox.Text = message;
    }

    private void RefreshGroupOptions()
    {
        var selectedGroup = GroupFilterComboBox.SelectedItem as string;

        _groupOptions.Clear();
        _groupOptions.Add(AllGroupsLabel);

        foreach (var group in _allChannels
                     .Select(channel => channel.GroupTitle.Trim())
                     .Where(group => !string.IsNullOrWhiteSpace(group))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase))
        {
            _groupOptions.Add(group);
        }

        var restoredSelection = _groupOptions.FirstOrDefault(group => string.Equals(group, selectedGroup, StringComparison.CurrentCultureIgnoreCase));
        GroupFilterComboBox.SelectedItem = restoredSelection ?? AllGroupsLabel;
    }

    private void SelectDefaultGroupIfAvailable()
    {
        if (string.IsNullOrWhiteSpace(_defaultGroup))
        {
            GroupFilterComboBox.SelectedItem = AllGroupsLabel;
            return;
        }

        var matchedGroup = _groupOptions.FirstOrDefault(group => string.Equals(group, _defaultGroup, StringComparison.CurrentCultureIgnoreCase));
        GroupFilterComboBox.SelectedItem = matchedGroup ?? AllGroupsLabel;
    }

    private void ApplyChannelFilter(bool selectFirstChannel)
    {
        var selectedStream = SelectedChannel?.StreamUri.ToString();
        var preferredChannelKey = selectFirstChannel ? _pendingSelectionChannelKey : string.Empty;
        var searchText = ChannelSearchTextBox.Text.Trim();
        var selectedGroup = GroupFilterComboBox.SelectedItem as string;
        var favoritesOnly = FavoritesOnlyCheckBox.IsChecked == true;
        var recentOnly = RecentOnlyCheckBox.IsChecked == true;

        var filteredChannels = _allChannels
            .Where(channel => MatchesSearch(channel, searchText))
            .Where(channel => MatchesGroup(channel, selectedGroup))
            .Where(channel => !favoritesOnly || channel.IsFavorite)
            .Where(channel => !recentOnly || channel.RecentRank >= 0)
            .OrderByDescending(channel => channel.IsFavorite)
            .ThenBy(channel => recentOnly ? channel.RecentRank : int.MaxValue)
            .ThenBy(channel => channel.GroupTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _visibleChannels.Clear();
        foreach (var channel in filteredChannels)
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
        StartGuideWarmupForVisibleChannels();
    }

    private static bool MatchesSearch(PlaylistChannel channel, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return channel.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.GroupTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.GroupDisplayTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.TvgId.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.NowTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.NextTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesGroup(PlaylistChannel channel, string? selectedGroup)
    {
        return string.IsNullOrWhiteSpace(selectedGroup)
            || string.Equals(selectedGroup, AllGroupsLabel, StringComparison.Ordinal)
            || string.Equals(channel.GroupTitle, selectedGroup, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateChannelSummary()
    {
        if (_allChannels.Count == 0)
        {
            ChannelSummaryTextBox.Text = "No channels loaded.";
            return;
        }

        var summary = $"Showing {_visibleChannels.Count} of {_allChannels.Count} channels.";

        if (FavoritesOnlyCheckBox.IsChecked == true)
        {
            summary += " Favorites only.";
        }

        if (RecentOnlyCheckBox.IsChecked == true)
        {
            summary += " Recent only.";
        }

        if (!string.IsNullOrWhiteSpace(GroupFilterComboBox.SelectedItem as string) && !string.Equals(GroupFilterComboBox.SelectedItem as string, AllGroupsLabel, StringComparison.Ordinal))
        {
            summary += $" Group: {GroupFilterComboBox.SelectedItem}.";
        }

        summary += _recentChannelKeys.Count > 0 ? $" Recents tracked: {_recentChannelKeys.Count}." : string.Empty;
        ChannelSummaryTextBox.Text = summary;
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
            ToggleFavoriteButton.IsEnabled = false;
            ToggleFavoriteButton.Content = "Add Favorite";
            return;
        }

        SelectedChannelNameTextBox.Text = channel.Name;
        SelectedChannelFavoriteStateTextBox.Text = BuildChannelStateText(channel);
        SelectedChannelGroupTextBox.Text = string.IsNullOrWhiteSpace(channel.GroupTitle)
            ? "Ungrouped"
            : channel.GroupTitle;
        SelectedChannelIdTextBox.Text = string.IsNullOrWhiteSpace(channel.TvgId)
            ? channel.StreamId?.ToString() ?? "Not available"
            : channel.TvgId;
        SelectedChannelUrlTextBox.Text = channel.StreamUri.ToString();
        SelectedChannelLogoImage.Source = CreateLogoSource(channel.LogoSource);
        ToggleFavoriteButton.IsEnabled = true;
        ToggleFavoriteButton.Content = channel.IsFavorite ? "Remove Favorite" : "Add Favorite";
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

    private static ImageSource? CreateLogoSource(string logoSource)
    {
        if (!Uri.TryCreate(logoSource, UriKind.Absolute, out var uri))
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

    private string FormatDefaultGroup()
    {
        return string.IsNullOrWhiteSpace(_defaultGroup) ? "All groups" : _defaultGroup;
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
        _guideWarmupCts?.Cancel();
        _guideWarmupCts?.Dispose();
        _guideWarmupCts = null;
    }

    private void ToggleFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        if (selectedChannel is null)
        {
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
        UpdateSelectedChannelDetails(selectedChannel);
        UpdateChannelSummary();
    }

    private void OpenPlayerButton_Click(object sender, RoutedEventArgs e)
    {
        EnsurePlayerWindow(true);
        StatusTextBlock.Text = "Player window opened.";
    }

    private async void PlaySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await PlaySelectedChannelAsync();
    }

    private async void PlayUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildDirectRequest(out var request) || request is null)
        {
            return;
        }

        await PlayRequestAsync(request, $"Playing {request.DisplayName}.");
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _playerWindow?.Stop();
        StatusTextBlock.Text = "Playback stopped.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsStore.Save(CollectSettingsFromUi());
        StatusTextBlock.Text = "Settings saved.";
    }

    private async void ChannelsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await PlaySelectedChannelAsync();
    }

    private async void ChannelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedChannel = SelectedChannel;
        UpdateSelectedChannelDetails(selectedChannel);
        await EnsureSelectedLogoCachedAsync(selectedChannel);
        await LoadProgramGuideAsync(selectedChannel);
    }

    private void ChannelSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void GroupFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void FavoritesOnlyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ApplyChannelFilter(selectFirstChannel: false);
    }

    private void RecentOnlyCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ApplyChannelFilter(selectFirstChannel: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _logoWarmupCts?.Cancel();
        _epgLoadCts?.Cancel();
        CancelGuideWarmup();
        _settingsStore.Save(CollectSettingsFromUi());
        _playerWindow?.Close();
        base.OnClosed(e);
    }
}








