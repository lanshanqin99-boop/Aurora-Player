using Microsoft.UI.Xaml;

namespace Aurora;

/// <summary>
/// Display-only shaping of the queue's text.
///
/// <b>This file used to filter.</b> It carried a rule set built around one particular library: a
/// keyword list of download-page markers, address and domain detection, trailing punctuation and
/// bracket trimming, a list of season phrases, and a limit that hid artist names longer than five Han
/// characters. Those rules existed to hide the promotion text that particular file names carry, which
/// is a property of one collection rather than of an audio player, so they have been removed: the
/// queue now shows exactly what the tags and the file system say.
///
/// What is left is the shaping any library needs — a title falls back to the file name when the tag
/// has none, an empty line collapses, and the player's header falls back to the application name so
/// it is never blank.
/// </summary>
public static class DisplayRules
{
    /// <summary>
    /// Whether the divider that opens a group is drawn. It lives here because it is the same kind of
    /// decision as the rest of this file: a rule of the display layer, expressed as the value XAML
    /// binds to, rather than a converter the view has to supply.
    /// </summary>
    public static Visibility GroupHeaderVisibility(bool isGroupStart)
        => isGroupStart ? Visibility.Visible : Visibility.Collapsed;

    // ---- public surface (bound from XAML) -------------------------------------------------

    /// <summary>
    /// The title to show: the title the tags carry, or the file name without its extension when they
    /// carry none. Nothing is filtered, cut or rewritten.
    /// </summary>
    public static string TitleFor(string? filePath, string? title)
    {
        string value = title ?? string.Empty;
        return value.Length > 0 ? value : StemFor(filePath);
    }

    /// <summary>Whether the title line is shown: not for a track with neither a title nor a name.</summary>
    public static Visibility TitleVisibilityFor(string? filePath, string? title) =>
        VisibilityOf(TitleFor(filePath, title));

    /// <summary>The artist text to show, exactly as the tags carry it.</summary>
    public static string ArtistFor(string? filePath, string? artist)
    {
        _ = filePath;
        return artist ?? string.Empty;
    }

    /// <summary>
    /// Whether the artist line is shown. Only an artist the tags do not carry is hidden; no name is
    /// hidden for its length or its content.
    /// </summary>
    public static Visibility ArtistVisibilityFor(string? filePath, string? artist) =>
        VisibilityOf(ArtistFor(filePath, artist));

    // ---- the player header -----------------------------------------------------------------

    /// <summary>
    /// Title for the player's own header, with the window's name as the fallback so the header always
    /// reads as something.
    /// </summary>
    public static string PlayerTitleFor(string? filePath, string? title)
    {
        string value = TitleFor(filePath, title);
        return value.Length == 0 ? "Aurora" : value;
    }

    /// <summary>Artist for the player's own header, exactly as the tags carry it.</summary>
    public static string PlayerArtistFor(string? filePath, string? artist) => ArtistFor(filePath, artist);

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// The file name without its extension. The stem is read rather than the whole name because the
    /// extension is punctuation of the file system, not of the track, and it should not become a title.
    /// </summary>
    private static string StemFor(string? filePath) =>
        System.IO.Path.GetFileNameWithoutExtension(filePath ?? string.Empty);

    /// <summary>An empty display string collapses its line; anything else is shown.</summary>
    private static Visibility VisibilityOf(string text) =>
        text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
}
