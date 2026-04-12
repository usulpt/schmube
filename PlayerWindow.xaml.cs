using System;
using System.Collections.ObjectModel;
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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using ImageSource = System.Windows.Media.ImageSource;

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
    private const float BufferingCompleteCacheThreshold = 99.5f;
    private const string ScreenshotsFolderName = "Schmube\\Screenshots";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private readonly DispatcherTimer _networkDebitStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _recordingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _recordingScheduleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly ObservableCollection<RecordingScheduleEntry> _recordingSchedules = [];
    private readonly ObservableCollection<RecordingHistoryEntry> _recordingHistory = [];

    private Media? _currentMedia;
    private PlaybackRequest? _currentRequest;
    private RecordingScheduleEntry? _activeScheduledRecording;
    private CancellationTokenSource? _reconnectCts;
    private bool _manualStopRequested;
    private int _reconnectAttempt;
    private bool _isRecording;
    private string _recordingFilePath = string.Empty;
    private string _activeRecordingDisplayName = string.Empty;
    private string _activeRecordingProgramTitle = string.Empty;
    private DateTime _recordingStartedAt;
    private bool _isScheduledRecordingActive;
    private bool _scheduleSwitchWarningShown;
    private int _recordingDefaultDurationMinutes = 60;
    private string _recordingFileNameFormat = "{timestamp}_{channel}";
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
    public event EventHandler? PlaybackStopped;
    public event EventHandler? RecordingStateChanged;

    public PlayerWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _libVlc = new LibVLC("--network-caching=1500");
        _mediaPlayer = new MediaPlayer(_libVlc);
        _mediaPlayer.Volume = DefaultVolume;
        VideoSurface.MediaPlayer = _mediaPlayer;
        RecordingSchedulesListBox.ItemsSource = _recordingSchedules;
        RecordingHistoryListBox.ItemsSource = _recordingHistory;

        _networkDebitStatusTimer.Tick += (_, _) => RefreshBufferingStatus();
        _recordingTimer.Tick += (_, _) => UpdateRecordingTimerText();
        _recordingScheduleTimer.Tick += (_, _) => UpdateScheduledRecording();

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

        InitializeRecordingScheduleInputs();
        UpdateRecordingVisualState();
        UpdateRecordingScheduleVisualState();
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
            if (_isRecording && _currentRequest is not null)
            {
                StopRecordingCore(_currentRequest, _isScheduledRecordingActive ? "Cancelled" : "Stopped");
            }

            if (_activeScheduledRecording is not null)
            {
                _recordingSchedules.Remove(_activeScheduledRecording);
                _activeScheduledRecording = null;
                _isScheduledRecordingActive = false;
                NotifyRecordingStateChanged();
            }

            _currentRequest = request;
            ResetRecordingState();

            SetAlwaysOnTop(request.KeepPlayerOnTop);
            NowPlayingText.Text = request.DisplayName;
            UpdateNowPlayingLogo(request.LogoSource, request.FallbackLogoSource);
            Title = $"Schmube Player - {request.DisplayName}";

            StartPlaybackCore(request, isReconnect: false);
            UpdateRecordingVisualState();
            UpdateRecordingScheduleVisualState();
            UpdateAudioVisualState();
            UpdateScreenshotButtonState();
        }).Task;
    }

    public void Stop()
    {
        CancelReconnect();
        _manualStopRequested = true;
        if (_isRecording && _currentRequest is not null)
        {
            StopRecordingCore(_currentRequest, _isScheduledRecordingActive ? "Cancelled" : "Stopped");
        }

        _currentRequest = null;
        UpdateNowPlayingLogo(string.Empty, string.Empty);
        ResetRecordingState();
        UpdateRecordingScheduleVisualState();
        ResetNetworkDebit();
        StopBufferingStatusUpdates();

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }
        else
        {
            SetPlaybackBadge("Stopped");
            SetStatus("Stopped.");
        }

        UpdateScreenshotButtonState();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
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
        SetPlaybackBadge(isReconnect ? "Reconnecting" : "Connecting");

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

    private void UpdateNowPlayingLogo(string logoSource, string fallbackLogoSource)
    {
        var source = CreateLogoSource(ResolveLogoFallback(logoSource, fallbackLogoSource));
        NowPlayingLogoImage.Source = source;
        NowPlayingLogoImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static ImageSource? CreateLogoSource(string logoSource)
    {
        if (string.IsNullOrWhiteSpace(logoSource))
        {
            return null;
        }

        Uri? uri;
        if (File.Exists(logoSource))
        {
            uri = new Uri(logoSource, UriKind.Absolute);
        }
        else if (!Uri.TryCreate(logoSource, UriKind.RelativeOrAbsolute, out uri))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            if (uri.IsFile)
            {
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            }

            image.EndInit();
            return image;
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
            SetPlaybackBadge(reason);
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_reconnectAttempt >= MaxReconnectAttempts)
        {
            SetStatus($"{reason} Auto-reconnect gave up after {MaxReconnectAttempts} attempts.");
            SetPlaybackBadge("Reconnect failed");
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
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
            var completedFile = StopRecordingCore(request, _isScheduledRecordingActive ? "Cancelled" : "Stopped");
            if (_activeScheduledRecording is not null)
            {
                _recordingSchedules.Remove(_activeScheduledRecording);
                _activeScheduledRecording = null;
                _isScheduledRecordingActive = false;
                NotifyRecordingStateChanged();
            }

            SetStatus($"Recording stopped. Saved to {completedFile}");
            return;
        }

        BeginRecordingCore(request);
        SetStatus($"Recording to {_recordingFilePath}");
    }

    private void BeginRecordingCore(PlaybackRequest request, RecordingScheduleEntry? schedule = null)
    {
        var recordingsDirectory = ResolveRecordingsDirectory(request.RecordingsDirectory);
        Directory.CreateDirectory(recordingsDirectory);

        _recordingFilePath = Path.Combine(recordingsDirectory, BuildRecordingFileName(request.DisplayName, schedule?.ProgramTitle ?? string.Empty));
        _activeRecordingDisplayName = request.DisplayName;
        _activeRecordingProgramTitle = schedule?.ProgramTitle ?? string.Empty;
        _isRecording = true;
        _recordingStartedAt = DateTime.Now;
        UpdateRecordingVisualState();
        try
        {
            StartPlaybackCore(request, isReconnect: false);
        }
        catch
        {
            _isRecording = false;
            _recordingFilePath = string.Empty;
            _activeRecordingDisplayName = string.Empty;
            _activeRecordingProgramTitle = string.Empty;
            _recordingStartedAt = default;
            UpdateRecordingVisualState();
            throw;
        }
    }

    private string StopRecordingCore(PlaybackRequest request, string status)
    {
        var completedFile = _recordingFilePath;
        var startedAt = _recordingStartedAt == default ? DateTime.Now : _recordingStartedAt;
        var displayName = string.IsNullOrWhiteSpace(_activeRecordingDisplayName) ? request.DisplayName : _activeRecordingDisplayName;
        var programTitle = _activeRecordingProgramTitle;
        _isRecording = false;
        _recordingFilePath = string.Empty;
        _activeRecordingDisplayName = string.Empty;
        _activeRecordingProgramTitle = string.Empty;
        _recordingStartedAt = default;
        UpdateRecordingVisualState();
        StartPlaybackCore(request, isReconnect: false);
        AddRecordingHistory(displayName, programTitle, completedFile, startedAt, DateTime.Now, status);
        return completedFile;
    }

    private void ScheduleRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentRequest is null)
        {
            SetStatus("Play a channel before scheduling a recording.");
            return;
        }

        try
        {
            if (!TryReadRecordingSchedule(out var startAt, out var duration, out var errorMessage))
            {
                SetStatus(errorMessage);
                return;
            }

            if (_isRecording)
            {
                SetStatus("Stop the current recording before scheduling another one.");
                return;
            }

            ScheduleRecording(_currentRequest, startAt, startAt.Add(duration), string.Empty);
        }
        catch (Exception ex)
        {
            SetStatus($"Recording schedule failed: {ex.Message}");
        }
    }

    private void CancelRecordingScheduleButton_Click(object sender, RoutedEventArgs e)
    {
        var schedule = RecordingSchedulesListBox.SelectedItem as RecordingScheduleEntry
            ?? _activeScheduledRecording
            ?? GetNextRecordingSchedule();
        if (schedule is null)
        {
            SetStatus("No recording is scheduled.");
            return;
        }

        if (ReferenceEquals(schedule, _activeScheduledRecording) && _isScheduledRecordingActive && _isRecording && _currentRequest is not null)
        {
            var completedFile = StopRecordingCore(_currentRequest, "Cancelled");
            _recordingSchedules.Remove(schedule);
            _activeScheduledRecording = null;
            _isScheduledRecordingActive = false;
            NotifyRecordingStateChanged();
            UpdateRecordingScheduleVisualState();
            SetStatus($"Scheduled recording cancelled. Saved to {completedFile}");
            return;
        }

        _recordingSchedules.Remove(schedule);
        EnsureRecordingScheduleTimer();
        NotifyRecordingStateChanged();
        UpdateRecordingScheduleVisualState();
        SetStatus("Scheduled recording cancelled.");
    }

    private bool TryReadRecordingSchedule(out DateTime startAt, out TimeSpan duration, out string errorMessage)
    {
        startAt = default;
        duration = default;
        errorMessage = string.Empty;

        if (RecordingScheduleDatePicker.SelectedDate is not { } selectedDate)
        {
            errorMessage = "Choose a recording date.";
            return false;
        }

        if (!TimeSpan.TryParse(RecordingScheduleTimeTextBox.Text.Trim(), CultureInfo.CurrentCulture, out var startTime))
        {
            errorMessage = "Enter the recording start time, for example 21:30.";
            return false;
        }

        if (startTime < TimeSpan.Zero || startTime >= TimeSpan.FromDays(1))
        {
            errorMessage = "Enter a start time between 00:00 and 23:59.";
            return false;
        }

        if (!int.TryParse(RecordingScheduleDurationTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var durationMinutes) ||
            durationMinutes <= 0)
        {
            errorMessage = "Enter a recording duration greater than 0 minutes.";
            return false;
        }

        startAt = selectedDate.Date.Add(startTime);
        duration = TimeSpan.FromMinutes(durationMinutes);
        if (startAt <= DateTime.Now)
        {
            errorMessage = "Choose a future recording start time.";
            return false;
        }

        return true;
    }

    private void InitializeRecordingScheduleInputs()
    {
        var defaultStart = DateTime.Now.AddMinutes(5);
        RecordingScheduleDatePicker.SelectedDate = defaultStart.Date;
        RecordingScheduleTimeTextBox.Text = defaultStart.ToString("HH:mm", CultureInfo.CurrentCulture);
        RecordingScheduleDurationTextBox.Text = _recordingDefaultDurationMinutes.ToString(CultureInfo.CurrentCulture);
    }

    public void ConfigureRecordingDefaults(int defaultDurationMinutes, string fileNameFormat)
    {
        _recordingDefaultDurationMinutes = Math.Clamp(defaultDurationMinutes, 1, 1440);
        _recordingFileNameFormat = string.IsNullOrWhiteSpace(fileNameFormat)
            ? "{timestamp}_{channel}"
            : fileNameFormat.Trim();
        if (!_isRecording && string.IsNullOrWhiteSpace(RecordingScheduleDurationTextBox.Text))
        {
            RecordingScheduleDurationTextBox.Text = _recordingDefaultDurationMinutes.ToString(CultureInfo.CurrentCulture);
        }
    }

    public void LoadRecordingState(IEnumerable<RecordingScheduleEntry> schedules, IEnumerable<RecordingHistoryEntry> history)
    {
        _recordingSchedules.Clear();
        foreach (var schedule in schedules
                     .Where(IsUsableSchedule)
                     .OrderBy(schedule => schedule.StartLocal)
                     .ThenBy(schedule => schedule.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            _recordingSchedules.Add(schedule);
        }

        _recordingHistory.Clear();
        foreach (var entry in history
                     .OrderByDescending(entry => entry.StartedLocal)
                     .Take(50))
        {
            _recordingHistory.Add(entry);
        }

        EnsureRecordingScheduleTimer();
        UpdateRecordingScheduleVisualState();
    }

    public IReadOnlyList<RecordingScheduleEntry> GetRecordingSchedulesSnapshot()
    {
        return _recordingSchedules
            .OrderBy(schedule => schedule.StartLocal)
            .Select(CloneSchedule)
            .ToList();
    }

    public IReadOnlyList<RecordingHistoryEntry> GetRecordingHistorySnapshot()
    {
        return _recordingHistory
            .OrderByDescending(entry => entry.StartedLocal)
            .Select(CloneHistory)
            .ToList();
    }

    public void ScheduleRecording(PlaybackRequest request, DateTime startAt, DateTime endAt, string programTitle)
    {
        if (endAt <= startAt)
        {
            throw new InvalidOperationException("Recording end time must be after the start time.");
        }

        if (endAt <= DateTime.Now)
        {
            throw new InvalidOperationException("Choose a future recording end time.");
        }

        var schedule = new RecordingScheduleEntry
        {
            StreamUri = request.StreamUri.ToString(),
            DisplayName = request.DisplayName,
            KeepPlayerOnTop = request.KeepPlayerOnTop,
            AllowReconnect = request.AllowReconnect,
            RecordingsDirectory = request.RecordingsDirectory,
            LogoSource = request.LogoSource,
            FallbackLogoSource = request.FallbackLogoSource,
            ProgramTitle = programTitle.Trim(),
            StartLocal = startAt,
            EndLocal = endAt,
            CreatedLocal = DateTime.Now
        };

        var conflict = _recordingSchedules.FirstOrDefault(existing =>
            existing.StartLocal < schedule.EndLocal && schedule.StartLocal < existing.EndLocal);
        if (conflict is not null)
        {
            SetStatus($"Recording scheduled, but it overlaps {conflict.DisplayName} at {conflict.StartLocal:g}. The earlier due recording wins if both are still queued.");
        }
        else
        {
            SetStatus($"Recording scheduled for {schedule.DisplayName} at {schedule.StartLocal:g}.");
        }

        _recordingSchedules.Add(schedule);
        SortRecordingSchedules();
        RecordingSchedulesListBox.SelectedItem = schedule;
        EnsureRecordingScheduleTimer();
        NotifyRecordingStateChanged();
        UpdateRecordingScheduleVisualState();
    }

    private void UpdateScheduledRecording()
    {
        if (_recordingSchedules.Count == 0)
        {
            _recordingScheduleTimer.Stop();
            return;
        }

        var now = DateTime.Now;
        if (!_isScheduledRecordingActive)
        {
            RemoveExpiredSchedules(now);
            var dueSchedule = _recordingSchedules
                .OrderBy(schedule => schedule.StartLocal)
                .FirstOrDefault(schedule => schedule.StartLocal <= now && now < schedule.EndLocal);
            if (dueSchedule is not null)
            {
                StartScheduledRecording(dueSchedule);
                return;
            }

            var nextSchedule = GetNextRecordingSchedule();
            if (nextSchedule is not null &&
                !_scheduleSwitchWarningShown &&
                nextSchedule.StartLocal > now &&
                nextSchedule.StartLocal - now <= TimeSpan.FromMinutes(1))
            {
                _scheduleSwitchWarningShown = true;
                var switchText = _currentRequest is not null &&
                                 !string.Equals(_currentRequest.StreamUri.ToString(), nextSchedule.StreamUri, StringComparison.OrdinalIgnoreCase)
                    ? $" It will switch from {_currentRequest.DisplayName}."
                    : string.Empty;
                SetStatus($"Recording {nextSchedule.DisplayName} starts in under 1 minute.{switchText}");
            }

            UpdateRecordingScheduleVisualState();
            return;
        }

        if (_activeScheduledRecording is not null && now >= _activeScheduledRecording.EndLocal)
        {
            FinishScheduledRecording();
            return;
        }

        UpdateRecordingScheduleVisualState();
    }

    private void StartScheduledRecording(RecordingScheduleEntry schedule)
    {
        if (_isRecording)
        {
            _recordingSchedules.Remove(schedule);
            NotifyRecordingStateChanged();
            UpdateRecordingScheduleVisualState();
            SetStatus("Scheduled recording skipped because another recording is already running.");
            return;
        }

        try
        {
            var request = schedule.ToPlaybackRequest();
            CancelReconnect();
            _isScheduledRecordingActive = true;
            _activeScheduledRecording = schedule;
            _scheduleSwitchWarningShown = false;
            _currentRequest = request;
            _manualStopRequested = false;
            _reconnectAttempt = 0;

            SetAlwaysOnTop(request.KeepPlayerOnTop);
            NowPlayingText.Text = request.DisplayName;
            UpdateNowPlayingLogo(request.LogoSource, request.FallbackLogoSource);
            Title = $"Schmube Player - {request.DisplayName}";

            BeginRecordingCore(request, schedule);
            UpdateRecordingScheduleVisualState();
            UpdateScreenshotButtonState();
            SetStatus($"Scheduled recording started for {request.DisplayName}. Saving to {_recordingFilePath}");
        }
        catch (Exception ex)
        {
            _isRecording = false;
            _recordingFilePath = string.Empty;
            _recordingStartedAt = default;
            _activeScheduledRecording = null;
            _isScheduledRecordingActive = false;
            _recordingSchedules.Remove(schedule);
            NotifyRecordingStateChanged();
            UpdateRecordingVisualState();
            UpdateRecordingScheduleVisualState();
            SetStatus($"Scheduled recording failed: {ex.Message}");
        }
    }

    private void FinishScheduledRecording()
    {
        if (_currentRequest is null || !_isRecording)
        {
            if (_activeScheduledRecording is not null)
            {
                _recordingSchedules.Remove(_activeScheduledRecording);
            }

            _activeScheduledRecording = null;
            _isScheduledRecordingActive = false;
            EnsureRecordingScheduleTimer();
            NotifyRecordingStateChanged();
            UpdateRecordingScheduleVisualState();
            SetStatus("Scheduled recording ended.");
            return;
        }

        try
        {
            var completedFile = StopRecordingCore(_currentRequest, "Finished");
            if (_activeScheduledRecording is not null)
            {
                _recordingSchedules.Remove(_activeScheduledRecording);
            }

            _activeScheduledRecording = null;
            _isScheduledRecordingActive = false;
            EnsureRecordingScheduleTimer();
            NotifyRecordingStateChanged();
            UpdateRecordingScheduleVisualState();
            SetStatus($"Scheduled recording finished. Saved to {completedFile}");
        }
        catch (Exception ex)
        {
            if (_activeScheduledRecording is not null)
            {
                _recordingSchedules.Remove(_activeScheduledRecording);
            }

            _activeScheduledRecording = null;
            _isScheduledRecordingActive = false;
            EnsureRecordingScheduleTimer();
            NotifyRecordingStateChanged();
            UpdateRecordingScheduleVisualState();
            SetStatus($"Scheduled recording finished, but playback restore failed: {ex.Message}");
        }
    }

    private void RemoveExpiredSchedules(DateTime now)
    {
        var expiredSchedules = _recordingSchedules
            .Where(schedule => schedule.EndLocal <= now)
            .ToList();
        if (expiredSchedules.Count == 0)
        {
            return;
        }

        foreach (var schedule in expiredSchedules)
        {
            _recordingSchedules.Remove(schedule);
            AddRecordingHistory(
                schedule.DisplayName,
                schedule.ProgramTitle,
                string.Empty,
                schedule.StartLocal,
                now,
                "Missed");
        }

        NotifyRecordingStateChanged();
    }

    private void UpdateRecordingScheduleVisualState()
    {
        var nextSchedule = _activeScheduledRecording ?? GetNextRecordingSchedule();
        var hasSchedule = nextSchedule is not null;
        ScheduleRecordingButton.IsEnabled = _currentRequest is not null && !_isRecording;
        ScheduleRecordingButton.Content = "Schedule";
        CancelRecordingScheduleButton.IsEnabled = hasSchedule;
        RecordingScheduleDatePicker.IsEnabled = !_isScheduledRecordingActive;
        RecordingScheduleTimeTextBox.IsEnabled = !_isScheduledRecordingActive;
        RecordingScheduleDurationTextBox.IsEnabled = !_isScheduledRecordingActive;

        if (nextSchedule is null)
        {
            RecordingScheduleText.Text = "No recording scheduled.";
            return;
        }

        if (_isScheduledRecordingActive)
        {
            var remaining = nextSchedule.EndLocal - DateTime.Now;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            RecordingScheduleText.Text = $"Recording {nextSchedule.DisplayName} until {nextSchedule.EndLocal:t} ({FormatScheduleCountdown(remaining)} left)";
            return;
        }

        var startsIn = nextSchedule.StartLocal - DateTime.Now;
        if (startsIn < TimeSpan.Zero)
        {
            startsIn = TimeSpan.Zero;
        }

        RecordingScheduleText.Text = $"Next: {nextSchedule.DisplayName} at {nextSchedule.StartLocal:g} ({FormatScheduleCountdown(startsIn)})";
    }

    private RecordingScheduleEntry? GetNextRecordingSchedule()
    {
        return _recordingSchedules
            .OrderBy(schedule => schedule.StartLocal)
            .FirstOrDefault();
    }

    private void EnsureRecordingScheduleTimer()
    {
        if (_recordingSchedules.Count > 0)
        {
            _recordingScheduleTimer.Start();
        }
        else
        {
            _recordingScheduleTimer.Stop();
        }
    }

    private static bool IsUsableSchedule(RecordingScheduleEntry schedule)
    {
        return !string.IsNullOrWhiteSpace(schedule.StreamUri) &&
               Uri.TryCreate(schedule.StreamUri, UriKind.Absolute, out _) &&
               !string.IsNullOrWhiteSpace(schedule.DisplayName) &&
               schedule.EndLocal > DateTime.Now;
    }

    private void SortRecordingSchedules()
    {
        var sorted = _recordingSchedules
            .OrderBy(schedule => schedule.StartLocal)
            .ThenBy(schedule => schedule.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _recordingSchedules.Clear();
        foreach (var schedule in sorted)
        {
            _recordingSchedules.Add(schedule);
        }
    }

    private void AddRecordingHistory(string displayName, string programTitle, string filePath, DateTime startedAt, DateTime endedAt, string status)
    {
        var fileSize = 0L;
        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            fileSize = new FileInfo(filePath).Length;
        }

        _recordingHistory.Insert(0, new RecordingHistoryEntry
        {
            DisplayName = displayName,
            ProgramTitle = programTitle,
            FilePath = filePath,
            StartedLocal = startedAt,
            EndedLocal = endedAt,
            Status = status,
            FileSizeBytes = fileSize
        });

        while (_recordingHistory.Count > 50)
        {
            _recordingHistory.RemoveAt(_recordingHistory.Count - 1);
        }

        NotifyRecordingStateChanged();
    }

    private void NotifyRecordingStateChanged()
    {
        RecordingStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static RecordingScheduleEntry CloneSchedule(RecordingScheduleEntry schedule)
    {
        return new RecordingScheduleEntry
        {
            Id = schedule.Id,
            StreamUri = schedule.StreamUri,
            DisplayName = schedule.DisplayName,
            KeepPlayerOnTop = schedule.KeepPlayerOnTop,
            AllowReconnect = schedule.AllowReconnect,
            RecordingsDirectory = schedule.RecordingsDirectory,
            LogoSource = schedule.LogoSource,
            FallbackLogoSource = schedule.FallbackLogoSource,
            ProgramTitle = schedule.ProgramTitle,
            StartLocal = schedule.StartLocal,
            EndLocal = schedule.EndLocal,
            CreatedLocal = schedule.CreatedLocal
        };
    }

    private static RecordingHistoryEntry CloneHistory(RecordingHistoryEntry entry)
    {
        return new RecordingHistoryEntry
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            ProgramTitle = entry.ProgramTitle,
            FilePath = entry.FilePath,
            StartedLocal = entry.StartedLocal,
            EndedLocal = entry.EndedLocal,
            Status = entry.Status,
            FileSizeBytes = entry.FileSizeBytes
        };
    }

    private static string FormatScheduleCountdown(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
            : $"{value:hh\\:mm\\:ss}";
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

    private void ToggleRecordingHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        RecordingHistoryPanel.Visibility = RecordingHistoryPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OpenRecordingHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = RecordingHistoryListBox.SelectedItem as RecordingHistoryEntry;
        if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath))
        {
            SetStatus("Select a recording history item first.");
            return;
        }

        if (!File.Exists(entry.FilePath))
        {
            SetStatus("Recording file is no longer available.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{entry.FilePath}\"",
            UseShellExecute = true
        });
    }

    private void PlayRecordingHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = RecordingHistoryListBox.SelectedItem as RecordingHistoryEntry;
        if (entry is null || string.IsNullOrWhiteSpace(entry.FilePath))
        {
            SetStatus("Select a recording history item first.");
            return;
        }

        if (!File.Exists(entry.FilePath))
        {
            SetStatus("Recording file is no longer available.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = entry.FilePath,
            UseShellExecute = true
        });
    }

    private void DeleteRecordingHistoryItemButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = RecordingHistoryListBox.SelectedItem as RecordingHistoryEntry;
        if (entry is null)
        {
            SetStatus("Select a recording history item first.");
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(entry.FilePath) && File.Exists(entry.FilePath))
            {
                File.Delete(entry.FilePath);
            }

            _recordingHistory.Remove(entry);
            NotifyRecordingStateChanged();
            SetStatus("Recording history item deleted.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not delete recording: {ex.Message}");
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

    private string BuildRecordingFileName(string displayName, string programTitle)
    {
        var channel = SanitizeFileNameToken(displayName, "channel");
        var program = SanitizeFileNameToken(programTitle, string.Empty);
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var format = string.IsNullOrWhiteSpace(_recordingFileNameFormat)
            ? "{timestamp}_{channel}"
            : _recordingFileNameFormat;
        var fileName = format
            .Replace("{timestamp}", timestamp, StringComparison.OrdinalIgnoreCase)
            .Replace("{channel}", channel, StringComparison.OrdinalIgnoreCase)
            .Replace("{program}", program, StringComparison.OrdinalIgnoreCase);
        var sanitized = SanitizeFileNameToken(fileName, $"{timestamp}_{channel}");

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = $"{timestamp}_{channel}";
        }

        return sanitized.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            ? sanitized
            : $"{sanitized}.ts";
    }

    private static string SanitizeFileNameToken(string value, string fallback)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string((value ?? string.Empty)
            .Select(ch => invalidCharacters.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim(' ', '.', '_');

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private void ResetRecordingState()
    {
        _isRecording = false;
        _recordingFilePath = string.Empty;
        _activeRecordingDisplayName = string.Empty;
        _activeRecordingProgramTitle = string.Empty;
        _recordingStartedAt = default;
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
        ToggleRecordingButton.Content = _isRecording ? "Stop" : "Record";
        ToggleRecordingButton.Tag = _isRecording ? "Recording" : null;
        RecordingText.Visibility = _isRecording ? Visibility.Visible : Visibility.Collapsed;
        if (_isRecording)
        {
            UpdateRecordingTimerText();
            _recordingTimer.Start();
        }
        else
        {
            _recordingTimer.Stop();
            RecordingText.Text = string.Empty;
        }

        UpdateRecordingScheduleVisualState();
    }

    private void UpdateRecordingTimerText()
    {
        if (!_isRecording || _recordingStartedAt == default)
        {
            return;
        }

        RecordingText.Text = $"REC {DateTime.Now - _recordingStartedAt:hh\\:mm\\:ss}";
    }

    private void UpdateAudioVisualState()
    {
        VolumeText.Text = BuildAudioStatusText();
        ToggleMuteButton.Content = _mediaPlayer.Mute ? "Unmute" : "Mute";
        ToggleMuteButton.Tag = _mediaPlayer.Mute ? "Active" : null;
    }

    private void UpdateNetworkDebitVisualState()
    {
        ToggleNetworkDebitButton.Content = "Net";
        ToggleNetworkDebitButton.Tag = _showNetworkDebit ? "Active" : null;
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
        SetPlaybackBadge(status.TrimEnd('.'));
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
        if (_mediaPlayer.IsPlaying && cache >= BufferingCompleteCacheThreshold)
        {
            StopBufferingStatusUpdates();
            SetPlaybackBadge("Playing");
            SetStatus("Playing.");
            return;
        }

        _isBuffering = true;
        SetPlaybackBadge("Buffering");
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

    private void SetPlaybackBadge(string status)
    {
        PlaybackStateBadgeText.Text = string.IsNullOrWhiteSpace(status) ? "Idle" : status;
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
        RecordingSchedulePanel.Visibility = Visibility.Collapsed;
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
        RecordingSchedulePanel.Visibility = Visibility.Visible;
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
        _recordingTimer.Stop();
        _recordingScheduleTimer.Stop();
        PreviewKeyDown -= PlayerWindow_PreviewKeyDown;
        VideoSurface.MediaPlayer = null;
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        PlaybackStopped?.Invoke(this, EventArgs.Empty);
        base.OnClosed(e);
    }
}
