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

    private readonly SettingsStore _settingsStore = new();
    private readonly AppConfigStore _appConfigStore = new();
    private readonly PlaylistService _playlistService = new();
    private readonly LogoCacheService _logoCacheService = new();
    private readonly EpgService _epgService = new();
    private readonly List<PlaylistChannel> _allChannels = [];
    private readonly ObservableCollection<PlaylistChannel> _visibleChannels = [];
    private readonly ObservableCollection<string> _groupOptions = [AllGroupsLabel];
    private readonly ObservableCollection<ProgramGuideEntry> _programGuideEntries = [];
    private readonly HashSet<string> _favoriteChannelKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _defaultGroups = new(StringComparer.CurrentCultureIgnoreCase);

    private readonly SchmubeAppConfig _appConfig;
    private CancellationTokenSource? _logoWarmupCts;
    private CancellationTokenSource? _epgLoadCts;
    private bool _autoLoadAttempted;
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

        LoadSettings();
        UpdateChannelSummary();
        ClearProgramGuide("Select a channel to load the guide.");
        UpdateSelectedChannelDetails(null);
    }

    private PlaylistChannel? SelectedChannel => ChannelsListView.SelectedItem as PlaylistChannel;

    private void LoadSettings()
    {
        var settings = _settingsStore.Load();

        _favoriteChannelKeys.Clear();
        foreach (var key in settings.FavoriteChannelKeys.Where(key => !string.IsNullOrWhiteSpace(key)))
        {
            _favoriteChannelKeys.Add(key);
        }

        _defaultGroups.Clear();
        foreach (var group in _appConfig.DefaultGroups.Where(group => !string.IsNullOrWhiteSpace(group)))
        {
            _defaultGroups.Add(group.Trim());
        }

        StreamUrlTextBox.Text = !string.IsNullOrWhiteSpace(_appConfig.SubscriptionUrl)
            ? _appConfig.SubscriptionUrl
            : settings.StreamUrl;
        UserAgentTextBox.Text = settings.UserAgent;
        RefererTextBox.Text = settings.Referer;
        KeepOnTopCheckBox.IsChecked = settings.KeepPlayerOnTop;
        FavoritesOnlyCheckBox.IsChecked = false;
        ApplyDefaultGroupsCheckBox.IsEnabled = _defaultGroups.Count > 0;
        ApplyDefaultGroupsCheckBox.IsChecked = _defaultGroups.Count > 0 && _appConfig.ApplyDefaultGroupsOnLoad;
        ConfiguredDefaultsTextBlock.Text = FormatDefaultGroups();
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
            UserAgent = UserAgentTextBox.Text.Trim(),
            Referer = RefererTextBox.Text.Trim(),
            KeepPlayerOnTop = KeepOnTopCheckBox.IsChecked == true,
            FavoriteChannelKeys = _favoriteChannelKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList()
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

    private PlaybackRequest BuildPlaybackRequest(Uri streamUri, string displayName)
    {
        var settings = CollectSettingsFromUi();
        return new PlaybackRequest(streamUri, settings.UserAgent, settings.Referer, settings.KeepPlayerOnTop, displayName);
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
        request = BuildPlaybackRequest(streamUri, BuildDisplayName(streamUri));
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

    private async Task PlayRequestAsync(PlaybackRequest request, string successMessage)
    {
        try
        {
            var playerWindow = EnsurePlayerWindow(true);
            await playerWindow.PlayAsync(request);
            StatusTextBlock.Text = successMessage;
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Playback error: {ex.Message}";
        }
    }

    private async Task PlaySelectedChannelAsync()
    {
        if (SelectedChannel is null)
        {
            StatusTextBlock.Text = "Select a channel from the list first.";
            return;
        }

        _settingsStore.Save(CollectSettingsFromUi());
        var request = BuildPlaybackRequest(SelectedChannel.StreamUri, SelectedChannel.Name);
        await PlayRequestAsync(request, $"Playing {SelectedChannel.Name}.");
    }

    private async void LoadChannelsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetConfiguredUri(out var playlistUri) || playlistUri is null)
        {
            return;
        }

        var settings = CollectSettingsFromUi();
        _settingsStore.Save(settings);
        SetLoadingState(true);
        StatusTextBlock.Text = PlaylistService.IsXtreamPlaylistUri(playlistUri)
            ? "Loading channels via Xtream API..."
            : "Loading playlist...";

        try
        {
            var channels = (await _playlistService.LoadChannelsAsync(playlistUri, settings.UserAgent, settings.Referer)).ToList();
            ApplyFavoriteStates(channels);
            _logoCacheService.RefreshResolvedLogoSources(channels);

            _allChannels.Clear();
            _allChannels.AddRange(channels
                .OrderBy(channel => channel.GroupTitle, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase));

            RefreshGroupOptions();
            ApplyChannelFilter(selectFirstChannel: true);
            StartLogoWarmup(channels);
            StatusTextBlock.Text = _allChannels.Count == 0
                ? "Channel source loaded, but no playable channels were found."
                : $"Loaded {_allChannels.Count} channels.";
        }
        catch (Exception ex)
        {
            _allChannels.Clear();
            _visibleChannels.Clear();
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
                    ChannelsListView.Items.Refresh();
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
            ChannelsListView.Items.Refresh();
            if (ReferenceEquals(SelectedChannel, channel))
            {
                UpdateSelectedChannelDetails(channel);
            }
        }
        catch
        {
        }
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

        if (channel.StreamId is null || !TryGetConfiguredUri(out var sourceUri) || sourceUri is null || !PlaylistService.TryGetXtreamConnection(sourceUri, out var connection) || connection is null)
        {
            ClearProgramGuide("EPG unavailable for this source.");
            return;
        }

        var settings = CollectSettingsFromUi();
        var cts = new CancellationTokenSource();
        _epgLoadCts = cts;
        ProgramGuideStatusTextBlock.Text = "Loading program guide...";

        try
        {
            var entries = await _epgService.LoadShortGuideAsync(connection, channel.StreamId.Value, settings.UserAgent, settings.Referer, cts.Token);
            if (!ReferenceEquals(_epgLoadCts, cts))
            {
                return;
            }

            _programGuideEntries.Clear();
            foreach (var entry in entries)
            {
                _programGuideEntries.Add(entry);
            }

            ProgramGuideStatusTextBlock.Text = entries.Count == 0
                ? "No guide data available."
                : $"{entries.Count} upcoming programs.";
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

    private void ClearProgramGuide(string message)
    {
        _programGuideEntries.Clear();
        ProgramGuideStatusTextBlock.Text = message;
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

    private void ApplyChannelFilter(bool selectFirstChannel)
    {
        var selectedStream = SelectedChannel?.StreamUri.ToString();
        var searchText = ChannelSearchTextBox.Text.Trim();
        var selectedGroup = GroupFilterComboBox.SelectedItem as string;
        var favoritesOnly = FavoritesOnlyCheckBox.IsChecked == true;
        var applyDefaultGroups = ApplyDefaultGroupsCheckBox.IsChecked == true && _defaultGroups.Count > 0;

        var filteredChannels = _allChannels
            .Where(channel => MatchesSearch(channel, searchText))
            .Where(channel => MatchesGroup(channel, selectedGroup))
            .Where(channel => !favoritesOnly || channel.IsFavorite)
            .Where(channel => !applyDefaultGroups || _defaultGroups.Contains(channel.GroupTitle))
            .OrderByDescending(channel => channel.IsFavorite)
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
    }

    private static bool MatchesSearch(PlaylistChannel channel, string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return channel.Name.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.GroupTitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
            || channel.TvgId.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
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
            ChannelSummaryTextBlock.Text = "No channels loaded.";
            return;
        }

        var summary = $"Showing {_visibleChannels.Count} of {_allChannels.Count} channels.";

        if (FavoritesOnlyCheckBox.IsChecked == true)
        {
            summary += " Favorites only.";
        }

        if (ApplyDefaultGroupsCheckBox.IsChecked == true && _defaultGroups.Count > 0)
        {
            summary += " Default groups active.";
        }

        ChannelSummaryTextBlock.Text = summary;
    }

    private void UpdateSelectedChannelDetails(PlaylistChannel? channel)
    {
        if (channel is null)
        {
            SelectedChannelNameTextBlock.Text = "No channel selected";
            SelectedChannelFavoriteStateTextBlock.Text = "Not a favorite";
            SelectedChannelGroupTextBlock.Text = "Load a playlist to browse channels.";
            SelectedChannelIdTextBlock.Text = "Not available";
            SelectedChannelUrlTextBlock.Text = "Load the playlist and choose a channel from the list.";
            SelectedChannelLogoImage.Source = null;
            ToggleFavoriteButton.IsEnabled = false;
            ToggleFavoriteButton.Content = "Add Favorite";
            return;
        }

        SelectedChannelNameTextBlock.Text = channel.Name;
        SelectedChannelFavoriteStateTextBlock.Text = channel.IsFavorite ? "Favorite" : "Not a favorite";
        SelectedChannelGroupTextBlock.Text = string.IsNullOrWhiteSpace(channel.GroupTitle)
            ? "Ungrouped"
            : channel.GroupTitle;
        SelectedChannelIdTextBlock.Text = string.IsNullOrWhiteSpace(channel.TvgId)
            ? "Not available"
            : channel.TvgId;
        SelectedChannelUrlTextBlock.Text = channel.StreamUri.ToString();
        SelectedChannelLogoImage.Source = CreateLogoSource(channel.LogoSource);
        ToggleFavoriteButton.IsEnabled = true;
        ToggleFavoriteButton.Content = channel.IsFavorite ? "Remove Favorite" : "Add Favorite";
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

    private string FormatDefaultGroups()
    {
        if (_defaultGroups.Count == 0)
        {
            return "All groups";
        }

        var groups = _defaultGroups.OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase).ToList();
        return groups.Count <= 3
            ? string.Join(", ", groups)
            : $"{string.Join(", ", groups.Take(3))} +{groups.Count - 3} more";
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadChannelsButton.IsEnabled = !isLoading;
    }

    private static string BuildDisplayName(Uri streamUri)
    {
        var lastSegment = streamUri.Segments.Length > 0 ? streamUri.Segments[^1].Trim('/') : string.Empty;
        return string.IsNullOrWhiteSpace(lastSegment) ? streamUri.Host : lastSegment;
    }

    private void ToggleFavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedChannel is null)
        {
            return;
        }

        if (SelectedChannel.IsFavorite)
        {
            _favoriteChannelKeys.Remove(SelectedChannel.FavoriteKey);
            SelectedChannel.IsFavorite = false;
            StatusTextBlock.Text = $"Removed {SelectedChannel.Name} from favorites.";
        }
        else
        {
            _favoriteChannelKeys.Add(SelectedChannel.FavoriteKey);
            SelectedChannel.IsFavorite = true;
            StatusTextBlock.Text = $"Added {SelectedChannel.Name} to favorites.";
        }

        _settingsStore.Save(CollectSettingsFromUi());
        ApplyChannelFilter(selectFirstChannel: false);
        if (SelectedChannel is not null)
        {
            UpdateSelectedChannelDetails(SelectedChannel);
        }
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

    private void ApplyDefaultGroupsCheckBox_Click(object sender, RoutedEventArgs e)
    {
        ApplyChannelFilter(selectFirstChannel: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        _logoWarmupCts?.Cancel();
        _epgLoadCts?.Cancel();
        _settingsStore.Save(CollectSettingsFromUi());
        _playerWindow?.Close();
        base.OnClosed(e);
    }
}
