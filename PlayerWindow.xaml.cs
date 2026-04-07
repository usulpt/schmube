using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace Schmube;

public partial class PlayerWindow : Window
{
    private const int MaxReconnectAttempts = 3;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;

    private Media? _currentMedia;
    private PlaybackRequest? _currentRequest;
    private CancellationTokenSource? _reconnectCts;
    private bool _manualStopRequested;
    private int _reconnectAttempt;
    private bool _isRecording;
    private string _recordingFilePath = string.Empty;

    public PlayerWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _libVlc = new LibVLC("--network-caching=1500");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoSurface.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Playing += (_, _) => SetStatus("Playing.");
        _mediaPlayer.Buffering += (_, e) => SetStatus($"Buffering {e.Cache:0}%");
        _mediaPlayer.Stopped += (_, _) =>
        {
            if (_manualStopRequested)
            {
                SetStatus("Stopped.");
            }
        };
        _mediaPlayer.EndReached += (_, _) => HandlePlaybackInterruption("Stream ended.");
        _mediaPlayer.EncounteredError += (_, _) => HandlePlaybackInterruption("Playback error.");

        UpdateRecordingVisualState();
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
        }).Task;
    }

    public void Stop()
    {
        CancelReconnect();
        _manualStopRequested = true;
        _currentRequest = null;
        ResetRecordingState();

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Stop();
        }
        else
        {
            SetStatus("Stopped.");
        }
    }

    public void SetAlwaysOnTop(bool isOnTop)
    {
        Topmost = isOnTop;
    }

    private void StartPlaybackCore(PlaybackRequest request, bool isReconnect)
    {
        _currentMedia?.Dispose();
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

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        Stop();
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

    protected override void OnClosed(EventArgs e)
    {
        CancelReconnect();
        VideoSurface.MediaPlayer = null;
        _currentMedia?.Dispose();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        base.OnClosed(e);
    }
}
