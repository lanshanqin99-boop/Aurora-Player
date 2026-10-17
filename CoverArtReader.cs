
namespace Aurora;

/// <summary>
/// Pulls the colour information out of a cover image, without decoding anything.
///
/// Deliberately dumb: it hands over an encoded image stream and lets the caller decide
/// how to decode it, so this module has no dependency beyond TagLib and stays usable off
/// the UI thread.
/// </summary>
internal static class CoverArtReader
{
    /// <summary>
    /// Returns the embedded cover bytes for <paramref name="filePath"/>, preferring the
    /// front cover. Null when the file has no picture, cannot be read, or holds only
    /// entries too small to be a cover.
    /// </summary>
    public static CoverImage? ReadEmbeddedCover(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        try
        {
            using TagLib.File file = TagLib.File.Create(filePath);
            TagLib.IPicture? picture = SelectPicture(file.Tag);

            if (picture is null || picture.Data is null || picture.Data.Count < MinBytes)
            {
                return null;
            }

            var bytes = new byte[picture.Data.Count];
            picture.Data.CopyTo(bytes, 0);

            if (!LooksLikeImage(bytes))
            {
                return null;
            }

            string mimeType = string.IsNullOrWhiteSpace(picture.MimeType)
                ? GuessMimeType(bytes)
                : picture.MimeType;

            return new CoverImage(bytes, mimeType);
        }
        catch (Exception)
        {
            // A file with no readable tags, or one TagLib refuses, simply has no cover.
            return null;
        }
    }

    /// <summary>
    /// Front cover first, then the leading picture of any type. TagLib orders pictures
    /// with the front cover in front when the format records the type, but ID3v2.2 and
    /// some containers report <c>NotAPicture</c>, which is why the fallback matters.
    /// </summary>
    private static TagLib.IPicture? SelectPicture(TagLib.Tag tag)
    {
        var pictures = tag.Pictures;
        if (pictures is null || pictures.Length == 0)
        {
            return null;
        }

        foreach (TagLib.IPicture picture in pictures)
        {
            if (picture.Type == TagLib.PictureType.FrontCover)
            {
                return picture;
            }
        }

        return pictures[0];
    }

    /// <summary>A cover smaller than this is not worth analysing.</summary>
    private const int MinBytes = 512;

    /// <summary>
    /// Magic-number check. Embedded artwork is frequently mislabelled — an APIC frame
    /// claiming image/jpeg often carries a PNG — so the actual signature is what decides.
    /// </summary>
    private static bool LooksLikeImage(IReadOnlyList<byte> data)
    {
        if (data.Count < 8)
        {
            return false;
        }

        // JPEG
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return true;
        }

        // PNG
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return true;
        }

        // GIF
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            return true;
        }

        // BMP
        if (data[0] == 0x42 && data[1] == 0x4D)
        {
            return true;
        }

        // TIFF (little/big endian)
        if ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))
        {
            return true;
        }

        // WebP: "RIFF" .... "WEBP"
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data.Count >= 12 && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return true;
        }

        return false;
    }

    private static string GuessMimeType(IReadOnlyList<byte> data)
    {
        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return "image/jpeg";
        }

        if (data[0] == 0x89 && data[1] == 0x50)
        {
            return "image/png";
        }

        if (data[0] == 0x47 && data[1] == 0x49)
        {
            return "image/gif";
        }

        if (data[0] == 0x42 && data[1] == 0x4D)
        {
            return "image/bmp";
        }

        if (data[0] == 0x52 && data[1] == 0x49)
        {
            return "image/webp";
        }

        return "image/tiff";
    }
}

/// <summary>Encoded cover bytes together with the format they claim to be.</summary>
/// <param name="Data">The encoded image.</param>
/// <param name="MimeType">Declared or sniffed media type; informational.</param>
internal sealed record CoverImage(byte[] Data, string MimeType);
