using System;
using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace Schmube;

public partial class PlayerWindow : Window
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;

    public PlayerWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _libVlc = new LibVLC("--network-caching=1500");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoSurface.MediaPlayer = _mediaPlayer;

        _mediaPlayer.Playing += (_, _) => SetStatus("Playing.");
        _mediaPlayer.Buffering += (_, e) => SetStatus($"Buffering {e.Cache:0}%");
        _mediaPlayer.Stopped += (_, _) => SetStatus("Stopped.");
        _mediaPlayer.EndReached += (_, _) => SetStatus("Stream ended.");
        _mediaPlayer.EncounteredError += (_, _) => SetStatus("Playback error. Verify the channel URL and provider headers.");
    }

    public Task PlayAsync(PlaybackRequest request)
    {
        return Dispatcher.InvokeAsync(() =>
        {
            SetAlwaysOnTop(request.KeepPlayerOnTop);
            NowPlayingText.Text = request.DisplayName;
            Title = $"Schmube Player - {request.DisplayName}";
            SetStatus($"Connecting to {request.DisplayName}...");

            using var media = new Media(_libVlc, request.StreamUri);

            if (!string.IsNullOrWhiteSpace(request.UserAgent))
            {
                media.AddOption($":http-user-agent={request.UserAgent}");
            }

            if (!string.IsNullOrWhiteSpace(request.Referer))
            {
                media.AddOption($":http-referrer={request.Referer}");
            }

            var started = _mediaPlayer.Play(media);
            if (!started)
            {
                throw new InvalidOperationException("LibVLC failed to start playback.");
            }
        }).Task;
    }

    public void Stop()
    {
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
        VideoSurface.MediaPlayer = null;
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
        base.OnClosed(e);
    }
}
