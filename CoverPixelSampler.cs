using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace Aurora;

/// <summary>
/// Decodes a cover and reduces it to a small sRGB pixel sample.
///
/// The image is never decoded at full size: a <see cref="BitmapTransform"/> asks the
/// decoder for a thumbnail-sized result, so a 3000×3000 scan costs the same as a 300×300
/// one. Orientation is read from the image metadata and applied through the same
/// transform, so a cover stored rotated still contributes its real colours.
/// </summary>
internal static class CoverPixelSampler
{
    /// <summary>Longest edge of the decoded sample. Plenty for colour statistics.</summary>
    private const uint TargetEdge = 48;

    /// <summary>Pixels at or below this alpha are treated as absent.</summary>
    private const byte AlphaThreshold = 8;

    /// <summary>
    /// Decodes <paramref name="cover"/> into straight BGRA8 sRGB samples.
    /// Every failure path returns null rather than throwing.
    /// </summary>
    public static async Task<PixelSample?> SampleAsync(CoverImage cover)
    {
        if (cover.Data.Length == 0)
        {
            return null;
        }

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(cover.Data.AsBuffer());
            stream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;
            if (width == 0 || height == 0)
            {
                return null;
            }

            // Ask the decoder for a small result instead of decoding everything.
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, Math.Min(width, TargetEdge)),
                ScaledHeight = Math.Max(1, Math.Min(height, TargetEdge)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };

            ApplyOrientation(transform, await ReadOrientationAsync(decoder));

            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.ColorManageToSRgb);

            return new PixelSample(pixels.DetachPixelData(), (int)transform.ScaledWidth, (int)transform.ScaledHeight);
        }
        catch (Exception)
        {
            // Undecodable, truncated, or an unexpected format: no colour from this cover.
            return null;
        }
    }

    /// <summary>
    /// Reads the orientation tag. Formats disagree on where it lives, so each known
    /// flavour is tried and the first usable value wins.
    /// </summary>
    private static async Task<ushort> ReadOrientationAsync(BitmapDecoder decoder)
    {
        try
        {
            string[] queries =
            {
                "/app1/ifd/{ushort=274}",   // EXIF
                "/ifd/{ushort=274}",        // TIFF
                "/xmp/tiff:Orientation",    // XMP
            };

            foreach (string query in queries)
            {
                try
                {
                    BitmapPropertySet properties = await decoder.BitmapProperties.GetPropertiesAsync(new[] { query });
                    if (properties.TryGetValue(query, out BitmapTypedValue? typed) && typed?.Value is ushort orientation)
                    {
                        return orientation;
                    }
                }
                catch (Exception)
                {
                    // This flavour is not present; try the next.
                }
            }
        }
        catch (Exception)
        {
            // No metadata at all: assume the image is already upright.
        }

        return 1;
    }

    /// <summary>
    /// Expresses an EXIF orientation as a rotation plus an optional horizontal flip. The
    /// mirrored cases are a flip of the rotated result, which is what
    /// <see cref="BitmapTransform"/> composes.
    /// </summary>
    private static void ApplyOrientation(BitmapTransform transform, ushort orientation)
    {
        switch (orientation)
        {
            case 2:
                transform.Rotation = BitmapRotation.None;
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 3:
                transform.Rotation = BitmapRotation.Clockwise180Degrees;
                break;
            case 4:
                transform.Rotation = BitmapRotation.Clockwise180Degrees;
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 5:
                transform.Rotation = BitmapRotation.Clockwise90Degrees;
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 6:
                transform.Rotation = BitmapRotation.Clockwise90Degrees;
                break;
            case 7:
                transform.Rotation = BitmapRotation.Clockwise270Degrees;
                transform.Flip = BitmapFlip.Horizontal;
                break;
            case 8:
                transform.Rotation = BitmapRotation.Clockwise270Degrees;
                break;
            default:
                transform.Rotation = BitmapRotation.None;
                break;
        }
    }

    /// <summary>The threshold the analysis uses to decide a pixel is present.</summary>
    public static byte TransparencyCutoff => AlphaThreshold;
}

/// <summary>
/// Straight (non-premultiplied) BGRA8 samples in sRGB, together with their dimensions.
/// </summary>
internal sealed class PixelSample
{
    /// <summary>
    /// Takes the pixels and the size they were sampled at.
    /// </summary>
    /// <param name="bgra">Raw pixels, four bytes each.</param>
    /// <param name="width">Sample width in pixels.</param>
    /// <param name="height">Sample height in pixels.</param>
    public PixelSample(byte[] bgra, int width, int height)
    {
        Bgra = bgra;
        Width = width;
        Height = height;
    }

    public byte[] Bgra { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>True when the buffer really holds a full image.</summary>
    public bool IsUsable =>
        Width > 0 && Height > 0 && Bgra.Length >= Width * Height * 4;

    /// <summary>Number of pixels the buffer carries.</summary>
    public int PixelCount => Width * Height;

    /// <summary>
    /// Reads pixel <paramref name="index"/> as straight sRGB, or null when it is
    /// effectively transparent and must not vote.
    /// </summary>
    public Color? PixelAt(int index)
    {
        int offset = index * 4;
        if (offset + 3 >= Bgra.Length)
        {
            return null;
        }

        byte b = Bgra[offset];
        byte g = Bgra[offset + 1];
        byte r = Bgra[offset + 2];
        byte a = Bgra[offset + 3];

        if (a <= CoverPixelSampler.TransparencyCutoff)
        {
            return null;
        }

        return Color.FromArgb(a, r, g, b);
    }
}
