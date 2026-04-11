using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Schmube;

public partial class PlayerWindow : Window
{
    private struct NetworkDebitSampler
    {
        private long _lastBytes;
        private long _lastTimestamp;
        private bool _hasSample;

        public double MegabitsPerSecond { get; private set; }

        public void Clear()
        {
            _lastBytes = 0;
            _lastTimestamp = 0;
            _hasSample = false;
            MegabitsPerSecond = 0;
        }

        public void Reset(long byteCount, long timestamp)
        {
            _lastBytes = byteCount;
            _lastTimestamp = timestamp;
            _hasSample = true;
            MegabitsPerSecond = 0;
        }

        public double Sample(long byteCount, long timestamp)
        {
            if (!_hasSample || byteCount < _lastBytes)
            {
                Reset(byteCount, timestamp);
                return MegabitsPerSecond;
            }

            var elapsedSeconds = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp).TotalSeconds;
            var bytesRead = byteCount - _lastBytes;
            if (elapsedSeconds > 0 && bytesRead >= 0)
            {
                MegabitsPerSecond = bytesRead * BitsPerByte / elapsedSeconds / BitsPerMegabit;
            }

            _lastBytes = byteCount;
            _lastTimestamp = timestamp;
            return MegabitsPerSecond;
        }
    }

    private const int MaxReconnectAttempts = 3;
    private const int DefaultVolume = 100;
    private const int VolumeStep = 10;
    private const double BitsPerByte = 8;
    private const double BitsPerMegabit = 1_000_000;
    private const string ScreenshotsFolderName = "Schmube\\Screenshots";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _networkDebitStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private Media? _currentMedia;
    private PlaybackRequest? _currentRequest;
    private CancellationTokenSource? _reconnectCts;
    private bool _manualStopRequested;
    private int _reconnectAttempt;
    private bool _isRecording;
    private string _recordingFilePath = string.Empty;
    private bool _keepPlayerOnTopRequested;
    private bool _isFullScreen;
    private WindowState _restoreWindowState = WindowState.Normal;
    private WindowStyle _restoreWindowStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _restoreResizeMode = ResizeMode.CanResize;
    private double _networkDebitMegabitsPerSecond;
    private NetworkDebitSampler _libVlcNetworkDebitSampler;
    private NetworkDebitSampler _processIoNetworkDebitSampler;
    private NetworkDebitSampler _networkInterfaceDebitSampler;
    private bool _isBuffering;
    private bool _showNetworkDebit;
    private float _lastBufferingCache;

    public event EventHandler<int>? ChannelStepRequested;

    public PlayerWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _libVlc = new LibVLC("--network-caching=1500");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = DefaultVolume;
        VideoSurface.MediaPlayer = _mediaPlayer;

        _networkDebitStatusTimer.Tick += (_, _) => RefreshBufferingStatus();

        _mediaPlayer.Playing += (_, _) => SetPlaybackStatus("Playing.");
        _mediaPlayer.Buffering += (_, e) => SetBufferingStatus(e.Cache);
        _mediaPlayer.Stopped += (_, _) =>
        {
            if (_manualStopRequested)
            {
                SetPlaybackStatus("Stopped.");
            }
        };
        _mediaPlayer.EndReached += (_, _) => HandlePlaybackInterruption("Stream ended.");
        _mediaPlayer.EncounteredError += (_, _) => HandlePlaybackInterruption("Playback error.");

        PreviewKeyDown += PlayerWindow_PreviewKeyDown;

        UpdateRecordingVisualState();
        UpdateAudioVisualState();
        UpdateNetworkDebitVisualState();
        UpdateScreenshotButtonState();
    }

    public Task PlayAsync(PlaybackRequest request)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            CancelReconnect();
            _manualStopRequested = false;
            _reconnectAttempt = 0;
            _currentRequest = request;
            ResetRecordingState();

            SetAlwaysOnTop(request.KeepPlayerOnTop);
            NowPlayingText.Text = request.DisplayName;
            Title = $"Schmube Player - {request.DisplayName}";

            StartPlaybackCore(request, isReconnect: false);
            UpdateRecordingVisualState();
            UpdateAudioVisualState();
            UpdateScreenshotButtonState();
        }).Task;
    }

    public void Stop()
    {
        CancelReconnect();
        _manualStopRequested = true;
        _currentRequest = null;
        ResetRecordingState();
        ResetNetworkDebit();
        StopBufferingStatusUpdates();

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }
        else
        {
            SetStatus("Stopped.");
        }

        UpdateScreenshotButtonState();
    }

    public void SetAlwaysOnTop(bool isOnTop)
    {
        _keepPlayerOnTopRequested = isOnTop;
        ApplyTopmostState();
    }

    private void ApplyTopmostState()
    {
        Topmost = _keepPlayerOnTopRequested || _isFullScreen;
    }

    private void StartPlaybackCore(PlaybackRequest request, bool isReconnect)
    {
        _currentMedia?.Dispose();
        ResetNetworkDebit();
        StopBufferingStatusUpdates();
        _currentMedia = CreateMedia(request);

        SetStatus(isReconnect
            ? $"Reconnecting to {request.DisplayName}..."
            : $"Connecting to {request.DisplayName}...");

        var started = _mediaPlayer.Play(_currentMedia);
        if (!started)
        {
            throw new InvalidOperationException("LibVLC failed to start playback.");
        }
    }

    private Media CreateMedia(PlaybackRequest request)
    {
        var media = new Media(_libVlc, request.StreamUri);
        if (_isRecording && !string.IsNullOrWhiteSpace(_recordingFilePath))
        {
            media.AddOption(BuildRecordingSoutOption(_recordingFilePath));
            media.AddOption(":sout-keep");
        }

        return media;
    }

    private static string BuildRecordingSoutOption(string recordingFilePath)
    {
        var normalizedPath = recordingFilePath.Replace('\\', '/').Replace("'", "\\'");
        return $":sout=#duplicate{{dst=display,dst=std{{access=file,mux=ts,dst='{normalizedPath}'}}}}";
    }

    private void HandlePlaybackInterruption(string reason)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => HandlePlaybackInterruption(reason));
            return;
        }

        if (_manualStopRequested)
        {
            SetStatus(reason);
            return;
        }

        if (_currentRequest is null)
        {
            SetStatus(reason);
            return;
        }

        if (!_currentRequest.AllowReconnect)
        {
            SetStatus(reason);
            return;
        }

        if (_reconnectAttempt >= MaxReconnectAttempts)
        {
            SetStatus($"{reason} Auto-reconnect gave up after {MaxReconnectAttempts} attempts.");
            return;
        }

        ScheduleReconnect(reason, _currentRequest);
    }

    private void ScheduleReconnect(string reason, PlaybackRequest request)
    {
        CancelReconnect();

        _reconnectAttempt++;
        var attempt = _reconnectAttempt;
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var secondsRemaining = (int)ReconnectDelay.TotalSeconds; secondsRemaining >= 1; secondsRemaining--)
                {
                    await Dispatcher.InvokeAsync(() =>
                        SetStatus($"{reason} Reconnecting in {secondsRemaining}s (attempt {attempt}/{MaxReconnectAttempts})..."));
                    await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested || _manualStopRequested || !ReferenceEquals(_reconnectCts, cts))
                    {
                        return;
                    }

                    try
                    {
                        StartPlaybackCore(request, isReconnect: true);
                    }
                    catch (Exception ex)
                    {
                        HandlePlaybackInterruption($"Reconnect failed: {ex.Message}");
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        }, cts.Token);
    }

    private void CancelReconnect()
    {
        if (_reconnectCts is null)
        {
            return;
        }

        _reconnectCts.Cancel();
        _reconnectCts.Dispose();
        _reconnectCts = null;
    }

    private async void ToggleRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRequest is null)
        {
            SetStatus("Play a channel before recording.");
            return;
        }

        try
        {
            await Dispatcher.InvokeAsync(() => ToggleRecordingCore(_currentRequest));
        }
        catch (Exception ex)
        {
            SetStatus($"Recording change failed: {ex.Message}");
        }
    }

    private void ToggleRecordingCore(PlaybackRequest request)
    {
        CancelReconnect();
        _manualStopRequested = false;
        _reconnectAttempt = 0;

        if (_isRecording)
        {
            var completedFile = _recordingFilePath;
            _isRecording = false;
            _recordingFilePath = string.Empty;
            UpdateRecordingVisualState();
            StartPlaybackCore(request, isReconnect: false);
            SetStatus($"Recording stopped. Saved to {completedFile}");
            return;
        }

        var recordingsDirectory = ResolveRecordingsDirectory(request.RecordingsDirectory);
        Directory.CreateDirectory(recordingsDirectory);

        _recordingFilePath = Path.Combine(recordingsDirectory, BuildRecordingFileName(request.DisplayName));
        _isRecording = true;
        UpdateRecordingVisualState();
        StartPlaybackCore(request, isReconnect: false);
        SetStatus($"Recording to {_recordingFilePath}");
    }

    private void OpenRecordingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = ResolveRecordingsDirectory(_currentRequest?.RecordingsDirectory ?? string.Empty);
            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open recordings folder: {ex.Message}");
        }
    }

    private static string ResolveRecordingsDirectory(string configuredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return configuredDirectory.Trim();
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Schmube");
    }

    private static string BuildRecordingFileName(string displayName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(displayName
            .Select(ch => invalidCharacters.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "channel";
        }

        return $"{DateTime.Now:yyyyMMdd_HHmmss}_{sanitized}.ts";
    }

    private void ResetRecordingState()
    {
        _isRecording = false;
        _recordingFilePath = string.Empty;
        UpdateRecordingVisualState();
    }

    private void UpdateScreenshotButtonState()
    {
        TakeScreenshotButton.IsEnabled = _currentRequest is not null;
    }

    private void UpdateRecordingVisualState()
    {
        ToggleRecordingButton.IsEnabled = _currentRequest is not null;
        OpenRecordingsButton.IsEnabled = true;
        ToggleRecordingButton.Content = _isRecording ? "Stop Rec" : "Start Rec";
        RecordingText.Visibility = _isRecording ? Visibility.Visible : Visibility.Collapsed;
        RecordingText.Text = _isRecording
            ? $"REC {Path.GetFileName(_recordingFilePath)}"
            : string.Empty;
    }

    private void UpdateAudioVisualState()
    {
        VolumeText.Text = BuildAudioStatusText();
        ToggleMuteButton.Content = _mediaPlayer.Mute ? "Unmute" : "Mute";
    }

    private void UpdateNetworkDebitVisualState()
    {
        ToggleNetworkDebitButton.Content = _showNetworkDebit ? "Net On" : "Net Off";
    }

    private string BuildAudioStatusText()
    {
        var volume = Math.Max(_mediaPlayer.Volume, 0);
        return _mediaPlayer.Mute ? $"Muted ({volume}%)" : $"Volume {volume}%";
    }

    private void SetPlaybackStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetPlaybackStatus(status));
            return;
        }

        StopBufferingStatusUpdates();
        SetStatus(status);
    }

    private void SetBufferingStatus(float cache)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetBufferingStatus(cache));
            return;
        }

        _lastBufferingCache = cache;
        _isBuffering = true;
        if (_showNetworkDebit && !_networkDebitStatusTimer.IsEnabled)
        {
            _networkDebitStatusTimer.Start();
        }

        SetStatus(BuildBufferingStatusText(cache));
    }

    private void RefreshBufferingStatus()
    {
        if (!_showNetworkDebit || !_isBuffering || _currentRequest is null)
        {
            StopBufferingStatusUpdates();
            return;
        }

        SetStatus(BuildBufferingStatusText(_lastBufferingCache));
    }

    private void StopBufferingStatusUpdates()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(StopBufferingStatusUpdates);
            return;
        }

        _isBuffering = false;
        _networkDebitStatusTimer.Stop();
    }

    private string BuildBufferingStatusText(float cache)
    {
        if (!_showNetworkDebit)
        {
            return string.Create(CultureInfo.CurrentCulture, $"Buffering {cache:0}%");
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"Buffering {cache:0}% | Network {GetNetworkDebitMegabitsPerSecond():0.0} Mb/s");
    }

    private double GetNetworkDebitMegabitsPerSecond()
    {
        if (_currentRequest is null)
        {
            return 0;
        }

        var timestamp = Stopwatch.GetTimestamp();
        var libVlcDebit = 0.0;
        var processIoDebit = 0.0;
        var networkInterfaceDebit = 0.0;
        if (TryGetLibVlcByteCount(out var libVlcByteCount))
        {
            libVlcDebit = _libVlcNetworkDebitSampler.Sample(libVlcByteCount, timestamp);
        }

        if (TryGetProcessIoReadByteCount(out var processIoByteCount))
        {
            processIoDebit = _processIoNetworkDebitSampler.Sample(processIoByteCount, timestamp);
        }

        if (TryGetNetworkInterfaceReadByteCount(out var networkInterfaceByteCount))
        {
            networkInterfaceDebit = _networkInterfaceDebitSampler.Sample(networkInterfaceByteCount, timestamp);
        }

        _networkDebitMegabitsPerSecond = libVlcDebit > 0 ? libVlcDebit
            : processIoDebit > 0 ? processIoDebit
            : networkInterfaceDebit > 0 ? networkInterfaceDebit
            : _networkDebitMegabitsPerSecond;

        return _networkDebitMegabitsPerSecond;
    }

    private void ResetNetworkDebit()
    {
        _networkDebitMegabitsPerSecond = 0;
        if (!_showNetworkDebit)
        {
            _libVlcNetworkDebitSampler.Clear();
            _processIoNetworkDebitSampler.Clear();
            _networkInterfaceDebitSampler.Clear();
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();

        _libVlcNetworkDebitSampler.Clear();

        if (TryGetProcessIoReadByteCount(out var processIoByteCount))
        {
            _processIoNetworkDebitSampler.Reset(processIoByteCount, timestamp);
        }
        else
        {
            _processIoNetworkDebitSampler.Clear();
        }

        if (TryGetNetworkInterfaceReadByteCount(out var networkInterfaceByteCount))
        {
            _networkInterfaceDebitSampler.Reset(networkInterfaceByteCount, timestamp);
        }
        else
        {
            _networkInterfaceDebitSampler.Clear();
        }
    }

    private bool TryGetLibVlcByteCount(out long byteCount)
    {
        byteCount = 0;
        var media = _mediaPlayer.Media ?? _currentMedia;
        if (media is null)
        {
            return false;
        }

        var statistics = media.Statistics;
        byteCount = Math.Max(statistics.ReadBytes, statistics.DemuxReadBytes);
        return byteCount > 0;
    }

    private static bool TryGetProcessIoReadByteCount(out long byteCount)
    {
        byteCount = 0;
        using var process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out var counters))
        {
            return false;
        }

        byteCount = counters.ReadTransferCount > long.MaxValue
            ? long.MaxValue
            : (long)counters.ReadTransferCount;
        return true;
    }

    private static bool TryGetNetworkInterfaceReadByteCount(out long byteCount)
    {
        byteCount = 0;
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var statistics = networkInterface.GetIPv4Statistics();
                byteCount = Math.Min(long.MaxValue, byteCount + statistics.BytesReceived);
            }
        }
        catch (NetworkInformationException)
        {
            byteCount = 0;
            return false;
        }

        return byteCount > 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    private void AdjustVolume(int delta)
    {
        var newVolume = Math.Clamp(_mediaPlayer.Volume + delta, 0, 200);
        _mediaPlayer.Volume = newVolume;
        if (newVolume > 0 && _mediaPlayer.Mute)
        {
            _mediaPlayer.Mute = false;
        }

        UpdateAudioVisualState();
        SetStatus(BuildAudioStatusText());
    }

    private void ToggleMute()
    {
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        UpdateAudioVisualState();
        SetStatus(BuildAudioStatusText());
    }

    private void ToggleNetworkDebit()
    {
        _showNetworkDebit = !_showNetworkDebit;
        UpdateNetworkDebitVisualState();
        ResetNetworkDebit();

        if (_showNetworkDebit)
        {
            if (_isBuffering && !_networkDebitStatusTimer.IsEnabled)
            {
                _networkDebitStatusTimer.Start();
            }

            SetStatus(_isBuffering
                ? BuildBufferingStatusText(_lastBufferingCache)
                : "Network debit display enabled.");
            return;
        }

        _networkDebitStatusTimer.Stop();
        SetStatus(_isBuffering
            ? BuildBufferingStatusText(_lastBufferingCache)
            : "Network debit display disabled.");
    }

    private void ToggleFullScreen()
    {
        if (_isFullScreen)
        {
            ExitFullScreen();
            return;
        }

        _restoreWindowState = WindowState;
        _restoreWindowStyle = WindowStyle;
        _restoreResizeMode = ResizeMode;
        _isFullScreen = true;
        PlayerToolbar.Visibility = Visibility.Collapsed;
        PlayerStatusBar.Visibility = Visibility.Collapsed;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        FullScreenButton.Content = "Window";
        ApplyTopmostState();
        SetStatus("Full screen enabled.");
    }

    private void ExitFullScreen()
    {
        if (!_isFullScreen)
        {
            return;
        }

        _isFullScreen = false;
        WindowState = WindowState.Normal;
        WindowStyle = _restoreWindowStyle;
        ResizeMode = _restoreResizeMode;
        WindowState = _restoreWindowState;
        PlayerToolbar.Visibility = Visibility.Visible;
        PlayerStatusBar.Visibility = Visibility.Visible;
        FullScreenButton.Content = "Full";
        ApplyTopmostState();
        SetStatus("Full screen disabled.");
    }

    private void RequestChannelStep(int delta)
    {
        ChannelStepRequested?.Invoke(this, delta);
    }

    private void PreviousChannelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestChannelStep(-1);
    }

    private void NextChannelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestChannelStep(1);
    }

    private void VolumeDownButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustVolume(-VolumeStep);
    }

    private void VolumeUpButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustVolume(VolumeStep);
    }

    private void ToggleMuteButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMute();
    }

    private void ToggleNetworkDebitButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleNetworkDebit();
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleFullScreen();
    }

    private void TakeScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        TakeScreenshot();
    }

    private void VideoSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }

        ToggleFullScreen();
        e.Handled = true;
    }

    private void PlayerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _isFullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.M)
        {
            ToggleMute();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            TakeScreenshot();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            AdjustVolume(VolumeStep);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            AdjustVolume(-VolumeStep);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.PageUp || (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control))
        {
            RequestChannelStep(-1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.PageDown || (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control))
        {
            RequestChannelStep(1);
            e.Handled = true;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        Stop();
    }

    private void TakeScreenshot()
    {
        if (_currentRequest is null || !_mediaPlayer.IsPlaying)
        {
            SetStatus("Start playback before taking a screenshot.");
            return;
        }

        try
        {
            var screenshotsDirectory = ResolveScreenshotsDirectory();
            Directory.CreateDirectory(screenshotsDirectory);

            var screenshotPath = Path.Combine(screenshotsDirectory, BuildScreenshotFileName(_currentRequest.DisplayName));
            var success = _mediaPlayer.TakeSnapshot(0, screenshotPath, 0, 0);
            SetStatus(success
                ? $"Screenshot saved to {screenshotPath}"
                : "Screenshot failed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Screenshot failed: {ex.Message}");
        }
    }

    private void SetStatus(string status)
    {
        if (Dispatcher.CheckAccess())
        {
            StatusText.Text = status;
            return;
        }

        Dispatcher.BeginInvoke(() => StatusText.Text = status);
    }

    private static string ResolveScreenshotsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            ScreenshotsFolderName);
    }

    private static string BuildScreenshotFileName(string displayName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(displayName
            .Select(ch => invalidCharacters.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "channel";
        }

        return $"{DateTime.Now:yyyyMMdd_HHmmss}_{sanitized}.png";
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelReconnect();
        _networkDebitStatusTimer.Stop();
        PreviewKeyDown -= PlayerWindow_PreviewKeyDown;
        VideoSurface.MediaPlayer = null;
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        base.OnClosed(e);
    }
}
