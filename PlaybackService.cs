using System.Collections.ObjectModel;
using LibVLCSharp.Shared;

namespace Aurora;

/// <summary>
/// Local audio playback core: owns the libvlc instance, the playlist, and the
/// current track. Deliberately free of any UI type so the window stays responsible
/// for presentation.
/// </summary>
public sealed class PlaybackService : IDisposable
{
    private static readonly string[] SupportedExtensions =
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".oga", ".opus",
        ".wma", ".aiff", ".aif", ".ape", ".mka", ".mp4",
    };

    /// <summary>Extensions the playlist scanner accepts, for use by file pickers.</summary>
    public static IReadOnlyList<string> SupportedFileExtensions => SupportedExtensions;

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;

    /// <summary>
    /// The single playlist. This collection is the one and only source of truth:
    /// every window binds to it directly and every mutation goes through the
    /// methods below, so the queue order and <see cref="CurrentIndex"/> never
    /// diverge from what is shown.
    /// </summary>
    private readonly ObservableCollection<AudioTrack> _playlist = new();

    private Media? _currentMedia;

    /// <summary>File the loaded Media belongs to, so a redundant reload can be skipped.</summary>
    private string? _loadedPath;

    private int _currentIndex = -1;
    private bool _disposed;

    /// <summary>True while a duration pass is walking the queue, so a second one cannot start.</summary>
    private bool _durationPassRunning;

    /// <summary>Set when a queue event arrives mid-pass, so the pass repeats once more.</summary>
    private bool _durationPassRequested;

    /// <summary>
    /// Files the media parser has already been asked about. A file whose duration cannot be
    /// reported is remembered, so it is not parsed again on the next queue event — without
    /// this the pending set never emptied and every event re-parsed the whole queue.
    /// </summary>
    private readonly HashSet<string> _durationAttempted = new(StringComparer.OrdinalIgnoreCase);

    public PlaybackService()
    {
        // Locates libvlc next to the app (deployed by VideoLAN.LibVLC.Windows).
        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);

        _mediaPlayer.EndReached += (_, _) => OnPlayerEndReached();
        _mediaPlayer.Playing += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.Paused += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
        _mediaPlayer.Stopped += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the current track finishes and playback moves on.</summary>
    public event EventHandler? EndReached;

    /// <summary>
    /// Tolerance used to decide that a media really reached its end: a fraction of the track for
    /// short files, capped at a couple of seconds for long ones. Measured player time at a genuine
    /// end lands on the reported length (a 2-second file reported 1908 ms of 2000 ms), so this
    /// absorbs the final frame without letting a zero-position event through on a short track.
    /// </summary>
    private static long EndTolerance(long length) =>
        Math.Min(2000, Math.Max(250, length / 10));

    /// <summary>
    /// Turns the player's <c>EndReached</c> into <see cref="EndReached"/>, but only when the
    /// media actually played to its end.
    ///
    /// libvlc raises <c>EndReached</c> for a media it could not open as well: the player state
    /// goes straight to Ended and both <c>Length</c> and <c>Time</c> stay at zero. Measured on
    /// a 231-second track, that event arrived 70–200 ms after the media was handed over, and
    /// acting on it moved the queue: the window called <see cref="Next"/>, which loads the
    /// following track and stops the one just given to the player, and each of those loads
    /// produced another such event. The queue walked forward several tracks, the row the user
    /// clicked was replaced within a fraction of a second, and the second and later batches
    /// looked like they would not play at all.
    ///
    /// A media that never opened reports no length, so length is the discriminator: zero length
    /// is a failed load, not a finished track, and it must not advance the queue.
    /// </summary>
    private void OnPlayerEndReached()
    {
        if (_disposed)
        {
            return;
        }

        long length;
        long time;
        try
        {
            length = _mediaPlayer.Length;
            time = _mediaPlayer.Time;
        }
        catch (Exception ex)
        {
            // The player was torn down between the callback and this read: nothing ended.
            Log("EndReached ignored: player unavailable", null, ex);
            return;
        }

        if (length <= 0)
        {
            Log($"EndReached ignored: media never opened (time={time}ms)");
            return;
        }

        if (time < length - EndTolerance(length))
        {
            Log($"EndReached ignored: {time}ms of {length}ms");
            return;
        }

        EndReached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when play/pause state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the playlist or the current index changes.</summary>
    public event EventHandler? PlaylistChanged;

    public IReadOnlyList<AudioTrack> Playlist => _playlist;

    /// <summary>The live playlist collection, for UI binding. Mutate through the methods here.</summary>
    public ObservableCollection<AudioTrack> Items => _playlist;

    public int CurrentIndex => _currentIndex;

    public AudioTrack? CurrentTrack =>
        _currentIndex >= 0 && _currentIndex < _playlist.Count ? _playlist[_currentIndex] : null;

    /// <summary>The track that is playing, and the fallback before playback has started.</summary>
    private AudioTrack? PlayingTrack => _playingTrack ?? CurrentTrack;

    /// <summary>
    /// Remembers the track itself, alongside <see cref="CurrentIndex"/>. The index alone cannot
    /// survive a reorder performed by the list: the moment a row is dragged, the index points at
    /// whatever slid into that slot, so the track that is playing has to be identified by identity.
    /// </summary>
    private AudioTrack? _playingTrack;

    /// <summary>
    /// True once the player holds the current track's media, i.e. the track has been started
    /// rather than merely selected. Adding files to a queue that was empty adopts the first
    /// entry as current without playing it, so this is the only reliable way for the window to
    /// tell "the user started this track" from "this track is just the selection" — which is
    /// what the lazy cover-art load is keyed on.
    /// </summary>
    public bool IsCurrentTrackLoaded
    {
        get
        {
            AudioTrack? track = CurrentTrack;
            return _loadedPath is not null
                && track is not null
                && string.Equals(_loadedPath, track.FilePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool IsPlaying => _mediaPlayer.IsPlaying;

    /// <summary>Current playback position, or zero when nothing is loaded.</summary>
    public TimeSpan Position
    {
        get
        {
            if (_disposed || _mediaPlayer.Media is null)
            {
                return TimeSpan.Zero;
            }

            try
            {
                return TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time));
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }
    }

    /// <summary>Length reported by the player, falling back to the tag duration.</summary>
    public TimeSpan Duration
    {
        get
        {
            if (_disposed)
            {
                return CurrentTrack?.Duration ?? TimeSpan.Zero;
            }

            try
            {
                long length = _mediaPlayer.Length;
                if (length > 0)
                {
                    return TimeSpan.FromMilliseconds(length);
                }
            }
            catch (Exception)
            {
                // Fall through to the tag duration.
            }

            return CurrentTrack?.Duration ?? TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Builds a playlist from a set of files and/or folders. Folders are scanned
    /// recursively for supported audio files.
    /// </summary>
    public void LoadFrom(IEnumerable<string> paths)
    {
        _playlist.Clear();
        foreach (AudioTrack track in Scan(paths))
        {
            _playlist.Add(track);
        }

        _currentIndex = _playlist.Count > 0 ? 0 : -1;
        _playingTrack = CurrentTrack;
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Appends files/folders to the existing queue, leaving the current track where
    /// it is. An empty queue adopts index 0 so the first track is ready to play.
    /// </summary>
    public void AppendPaths(IEnumerable<string> paths)
    {
        bool wasEmpty = _playlist.Count == 0;
        var existing = new HashSet<string>(
            _playlist.Select(t => t.FilePath), StringComparer.OrdinalIgnoreCase);

        // Only the files being added now are ordered; the existing queue keeps the
        // order the user arranged, which is why this does not touch _playlist.
        var added = new List<AudioTrack>();
        foreach (AudioTrack track in Scan(paths))
        {
            if (existing.Add(track.FilePath))
            {
                added.Add(track);
            }
        }

        added.Sort(AudioTrack.DefaultOrder);
        foreach (AudioTrack track in added)
        {
            _playlist.Add(track);
        }

        if (wasEmpty)
        {
            _currentIndex = _playlist.Count > 0 ? 0 : -1;
            _playingTrack = CurrentTrack;
        }

        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fills in durations that the file tags could not provide, using the media parser
    /// on the existing libvlc instance. One lightweight Media parse per track — no
    /// extra MediaPlayer is created, and the currently playing track is untouched.
    ///
    /// All parsing is awaited, so nothing touches the playlist from a libvlc callback
    /// thread and no LibVLC event handler is registered by hand. Call this from the UI
    /// thread: the continuation resumes there, which is where <see cref="PlaylistChanged"/>
    /// must be raised. No exception escapes — a file that cannot be parsed is skipped.
    ///
    /// Two rules keep this bounded, because this method is called from the PlaylistChanged
    /// handler and therefore once per queue event:
    /// <list type="bullet">
    /// <item><b>One pass at a time.</b> A call that arrives while a pass is running is folded
    /// into it instead of starting a second pass over the same files.</item>
    /// <item><b>Every file is attempted once.</b> A file whose duration the parser cannot
    /// report is never retried, so the set of files to examine cannot stay non-empty and be
    /// re-parsed on every event.</item>
    /// </list>
    /// Both are needed: with neither, a queue of tracks whose durations cannot be resolved
    /// was re-parsed in full on every queue event, so the live passes and the per-file parse
    /// time grew with each file added.
    /// </summary>
    public async Task ResolveMissingDurationsAsync()
    {
        if (_durationPassRunning)
        {
            // A pass is already walking the queue; let it do the work for this request too.
            _durationPassRequested = true;
            return;
        }

        _durationPassRunning = true;
        Log("ResolveMissingDurations: enter");

        try
        {
            do
            {
                _durationPassRequested = false;
                await RunDurationPassAsync().ConfigureAwait(true);
            }
            while (_durationPassRequested);
        }
        finally
        {
            _durationPassRunning = false;
        }
    }

    /// <summary>
    /// One walk over the tracks whose duration is still unknown and which have not been
    /// attempted yet. Raises <see cref="PlaylistChanged"/> when it learned something.
    /// </summary>
    private async Task RunDurationPassAsync()
    {
        // Capture the caller's context (the UI thread, when invoked from the window) so
        // the playlist notification is raised there however the parse continuation
        // resumes.
        System.Threading.SynchronizationContext? context =
            System.Threading.SynchronizationContext.Current;

        AudioTrack[] pending;
        try
        {
            // Snapshot: the queue can change while parses are in flight. Only files that
            // have never been through the parser are taken, so a file that reports nothing
            // is not examined again on the next event.
            pending = _playlist
                .Where(t => t.Duration <= TimeSpan.Zero && !_durationAttempted.Contains(t.FilePath))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log("ResolveMissingDurations: snapshot failed", null, ex);
            return;
        }

        Log($"ResolveMissingDurations: {pending.Length} track(s) need a duration");
        bool changed = false;

        foreach (AudioTrack track in pending)
        {
            // Recorded before the parse: whether it succeeds or not, this file has had its
            // one attempt.
            _durationAttempted.Add(track.FilePath);

            TimeSpan parsed = await TryParseDurationAsync(track.FilePath).ConfigureAwait(true);
            if (parsed > TimeSpan.Zero)
            {
                try
                {
                    track.SetDuration(parsed);
                    changed = true;
                }
                catch (Exception ex)
                {
                    Log("ResolveMissingDurations: SetDuration failed", track.FilePath, ex);
                }
            }
        }

        if (!changed)
        {
            Log("ResolveMissingDurations: nothing to update");
            return;
        }

        try
        {
            if (context is not null && !ReferenceEquals(
                    System.Threading.SynchronizationContext.Current, context))
            {
                context.Post(_ => RaisePlaylistChanged(), null);
                Log("ResolveMissingDurations: posted to UI context");
                return;
            }
        }
        catch (Exception ex)
        {
            Log("ResolveMissingDurations: posting failed, raising inline", null, ex);
        }

        RaisePlaylistChanged();
        Log("ResolveMissingDurations: done");
    }

    /// <summary>Raises the playlist notification, guarded so a handler cannot escape.</summary>
    private void RaisePlaylistChanged()
    {
        try
        {
            PlaylistChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log("PlaylistChanged handler threw", null, ex);
        }
    }

    /// <summary>
    /// Parses one file's duration. Returns zero when the parser cannot tell, when the
    /// file is unreadable, or when anything at all goes wrong; failures are logged and
    /// never propagate.
    /// </summary>
    private async Task<TimeSpan> TryParseDurationAsync(string filePath)
    {
        Media? media = null;
        try
        {
            // Local files go through FromType.FromPath; a Uri would make libvlc
            // re-interpret the path (and mis-handle characters such as '#').
            using (media = new Media(_libVlc, filePath, FromType.FromPath))
            {
                Log("parse: begin", filePath);

                // Awaiting Parse means no manual ParsedChanged handler runs on a libvlc
                // callback thread, so nothing here can touch the playlist off-thread.
                MediaParsedStatus status = await media.Parse(MediaParseOptions.ParseLocal)
                    .ConfigureAwait(true);

                Log($"parse: status={status}", filePath);

                if (status != MediaParsedStatus.Done)
                {
                    Log("parse: status not Done, skipping", filePath);
                    return TimeSpan.Zero;
                }

                long milliseconds = media.Duration;
                if (milliseconds <= 0)
                {
                    Log($"parse: duration={milliseconds}, skipping", filePath);
                    return TimeSpan.Zero;
                }

                return TimeSpan.FromMilliseconds(milliseconds);
            }
        }
        catch (Exception ex)
        {
            Log("parse: failed", filePath, ex);
            return TimeSpan.Zero;
        }
        finally
        {
            // The using above normally disposes; this only guards the ctor-throw path.
            media?.Dispose();
        }
    }

    /// <summary>
    /// Where diagnostics are written: a file under the current user's local application data.
    ///
    /// It used to be an absolute path inside the developer's own project folder. That was wrong twice
    /// over: the path does not exist on anybody else's machine, so a published build could not write a
    /// log at all, and it put a private path into the published source. The local application data
    /// folder is writable both from a package — where the install folder is read-only — and from an
    /// unpacked folder, and it belongs to whoever is running the app.
    ///
    /// Empty when no writable folder could be found, in which case logging quietly does nothing.
    /// </summary>
    private static readonly string LogPath = BuildLogPath();

    private static string BuildLogPath()
    {
        try
        {
            string folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Aurora");

            System.IO.Directory.CreateDirectory(folder);
            return System.IO.Path.Combine(folder, "aurora-diagnostics.log");
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Writes a diagnostics line. Never throws: logging must not become the reason the app dies.
    /// </summary>
    private static void Log(string message, string? filePath = null, Exception? ex = null)
    {
        try
        {
            var text = new System.Text.StringBuilder();
            text.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(message);
            if (filePath is not null)
            {
                text.Append(" | file=").Append(filePath);
            }

            if (ex is not null)
            {
                text.Append(" | ").Append(ex.GetType().FullName).Append(": ").Append(ex.Message);
                text.Append(" | ").Append(ex.StackTrace);
            }

            if (LogPath.Length == 0)
            {
                return;
            }

            System.IO.File.AppendAllText(LogPath, text.AppendLine().ToString());
        }
        catch
        {
            // Diagnostics only.
        }
    }

    /// <summary>
    /// Re-derives the current index from the track that is playing, after the queue was reordered in
    /// place.
    ///
    /// This is the path the queue window uses: with CanReorderItems the ListView, not this service,
    /// moves the row inside <see cref="Items"/>, because that is the only reorder the list will
    /// actually render. The collection is reordered in place — nothing is created, resorted or
    /// rebuilt — and the playing track is still in it; only <see cref="CurrentIndex"/> has to catch
    /// up. Playback is not touched.
    /// </summary>
    public void NotifyQueueReordered()
    {
        AudioTrack? playing = PlayingTrack;
        if (playing is null)
        {
            return;
        }

        int index = _playlist.IndexOf(playing);
        if (index < 0)
        {
            return;
        }

        _currentIndex = index;
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes the track at <paramref name="index"/> current and plays it.</summary>
    public void SetCurrent(int index)
    {
        if (index < 0 || index >= _playlist.Count)
        {
            return;
        }

        _currentIndex = index;
        _playingTrack = CurrentTrack;
        PlayCurrent();
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes a track. When the removed item was playing, the following item takes
    /// over; if it was the last one, the previous becomes current.
    /// </summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _playlist.Count)
        {
            return;
        }

        bool wasCurrent = index == _currentIndex;

        if (wasCurrent)
        {
            // Frees the Media reference on _currentMedia so the player is not handed a
            // new Media while holding one that is being removed from the queue.
            StopInternal()?.Dispose();
        }

        _playlist.RemoveAt(index);

        if (_playlist.Count == 0)
        {
            _currentIndex = -1;
        }
        else if (wasCurrent)
        {
            // Shift the index onto what was the next item, or the new last one.
            _currentIndex = Math.Min(index, _playlist.Count - 1);
            PlayCurrent();
        }
        else if (index < _currentIndex)
        {
            _currentIndex--;
        }

        // The removed track must not stay the remembered one, or a later reorder would look for a
        // row that is no longer in the queue.
        if (wasCurrent || _playingTrack is null || !_playlist.Contains(_playingTrack))
        {
            _playingTrack = CurrentTrack;
        }

        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reorders the queue through the service. The playing track stays current: its index is
    /// recomputed from the track object rather than from the old position. The collection itself is
    /// moved in place — nothing is copied, rebuilt or re-sorted — and no <c>Reset</c> is raised.
    /// </summary>
    public void Move(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= _playlist.Count
            || newIndex < 0 || newIndex >= _playlist.Count
            || oldIndex == newIndex)
        {
            return;
        }

        AudioTrack? playing = PlayingTrack;
        _playlist.Move(oldIndex, newIndex);

        if (playing is not null)
        {
            int updated = _playlist.IndexOf(playing);
            if (updated >= 0)
            {
                _currentIndex = updated;
            }
        }

        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Expands files and folders into a sorted, de-duplicated track list. Ordering is
    /// applied to the resolved titles via <see cref="AudioTrack.DefaultOrder"/> (natural
    /// number handling plus pinyin for Chinese), not to the raw file paths — sorting by
    /// path is plain string order, which puts "10" before "2".
    /// </summary>
    private static IEnumerable<AudioTrack> Scan(IEnumerable<string> paths)
    {
        var files = new List<string>();

        // Which of the requested paths each file came out of. The label a row is grouped under is read
        // relative to that root — the first level under it is the main folder — so it has to be known
        // here, where the walk is, rather than guessed from the file's path afterwards.
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                if (IsSupported(path))
                {
                    files.Add(path);
                    roots[path] = path;
                }
            }
            else if (Directory.Exists(path))
            {
                try
                {
                    IEnumerable<string> found = Directory
                        .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Where(IsSupported);

                    foreach (string file in found)
                    {
                        files.Add(file);
                        roots[file] = path;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // A folder we cannot read is skipped rather than fatal.
                }
            }
        }

        var tracks = files
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                AudioTrack track = ReadTrack(file);

                // Where the file came from, recorded as it enters the queue: it is what the divider
                // above its group is labelled with.
                track.GroupLabel = roots.TryGetValue(file, out string? root)
                    ? TrackGrouping.LabelFor(file, root)
                    : TrackGrouping.LabelFor(file, null);

                return track;
            })
            .ToList();

        tracks.Sort(AudioTrack.DefaultOrder);
        return tracks;
    }

    /// <summary>Starts the current track from the beginning.</summary>
    public void PlayCurrent()
    {
        var track = CurrentTrack;
        if (track is null)
        {
            return;
        }

        // Already loaded and pointed at this very file: nothing to tear down or rebuild.
        // Re-issuing Play() here is what let the EndReached handler, which arrives on a
        // libvlc thread while the previous Stop() is still settling, start the whole
        // load sequence again and re-enter this method.
        if (_loadedPath is not null
            && string.Equals(_loadedPath, track.FilePath, StringComparison.OrdinalIgnoreCase)
            && _mediaPlayer.Media is not null)
        {
            if (!_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Play();
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        Media? previous = StopInternal();

        Media media;
        try
        {
            media = new Media(_libVlc, new Uri(track.FilePath));
        }
        catch (Exception)
        {
            // A path libvlc refuses is not fatal: release what we held and stay stopped.
            previous?.Dispose();
            _loadedPath = null;
            return;
        }

        _currentMedia = media;
        _loadedPath = track.FilePath;
        _mediaPlayer.Play(media);

        // The previous Media is released only after the player has been handed the new
        // one, so libvlc never has a Media disappear from under a pending Play/Stop.
        previous?.Dispose();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Toggles between play and pause.</summary>
    public void TogglePlayPause()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            return;
        }

        // Pause() then TogglePause() resumes; with no media yet, start the track.
        if (_mediaPlayer.Media is null)
        {
            PlayCurrent();
            return;
        }

        _mediaPlayer.Play();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Next()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        // Sequential playback: the track after this one, wrapping at the end of the list.
        _currentIndex = (_currentIndex + 1) % _playlist.Count;
        _playingTrack = CurrentTrack;
        PlayCurrent();
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Previous()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        _currentIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count;
        _playingTrack = CurrentTrack;
        PlayCurrent();
        PlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// True when a seek can actually land: a media is loaded and the player reports a
    /// length to map the position onto. While idle this is false, and the UI uses it to
    /// leave seeking alone instead of handing a position to libvlc.
    /// </summary>
    public bool CanSeek
    {
        get
        {
            if (_disposed || _mediaPlayer.Media is null)
            {
                return false;
            }

            try
            {
                return _mediaPlayer.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Jumps to a position within the current track. Does nothing when there is no media
    /// to move or the player has already been disposed, so an idle seek can never reach
    /// libvlc.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        if (_disposed || _mediaPlayer.Media is null)
        {
            return;
        }

        try
        {
            _mediaPlayer.Time = (long)Math.Max(0, position.TotalMilliseconds);
        }
        catch (Exception)
        {
            // The media can be torn down between the check and the write.
        }
    }

    /// <summary>
    /// Stops the player and hands back the Media that is no longer in use, rather than
    /// disposing it here. The caller releases it once the player has been given its
    /// replacement, so libvlc never loses a Media while a Start/Stop is in flight.
    /// </summary>
    private Media? StopInternal()
    {
        if (_mediaPlayer.IsPlaying || _mediaPlayer.Media is not null)
        {
            _mediaPlayer.Stop();
        }

        Media? previous = _currentMedia;
        _currentMedia = null;
        _loadedPath = null;
        return previous;
    }

    private static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads title/artist/duration from the file's tags. Any failure falls back to
    /// the file name so the playlist always has a displayable entry.
    /// </summary>
    private static AudioTrack ReadTrack(string filePath)
    {
        string fallbackTitle = Path.GetFileNameWithoutExtension(filePath);

        try
        {
            using var file = TagLib.File.Create(filePath);
            string title = string.IsNullOrWhiteSpace(file.Tag.Title) ? fallbackTitle : file.Tag.Title;
            string artist = file.Tag.FirstPerformer ?? file.Tag.FirstAlbumArtist ?? string.Empty;
            return new AudioTrack(filePath, title, artist, file.Properties?.Duration ?? TimeSpan.Zero);
        }
        catch
        {
            return new AudioTrack(filePath, fallbackTitle, string.Empty, TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Stop first: disposing a Media the player is still running would pull it out
        // from under libvlc's own threads.
        try
        {
            if (_mediaPlayer.IsPlaying || _mediaPlayer.Media is not null)
            {
                _mediaPlayer.Stop();
            }
        }
        catch (Exception)
        {
            // Shutting down either way.
        }

        _currentMedia?.Dispose();
        _currentMedia = null;
        _loadedPath = null;

        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}