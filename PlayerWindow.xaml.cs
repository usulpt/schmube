using System;
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
    }

    public Task PlayAsync(PlaybackRequest request)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            CancelReconnect();
            _manualStopRequested = false;
            _reconnectAttempt = 0;
            _currentRequest = request;

            SetAlwaysOnTop(request.KeepPlayerOnTop);
            NowPlayingText.Text = request.DisplayName;
            Title = $"Schmube Player - {request.DisplayName}";

            StartPlaybackCore(request, isReconnect: false);
        }).Task;
    }

    public void Stop()
    {
        CancelReconnect();
        _manualStopRequested = true;
        _currentRequest = null;

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
        _currentMedia = new Media(_libVlc, request.StreamUri);

        SetStatus(isReconnect
            ? $"Reconnecting to {request.DisplayName}..."
            : $"Connecting to {request.DisplayName}...");

        var started = _mediaPlayer.Play(_currentMedia);
        if (!started)
        {
            throw new InvalidOperationException("LibVLC failed to start playback.");
        }
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
