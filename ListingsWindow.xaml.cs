using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Schmube;

public partial class ListingsWindow : Window
{
    private const string DefaultListingsUrl = "https://www.livesoccertv.com/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly HttpClient ImportHttpClient = BuildImportHttpClient();

    private readonly IReadOnlyList<PlaylistChannel> _availableChannels;
    private readonly ObservableCollection<ListingChannelMatch> _visibleMatches = [];
    private readonly List<ListingChannelMatch> _allMatches = [];
    private readonly string _diagnosticsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Schmube",
        "listings-import.log");
    private bool _browserInitialized;
    private string _lastImportedTitle = string.Empty;

    public ListingsWindow(IReadOnlyList<PlaylistChannel> availableChannels, string? initialUrl = null)
    {
        _availableChannels = availableChannels.ToList();

        InitializeComponent();

        ImportedMatchesListView.ItemsSource = _visibleMatches;
        ListingUrlTextBox.Text = string.IsNullOrWhiteSpace(initialUrl) ? DefaultListingsUrl : initialUrl.Trim();
        Loaded += ListingsWindow_Loaded;

        UseTempListButton.IsEnabled = false;
        if (_availableChannels.Count == 0)
        {
            ImportedSummaryTextBlock.Text = "No playlist is loaded yet. You can still browse and import page rows, but temporary list creation will stay empty.";
        }
    }

    public TemporaryChannelListSelection? TemporarySelection { get; private set; }

    private async void ListingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= ListingsWindow_Loaded;
        PositionWithinWorkArea();
        AppendDiagnostic($"Window loaded. Size={Width:0}x{Height:0}");
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
            StatusTextBlock.Text = "Browser ready. Paste a match or listings URL and click Go.";
            AppendDiagnostic("WebView2 initialized.");
        }
        catch (Exception ex)
        {
            ImportButton.IsEnabled = false;
            GoButton.IsEnabled = false;
            StatusTextBlock.Text = $"WebView2 initialization failed: {ex.Message}";
            AppendDiagnostic($"WebView2 init failed: {ex}");
        }
    }

    private void ListingsBrowser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        ListingUrlTextBox.Text = e.Uri;
        StatusTextBlock.Text = "Loading listings page...";
        AppendDiagnostic($"Navigation starting: {e.Uri}");
    }

    private void ListingsBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (ListingsBrowser.CoreWebView2 is not null)
        {
            ListingUrlTextBox.Text = ListingsBrowser.CoreWebView2.Source;
        }

        StatusTextBlock.Text = e.IsSuccess
            ? "Page loaded. Click Import Page to capture broadcaster rows from the visible page."
            : $"Page load failed: {e.WebErrorStatus}";
        AppendDiagnostic(e.IsSuccess
            ? $"Navigation completed: {ListingUrlTextBox.Text}"
            : $"Navigation failed: {e.WebErrorStatus} ({ListingUrlTextBox.Text})");
    }

    private void CoreWebView2_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (ListingsBrowser.CoreWebView2 is null)
        {
            return;
        }

        ListingUrlTextBox.Text = ListingsBrowser.CoreWebView2.Source;
        AppendDiagnostic($"Source changed: {ListingUrlTextBox.Text}");
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
        AppendDiagnostic($"Navigate requested: {uri}");
    }

    private static HttpClient BuildImportHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
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

    private async Task<PageCapturePayload?> CapturePageAsync()
    {
        if (!_browserInitialized || ListingsBrowser.CoreWebView2 is null)
        {
            StatusTextBlock.Text = "Browser is not ready yet.";
            return null;
        }

        const string captureScript = """
            (() => {
                const clean = value => (value || "").replace(/\s+/g, " ").trim();
                const rows = [];
                const seen = new Set();
                const addRow = (left, right) => {
                    const country = clean(left);
                    const broadcasters = clean(right);
                    if (!country || !broadcasters) {
                        return;
                    }

                    const key = `${country}|||${broadcasters}`;
                    if (!seen.has(key)) {
                        seen.add(key);
                        rows.push(`${country} | ${broadcasters}`);
                    }
                };

                const ignoredOwnText = new Set([
                    "Watch",
                    "Live",
                    "Lineups"
                ]);

                const collectStructuredRows = root => {
                    if (!root) {
                        return;
                    }

                    const candidates = [root, ...root.querySelectorAll("div, p, li, tr, td")];
                    for (const candidate of candidates) {
                        const links = Array.from(candidate.querySelectorAll("a"))
                            .map(link => clean(link.innerText))
                            .filter(text => text && !/^watch$/i.test(text));

                        if (links.length === 0) {
                            continue;
                        }

                        const ownText = clean(Array.from(candidate.childNodes)
                            .filter(node => node.nodeType === Node.TEXT_NODE)
                            .map(node => node.textContent || "")
                            .join(" "));

                        if (!ownText || ignoredOwnText.has(ownText) || ownText.length > 80) {
                            continue;
                        }

                        addRow(ownText, links.join(" | "));
                    }
                };

                const headingMatchers = [
                    /international coverage/i,
                    /^live broadcasts$/i
                ];

                for (const heading of document.querySelectorAll("h1, h2, h3, h4, h5, h6")) {
                    const headingText = clean(heading.innerText);
                    if (!headingMatchers.some(pattern => pattern.test(headingText))) {
                        continue;
                    }

                    let sibling = heading.nextElementSibling;
                    while (sibling && !/^H[1-6]$/i.test(sibling.tagName)) {
                        collectStructuredRows(sibling);
                        sibling = sibling.nextElementSibling;
                    }
                }

                for (const row of document.querySelectorAll("tr")) {
                    const cells = Array.from(row.querySelectorAll("th, td"))
                        .map(cell => clean(cell.innerText))
                        .filter(Boolean);
                    if (cells.length >= 2) {
                        addRow(cells[0], cells.slice(1).join(" | "));
                    }
                }

                for (const item of document.querySelectorAll("li")) {
                    const text = clean(item.innerText);
                    if (text.includes("|")) {
                        const parts = text.split("|");
                        if (parts.length >= 2) {
                            addRow(parts[0], parts.slice(1).join("|"));
                        }
                    }
                }

                return {
                    title: document.title || "",
                    url: location.href || "",
                    html: document.documentElement ? document.documentElement.outerHTML : "",
                    text: document.body ? document.body.innerText : "",
                    readyState: document.readyState || "",
                    rows
                };
            })();
            """;

        var result = await ListingsBrowser.ExecuteScriptAsync(captureScript);
        return JsonSerializer.Deserialize<PageCapturePayload>(result, JsonOptions);
    }

    private async Task<string> FetchPageHtmlAsync(Uri uri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://www.livesoccertv.com/");
        using var response = await ImportHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
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

    private void AppendDiagnostic(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DiagnosticsTextBox.AppendText(line + Environment.NewLine);
        DiagnosticsTextBox.ScrollToEnd();

        var directory = Path.GetDirectoryName(_diagnosticsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(_diagnosticsPath, line + Environment.NewLine);
        }
    }

    private void ApplyImportedFilter()
    {
        var searchText = ImportedSearchTextBox.Text.Trim();
        var filteredMatches = _allMatches
            .Where(match => string.IsNullOrWhiteSpace(searchText)
                || match.Country.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || match.BroadcasterText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase)
                || match.MatchPreview.Contains(searchText, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        _visibleMatches.Clear();
        foreach (var match in filteredMatches)
        {
            _visibleMatches.Add(match);
        }

        UpdateImportedSummary();
        UpdateUseTempListState();
    }

    private void UpdateImportedSummary()
    {
        if (_allMatches.Count == 0)
        {
            ImportedSummaryTextBlock.Text = "Import a page to inspect broadcaster rows and playlist matches.";
            return;
        }

        var matchedChannelCount = _visibleMatches
            .SelectMany(match => match.MatchedChannels)
            .DistinctBy(channel => channel.StreamUri.ToString())
            .Count();

        ImportedSummaryTextBlock.Text = $"Showing {_visibleMatches.Count} of {_allMatches.Count} imported rows. Matched playlist channels in view: {matchedChannelCount}.";
    }

    private void UpdateUseTempListState()
    {
        var sourceMatches = GetActiveSourceMatches();
        var matchedChannels = sourceMatches
            .SelectMany(match => match.MatchedChannels)
            .DistinctBy(channel => channel.StreamUri.ToString())
            .Count();

        UseTempListButton.IsEnabled = matchedChannels > 0;
        UseTempListButton.Content = ImportedMatchesListView.SelectedItems.Count > 0
            ? $"Use Selected ({matchedChannels})"
            : $"Use Temp List ({matchedChannels})";
    }

    private IReadOnlyList<ListingChannelMatch> GetActiveSourceMatches()
    {
        if (ImportedMatchesListView.SelectedItems.Count > 0)
        {
            return ImportedMatchesListView.SelectedItems.Cast<ListingChannelMatch>().ToList();
        }

        return _visibleMatches.ToList();
    }

    private string BuildSelectionLabel()
    {
        var title = string.IsNullOrWhiteSpace(_lastImportedTitle)
            ? "TV listings"
            : _lastImportedTitle.Trim();

        return title.Length > 80 ? title[..80].TrimEnd() : title;
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

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = false;
        var originalButtonText = ImportButton.Content;
        ImportButton.Content = "Loading...";

        try
        {
            StatusTextBlock.Text = "Step 1/3: Reading the listings page...";
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            AppendDiagnostic($"Import requested on {ListingUrlTextBox.Text}");
            var capture = await CapturePageAsync();
            if (capture is null || (string.IsNullOrWhiteSpace(capture.Text) && (capture.Rows?.Count ?? 0) == 0))
            {
                AppendDiagnostic($"Initial capture empty. ReadyState={capture?.ReadyState ?? "<null>"}, html length={capture?.Html?.Length ?? 0}");
            }

            capture ??= new PageCapturePayload
            {
                Url = ListingUrlTextBox.Text.Trim()
            };

            var effectiveUrl = !string.IsNullOrWhiteSpace(capture.Url)
                ? capture.Url.Trim()
                : !string.IsNullOrWhiteSpace(ListingUrlTextBox.Text)
                    ? ListingUrlTextBox.Text.Trim()
                    : ListingsBrowser.CoreWebView2?.Source?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(capture.Url))
            {
                capture.Url = effectiveUrl;
            }

            AppendDiagnostic($"Effective import URL: {effectiveUrl}");

            var fetchedHtml = string.Empty;
            if (Uri.TryCreate(effectiveUrl, UriKind.Absolute, out var captureUri))
            {
                try
                {
                    StatusTextBlock.Text = "Step 1/3: Reading the listings page source...";
                    fetchedHtml = await FetchPageHtmlAsync(captureUri);
                    AppendDiagnostic($"Fetched HTML directly from URL. Length={fetchedHtml.Length}");
                }
                catch (Exception ex)
                {
                    AppendDiagnostic($"Direct HTML fetch failed: {ex.Message}");
                }
            }

            var importService = new ListingsImportService(_availableChannels);
            var capturedRows = capture.Rows ?? [];
            var htmlForImport = !string.IsNullOrWhiteSpace(fetchedHtml) ? fetchedHtml : capture.Html;
            StatusTextBlock.Text = "Step 2/3: Extracting listed channels from International Coverage...";
            var matches = importService.BuildMatches(capturedRows, capture.Text, htmlForImport);
            AppendDiagnostic($"Capture title: {capture.Title}");
            AppendDiagnostic($"Capture url: {capture.Url}");
            AppendDiagnostic($"ReadyState: {capture.ReadyState}");
            AppendDiagnostic($"Structured rows: {capturedRows.Count}, body text length: {capture.Text.Length}, html length: {htmlForImport.Length}, matched rows: {matches.Count}");
            foreach (var row in capturedRows.Take(8))
            {
                AppendDiagnostic($"Row: {row}");
            }

            _lastImportedTitle = capture.Title?.Trim() ?? string.Empty;
            _allMatches.Clear();
            _allMatches.AddRange(matches);
            StatusTextBlock.Text = $"Step 3/3: Matching {matches.Count} listed channels against your playlist...";
            ApplyImportedFilter();

            if (_allMatches.Count == 0)
            {
                StatusTextBlock.Text = "Finished loading listed channels, but none could be matched to your playlist.";
                AppendDiagnostic("Import completed with zero parsed broadcaster rows.");
                MessageBox.Show(
                    this,
                    $"No broadcaster rows were detected on:\n{capture.Url}\n\nIf the page has a collapsed coverage section, expand it first and then run Import Page again.",
                    "Import Page",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            ImportedMatchesListView.SelectedIndex = 0;
            ImportedMatchesListView.ScrollIntoView(ImportedMatchesListView.SelectedItem);
            StatusTextBlock.Text = $"Finished loading {_allMatches.Count} listed channels from {BuildSelectionLabel()}.";
            AppendDiagnostic($"Import completed successfully. Visible matches: {_visibleMatches.Count}");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Import failed: {ex.Message}";
            AppendDiagnostic($"Import failed: {ex}");
            MessageBox.Show(
                this,
                ex.Message,
                "Import Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ImportButton.Content = originalButtonText;
            ImportButton.IsEnabled = true;
        }
    }

    private void ImportedSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyImportedFilter();
    }

    private void ImportedMatchesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUseTempListState();
    }

    private void UseTempListButton_Click(object sender, RoutedEventArgs e)
    {
        var streamKeys = GetActiveSourceMatches()
            .SelectMany(match => match.MatchedChannels)
            .DistinctBy(channel => channel.StreamUri.ToString())
            .Select(channel => channel.StreamUri.ToString())
            .ToList();

        if (streamKeys.Count == 0)
        {
            StatusTextBlock.Text = "The current import has no playlist matches to turn into a temporary list.";
            return;
        }

        TemporarySelection = new TemporaryChannelListSelection(BuildSelectionLabel(), streamKeys);
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class PageCapturePayload
    {
        public string Title { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string Html { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public string ReadyState { get; set; } = string.Empty;

        public List<string> Rows { get; set; } = [];
    }
}
