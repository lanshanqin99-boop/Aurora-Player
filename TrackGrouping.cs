using System;
using System.Collections.Generic;
using System.IO;

namespace Aurora;

/// <summary>
/// Where one imported batch ends and the next begins, said in the queue itself.
///
/// A queue built up over several sittings reads as one undifferentiated run of rows: nothing in it
/// says that the first thirty came from one drama and the next twelve from another. The divider is
/// that missing line, and the text on it is the folder the tracks came from — the subfolder when the
/// files sit in one, and the folder that was imported when they sit directly in it. Both cases are the
/// same question asked of the path — what is the directory this file is in — so a batch of files
/// picked from many places labels itself as it goes, and a folder imported whole labels each of its
/// subfolders.
///
/// Grouping is by that label alone, which is what makes one rule cover both things the reader wants
/// to see: a new import always starts a new group, and so does a new subfolder inside one import. Two
/// imports of the same folder therefore read as one group, which is the right answer — they are
/// contiguous and identical, and a divider between them would say nothing.
///
/// No UI types here: the rule is the part worth checking on its own.
/// </summary>
internal static class TrackGrouping
{
    /// <summary>
    /// The folder a track is shown under.
    ///
    /// When a folder was imported, the answer is read relative to it: the first level under it is the
    /// main folder and the next is the subfolder, and both are shown — <c>囚于永夜 · 第二季</c>. Two
    /// dramas with a season of the same name are otherwise indistinguishable, and the season alone is
    /// not enough to find anything by. A file sitting directly in the imported folder has no subfolder
    /// to name, so it is shown under the folder itself.
    ///
    /// A file picked on its own has no imported folder above it, so it is shown under the folder it
    /// lives in — the same question, asked of what is there.
    /// </summary>
    internal static string LabelFor(string filePath, string? rootPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(filePath) ?? string.Empty;

            if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
            {
                string relative = Path.GetRelativePath(rootPath, directory);

                if (relative is "." or "")
                {
                    // Directly in the imported folder: that folder names the group.
                    return Path.GetFileName(rootPath);
                }

                if (!relative.StartsWith('.'))
                {
                    string[] parts = relative.Split(
                        new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                        StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length >= 2)
                    {
                        return $"{parts[0]} · {parts[1]}";
                    }

                    if (parts.Length == 1)
                    {
                        return parts[0];
                    }
                }
            }

            string name = Path.GetFileName(directory);

            return string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(filePath)
                : name;
        }
        catch (Exception)
        {
            // A path the framework will not even parse still needs a row to sit on.
            return string.Empty;
        }
    }

    /// <summary>
    /// Marks the first track of every group. Called after anything that can change what neighbours
    /// what — an append, a removal, a drag — because the mark belongs to the position, not the track.
    /// </summary>
    internal static void Recompute(IReadOnlyList<AudioTrack> tracks)
    {
        string? previous = null;

        for (int i = 0; i < tracks.Count; i++)
        {
            string label = tracks[i].GroupLabel;
            bool startsGroup = i == 0
                || !string.Equals(label, previous, StringComparison.OrdinalIgnoreCase);

            tracks[i].IsGroupStart = startsGroup;
            previous = label;
        }
    }
}
