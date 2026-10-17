using System;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Aurora;

/// <summary>
/// What a headset's buttons talk to.
///
/// Aurora plays through libvlc, and libvlc knows nothing about the system media transport controls: no
/// session is ever registered, so the play/pause/next/previous buttons on a Bluetooth headset — and the
/// media keys on a keyboard, and the pop-up the system shows when a key is pressed — have nothing in
/// this app to reach. Pressing them does nothing at all.
///
/// The way in is a <see cref="MediaPlayer"/> that never plays anything. Its
/// <see cref="MediaPlayer.SystemMediaTransportControls"/> is the session Aurora registers, and the
/// player's own command manager is switched off so the transport controls are driven by hand instead —
/// which is the documented arrangement when the sound is coming from somewhere else. The buttons then
/// arrive on <see cref="SystemMediaTransportControls.ButtonPressed"/> and are handed to Aurora's
/// transport, and the state is written back the other way: playback status, the track's title and
/// artist, and the position, so the headset and the system's own panel show what is really happening
/// rather than a stale guess.
///
/// The player is silenced and never started. It exists for its session and nothing else; the only
/// reason it is given a source at all is that a session with nothing to describe is not one the system
/// keeps.
/// </summary>
internal sealed class MediaControls : IDisposable
{
    /// <summary>How often the position is reported. The transport controls are asleep otherwise.</summary>
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(4);

    private readonly MediaPlayer _player = new();
    private readonly SystemMediaTransportControls _controls;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _play;
    private readonly Action _pause;
    private readonly Action _next;
    private readonly Action _previous;

    private string? _sourcePath;
    private DateTime _lastTimeline = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// Takes over the session. The four actions are Aurora's own transport, called on the UI thread —
    /// the controls raise their buttons on a thread of their own, so every one of them is marshalled
    /// before it runs.
    /// </summary>
    internal MediaControls(
        DispatcherQueue dispatcher,
        Action play,
        Action pause,
        Action next,
        Action previous)
    {
        _dispatcher = dispatcher;
        _play = play;
        _pause = pause;
        _next = next;
        _previous = previous;

        _controls = _player.SystemMediaTransportControls;

        // Manual control: the player is not playing anything, so it must not be the thing deciding what
        // the transport controls say or do.
        _player.CommandManager.IsEnabled = false;
        _player.AutoPlay = false;
        _player.IsMuted = true;
        _player.Volume = 0;

        _controls.IsEnabled = true;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;

        // Without a type the system does not treat this as music, and the panel stays empty.
        _controls.DisplayUpdater.Type = MediaPlaybackType.Music;
    }

    /// <summary>
    /// Reports the track now playing: what the headset and the system panel show, and what makes this
    /// session the one the media keys belong to.
    /// </summary>
    internal void SetTrack(string title, string artist, string filePath)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // A session describing nothing is not kept, so the player is pointed at the file. It is
            // never started, and it is muted, so this is bookkeeping rather than a second playback.
            if (!string.Equals(_sourcePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                _sourcePath = filePath;
                _player.Source = MediaSource.CreateFromUri(new Uri(filePath));
            }

            SystemMediaTransportControlsDisplayUpdater updater = _controls.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.Update();
        }
        catch (Exception)
        {
            // A session without metadata is still a session, and the buttons still work.
        }
    }

    /// <summary>Says whether sound is coming out, so the buttons shown are the ones that make sense.</summary>
    internal void SetPlaying(bool playing)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch (Exception)
        {
            // Cosmetic.
        }
    }

    /// <summary>
    /// Reports the position, which is what the system panel's progress line and its seek bar read.
    /// Called from the progress tick; throttled, because each of these crosses into the shell.
    /// </summary>
    internal void SetPosition(TimeSpan position, TimeSpan duration)
    {
        if (_disposed || duration <= TimeSpan.Zero)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _lastTimeline < TimelineInterval)
        {
            return;
        }

        _lastTimeline = now;

        try
        {
            var timeline = new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = position,
                MaxSeekTime = duration,
                EndTime = duration,
            };

            _controls.UpdateTimelineProperties(timeline);
        }
        catch (Exception)
        {
            // Cosmetic.
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        // Raised off the UI thread: the transport it drives is the window's, so it is run there.
        _dispatcher.TryEnqueue(() =>
        {
            try
            {
                switch (args.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                        _play();
                        break;
                    case SystemMediaTransportControlsButton.Pause:
                        _pause();
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        _next();
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        _previous();
                        break;
                }
            }
            catch (Exception)
            {
                // A button that fails must not take the session down with it.
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.IsEnabled = false;
            _player.Dispose();
        }
        catch (Exception)
        {
            // Shutting down.
        }
    }
}
