using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;

namespace Schmube;

public partial class ListingsWindow : Window
{
    private const string DefaultListingsUrl = "https://www.livesoccertv.com/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<PlaylistChannel> _availableChannels;
    private readonly ObservableCollection<CompatiblePlaylistChannel> _visibleChannels = [];
    private readonly ObservableCollection<BroadcastOfferMatch> _visibleOfferMatches = [];
    private readonly ObservableCollection<BroadcastOfferMatch> _visibleUnmatchedOffers = [];
    private readonly ChannelMatchingConfigStore _configStore = new();
    private CompatibleChannelListResult? _currentResult;
    private bool _browserInitialized;

    public ListingsWindow(IReadOnlyList<PlaylistChannel> availableChannels, string? initialUrl = null)
    {
        _availableChannels = availableChannels.ToList();

        InitializeComponent();

        GeneratedChannelsListView.ItemsSource = _visibleChannels;
        OfferMatchesListView.ItemsSource = _visibleOfferMatches;
        UnmatchedOffersListView.ItemsSource = _visibleUnmatchedOffers;
        ListingUrlTextBox.Text = string.IsNullOrWhiteSpace(initialUrl) ? DefaultListingsUrl : initialUrl.Trim();
        Loaded += ListingsWindow_Loaded;

        UpdateSummary();
        UpdateUseTempListState();
    }

    public TemporaryChannelListSelection? TemporarySelection { get; private set; }

    private async void ListingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ListingsWindow_Loaded;
        PositionWithinWorkArea();
        await InitializeBrowserAsync();
        NavigateBrowser();
    }

    private async Task InitializeBrowserAsync()
    {
        if (_browserInitialized)
        {
            return;
        }

        try
        {
            await ListingsBrowser.EnsureCoreWebView2Async();
            ListingsBrowser.NavigationStarting += ListingsBrowser_NavigationStarting;
            ListingsBrowser.NavigationCompleted += ListingsBrowser_NavigationCompleted;
            ListingsBrowser.CoreWebView2.SourceChanged += CoreWebView2_SourceChanged;
            ListingsBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            ListingsBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _browserInitialized = true;
            StatusTextBlock.Text = "Browser ready. Open a match page, then generate compatible channels.";
        }
        catch (Exception ex)
        {
            GenerateButton.IsEnabled = false;
            GoButton.IsEnabled = false;
            StatusTextBlock.Text = $"WebView2 initialization failed: {ex.Message}";
        }
    }

    private void ListingsBrowser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        ListingUrlTextBox.Text = e.Uri;
        StatusTextBlock.Text = "Loading listings page...";
    }

    private void ListingsBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (ListingsBrowser.CoreWebView2 is not null)
        {
            ListingUrlTextBox.Text = ListingsBrowser.CoreWebView2.Source;
        }

        StatusTextBlock.Text = e.IsSuccess
            ? "Page loaded. Click Generate Channels to build a compatible playlist list."
            : $"Page load failed: {e.WebErrorStatus}";
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (ListingsBrowser.CoreWebView2 is not null)
        {
            ListingUrlTextBox.Text = ListingsBrowser.CoreWebView2.Source;
        }
    }

    private void NavigateBrowser()
    {
        if (!_browserInitialized)
        {
            return;
        }

        if (!TryGetListingsUri(out var uri))
        {
            return;
        }

        ListingsBrowser.Source = uri;
        StatusTextBlock.Text = $"Opening {uri.Host}...";
    }

    private bool TryGetListingsUri(out Uri uri)
    {
        uri = null!;
        var value = ListingUrlTextBox.Text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsedUri))
        {
            StatusTextBlock.Text = "Provide a valid absolute listings URL.";
            return false;
        }

        uri = parsedUri;
        return true;
    }

    private async Task<MatchPageSnapshot?> CapturePageAsync()
    {
        if (!_browserInitialized || ListingsBrowser.CoreWebView2 is null)
        {
            StatusTextBlock.Text = "Browser is not ready yet.";
            return null;
        }

        const string captureScript = """
            (() => {
                return {
                    title: document.title || "",
                    url: location.href || "",
                    html: document.documentElement ? document.documentElement.outerHTML : "",
                    text: document.body ? document.body.innerText : "",
                    readyState: document.readyState || ""
                };
            })();
            """;

        var result = await ListingsBrowser.ExecuteScriptAsync(captureScript);
        return JsonSerializer.Deserialize<MatchPageSnapshot>(result, JsonOptions);
    }

    private void PositionWithinWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 40));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 40));
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
        Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
    }

    private void ApplyResultFilter()
    {
        var searchText = ResultSearchTextBox.Text.Trim();
        var source = _currentResult;

        _visibleChannels.Clear();
        _visibleOfferMatches.Clear();
        _visibleUnmatchedOffers.Clear();

        if (source is null)
        {
            UpdateSummary();
            UpdateUseTempListState();
            return;
        }

        foreach (var channel in source.GeneratedChannels.Where(channel => MatchesChannel(channel, searchText)))
        {
            _visibleChannels.Add(channel);
        }

        foreach (var offer in source.OfferMatches.Where(offer => MatchesOffer(offer, searchText)))
        {
            _visibleOfferMatches.Add(offer);
            if (offer.MatchCount == 0)
            {
                _visibleUnmatchedOffers.Add(offer);
            }
        }

        UpdateSummary();
        UpdateUseTempListState();
    }

    private static bool MatchesChannel(CompatiblePlaylistChannel channel, string searchText)
    {
        return string.IsNullOrWhiteSpace(searchText)
               || channel.ChannelName.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
               || channel.ChannelGroup.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
               || channel.OfferPreview.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
               || channel.MatchReason.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesOffer(BroadcastOfferMatch offer, string searchText)
    {
        return string.IsNullOrWhiteSpace(searchText)
               || offer.Country.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
               || offer.Broadcaster.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
               || offer.TopMatch.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
               || offer.MatchReason.Contains(searchText, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateSummary()
    {
        if (_currentResult is null)
        {
            ResultsSummaryTextBlock.Text = "Generate a compatible channel list from a rendered match page.";
            SourceSummaryTextBlock.Text = _availableChannels.Count == 0
                ? "No playlist loaded. Broadcaster extraction still works, but no compatible channels can be generated yet."
                : $"Playlist channels available: {_availableChannels.Count}.";
            DiagnosticsTextBox.Text = string.Empty;
            return;
        }

        ResultsSummaryTextBlock.Text =
            $"Compatible channels: {_visibleChannels.Count} shown of {_currentResult.GeneratedChannels.Count}. " +
            $"Broadcaster offers: {_visibleOfferMatches.Count} shown of {_currentResult.OfferMatches.Count}.";

        SourceSummaryTextBlock.Text =
            $"Source: {_currentResult.SourceName}. Label: {_currentResult.Label}. " +
            $"Unmatched offers: {_currentResult.UnmatchedOffers.Count}.";

        DiagnosticsTextBox.Text = string.Join(Environment.NewLine, _currentResult.Diagnostics);
    }

    private void UpdateUseTempListState()
    {
        var sourceChannels = GetActiveChannelSelection();
        UseTempListButton.IsEnabled = sourceChannels.Count > 0;
        UseTempListButton.Content = GeneratedChannelsListView.SelectedItems.Count > 0
            ? $"Use Selected ({sourceChannels.Count})"
            : $"Use Temp List ({sourceChannels.Count})";
    }

    private IReadOnlyList<CompatiblePlaylistChannel> GetActiveChannelSelection()
    {
        if (GeneratedChannelsListView.SelectedItems.Count > 0)
        {
            return GeneratedChannelsListView.SelectedItems.Cast<CompatiblePlaylistChannel>().ToList();
        }

        return _visibleChannels.ToList();
    }

    private async void GoButton_Click(object sender, RoutedEventArgs e)
    {
        await InitializeBrowserAsync();
        NavigateBrowser();
    }

    private void OpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetListingsUri(out var uri))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true
        });
    }

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateButton.IsEnabled = false;
        var originalText = GenerateButton.Content;
        GenerateButton.Content = "Working...";

        try
        {
            StatusTextBlock.Text = "Step 1/3: Capturing the rendered page...";
            var snapshot = await CapturePageAsync();
            if (snapshot is null)
            {
                StatusTextBlock.Text = "Could not capture the current page.";
                return;
            }

            snapshot.Url = string.IsNullOrWhiteSpace(snapshot.Url)
                ? ListingUrlTextBox.Text.Trim()
                : snapshot.Url.Trim();

            StatusTextBlock.Text = "Step 2/3: Extracting broadcaster offers...";
            var config = _configStore.Load();
            var service = new CompatibleChannelListService(_availableChannels, config);
            _currentResult = service.Generate(snapshot);

            StatusTextBlock.Text = "Step 3/3: Building the compatible playlist channel list...";
            ApplyResultFilter();

            if (_currentResult.GeneratedChannels.Count == 0)
            {
                StatusTextBlock.Text = _availableChannels.Count == 0
                    ? "Broadcasters were extracted, but no playlist is loaded yet."
                    : "Broadcasters were extracted, but none could be matched to the current playlist.";
            }
            else
            {
                StatusTextBlock.Text = $"Generated {_currentResult.GeneratedChannels.Count} compatible channels from {_currentResult.Label}.";
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Generation failed: {ex.Message}";
            MessageBox.Show(
                this,
                ex.Message,
                "Generate Channels Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            GenerateButton.Content = originalText;
            GenerateButton.IsEnabled = true;
        }
    }

    private void ResultSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyResultFilter();
    }

    private void GeneratedChannelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUseTempListState();
    }

    private void UseTempListButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedChannels = GetActiveChannelSelection();
        var streamKeys = selectedChannels
            .Select(channel => channel.StreamKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (streamKeys.Count == 0)
        {
            StatusTextBlock.Text = "No compatible channels are available for the current result set.";
            return;
        }

        var label = _currentResult is null || string.IsNullOrWhiteSpace(_currentResult.Label)
            ? "TV listings"
            : _currentResult.Label.Trim();

        TemporarySelection = new TemporaryChannelListSelection(label, streamKeys);
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
