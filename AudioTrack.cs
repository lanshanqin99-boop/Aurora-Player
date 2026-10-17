namespace Aurora;

using System;
using System.Collections.Generic;
using System.ComponentModel;

/// <summary>
/// One entry in the playlist. Metadata is best-effort: when a tag cannot be read,
/// the file name is used so the UI always has something to show.
/// </summary>
public sealed class AudioTrack : INotifyPropertyChanged
{
    public AudioTrack(string filePath, string title, string artist, TimeSpan duration)
    {
        FilePath = filePath;
        Title = title;
        Artist = artist;
        Duration = duration;
    }

    /// <summary>Absolute path of the audio file.</summary>
    public string FilePath { get; }

    /// <summary>Tag title, or the file name when no usable tag was found.</summary>
    public string Title { get; }

    /// <summary>Tag artist, or empty when unknown.</summary>
    public string Artist { get; }

    /// <summary>
    /// Tag duration, or <see cref="TimeSpan.Zero"/> when unknown. The media parser can
    /// fill this in later, which is why it is not readonly.
    /// </summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>Applies a duration discovered after the track was created.</summary>
    internal void SetDuration(TimeSpan duration)
    {
        if (duration > TimeSpan.Zero)
        {
            Duration = duration;
        }
    }

    /// <summary>Duration rendered for the playlist UI, e.g. "3:07".</summary>
    public string DurationText =>
        Duration <= TimeSpan.Zero
            ? "--:--"
            : Duration.TotalHours >= 1
                ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2}"
                : $"{Duration.Minutes}:{Duration.Seconds:D2}";

    /// <summary>Artist for the playlist UI, falling back to the file name.</summary>
    public string ArtistText => string.IsNullOrWhiteSpace(Artist) ? System.IO.Path.GetFileName(FilePath) : Artist;

    /// <summary>Set when the track enters the queue, and never changed after. It is the final tie-break of
    /// <see cref="DefaultOrder"/>, so two rows that read the same keep the order they were added in
    /// instead of an arbitrary one — <c>List.Sort</c> is not stable by itself.
    /// </summary>
    internal long Order { get; } = NextOrder++;

    /// <summary>
    /// The folder this track was imported from, shown on the divider that opens its group. Set once, as
    /// the track enters the queue: it describes where the file came from, which does not change.
    /// </summary>
    public string GroupLabel { get; internal set; } = string.Empty;

    /// <summary>
    /// True for the first track of a group — the one the divider is drawn above. It belongs to the
    /// track's <em>position</em>, so it is recomputed whenever the queue is reordered rather than set
    /// once at import, and it notifies because a row already on screen changes with it.
    /// </summary>
    public bool IsGroupStart
    {
        get => _isGroupStart;
        internal set
        {
            if (_isGroupStart == value)
            {
                return;
            }

            _isGroupStart = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGroupStart)));
        }
    }

    private bool _isGroupStart;

    public event PropertyChangedEventHandler? PropertyChanged;

    private static long NextOrder;

    /// <summary>
    /// The text this track is ordered by: its full path.
    ///
    /// Ordering by the path first keeps the files of one folder together — which is what a folder's
    /// contents look like in Explorer — and orders by file name inside it, because a path is the
    /// folder, a separator, and then the name. Nothing here is filtered or rewritten: the queue is
    /// ordered by where the files are, not by what their tags happen to say.
    /// </summary>
    public string SortKey => FilePath ?? string.Empty;

    /// <summary>
    /// Order of newly added files, following Explorer: the comparison is the shell's own logical string
    /// comparison, so the mixed digit-and-text names this library is full of land in the order Explorer
    /// shows them — <c>1, 2, 3, 9, 10, 11</c>, never <c>1, 10, 11, 2</c>. When two paths still compare
    /// equal, the one added first stays first.
    /// </summary>
    public static IComparer<AudioTrack> DefaultOrder { get; } = new NaturalOrderComparer();

    /// <summary>
    /// The shell's logical string comparison — the one File Explorer orders its own views with. It
    /// compares a run of digits as a number rather than character by character, which is what puts
    /// "10" after "9" rather than after "1", and it is culture-aware for text.
    /// </summary>
    [System.Runtime.InteropServices.DllImport(
        "shlwapi.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    private static int CompareTracks(AudioTrack a, AudioTrack b)
    {
        int byPath = StrCmpLogicalW(a.SortKey, b.SortKey);
        return byPath != 0 ? byPath : a.Order.CompareTo(b.Order);
    }

    private sealed class NaturalOrderComparer : IComparer<AudioTrack>
    {
        public int Compare(AudioTrack? x, AudioTrack? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return CompareTracks(x, y);
        }
    }
}
