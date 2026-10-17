
namespace Aurora;

/// <summary>
/// Where the idle photograph falls across the player and the queue.
///
/// The two windows are stacked, share a width and sit a fixed gap apart, so the photograph is
/// measured once against the pair they make and each window is handed the band that falls inside it.
/// The queue's band begins exactly where the player's ended, plus the gap between them, which is what
/// makes the queue's backdrop a continuation of the player's rather than a second copy of the same
/// picture: the player shows the sky, the queue the ground below it, and no part of the photograph is
/// shown twice.
///
/// It is scaled to <em>cover</em> the pair. The scale is whichever of the two needs more, the pair's
/// width or its whole height, so the picture always reaches every edge of both windows. Fitting it to
/// the width alone left the queue's lower rows beyond the picture's end, and those rows showed the
/// background gradient instead — a black block under an otherwise full-bleed photograph. Covering
/// costs a strip of the picture's sides when the queue is tall, which is what the windows crop
/// evenly; the alternative was a hole, and a hole is what the eye finds.
///
/// Nothing here touches a UI type. The arithmetic is the part that has to be right, and keeping it
/// free of XAML is what lets it be checked on its own, without a window.
/// </summary>
internal static class IdleCanvas
{
    /// <summary>
    /// Gap left between the player and the queue, in effective pixels. It is the same number the
    /// queue's placement uses: the photograph is only continuous across the gap while the band
    /// arithmetic and the window placement agree on how big it is.
    /// </summary>
    internal const double Gap = 8;

    /// <summary>Natural size of Assets/IdleBackground.jpg.</summary>
    private const double PictureWidth = 735;
    private const double PictureHeight = 1109;

    /// <summary>
    /// The band of the photograph one window shows. The picture is drawn at <paramref name="Width"/>
    /// by <paramref name="Height"/> and offset by (<paramref name="Left"/>, <paramref name="Top"/>), so
    /// the window is a viewport onto it.
    /// </summary>
    internal readonly record struct Band(double Width, double Height, double Left, double Top);

    /// <summary>The player's band: the top of the photograph, from the top of the pair.</summary>
    internal static Band ForPlayer(double width, double playerHeight, double queueHeight)
        => For(width, SurfaceHeight(playerHeight, queueHeight), 0);

    /// <summary>
    /// The queue's band: the photograph as it continues below the player. It starts one gap below the
    /// player's height, so the rows the gap covers are the only part of the picture neither window
    /// shows.
    /// </summary>
    internal static Band ForQueue(double width, double playerHeight, double queueHeight)
        => For(width, SurfaceHeight(playerHeight, queueHeight), QueueTop(playerHeight));

    /// <summary>How far below the top of the pair the queue's first pixel sits.</summary>
    internal static double QueueTop(double playerHeight) => Math.Max(0, playerHeight) + Gap;

    /// <summary>
    /// How tall the pair is that the photograph has to cover. With no queue open that is the player
    /// alone; with one, it reaches to the queue's last row.
    /// </summary>
    internal static double SurfaceHeight(double playerHeight, double queueHeight)
    {
        double player = Math.Max(0, playerHeight);
        double queue = Math.Max(0, queueHeight);

        return queue <= 0 ? player : QueueTop(player) + queue;
    }

    /// <summary>
    /// How much the photograph is scaled by to cover a pair of this width and height. It is the larger
    /// of the two ratios, so neither edge is ever reached short.
    /// </summary>
    internal static double ScaleFor(double width, double surfaceHeight)
        => Math.Max(
            Math.Max(1, width) / PictureWidth,
            Math.Max(0, surfaceHeight) / PictureHeight);

    private static Band For(double width, double surfaceHeight, double top)
    {
        double scale = ScaleFor(width, surfaceHeight);

        double pictureWidth = PictureWidth * scale;
        double pictureHeight = PictureHeight * scale;

        // Centred horizontally, so whatever the cover costs in width is taken evenly from both sides.
        double left = (Math.Max(1, width) - pictureWidth) / 2;

        // When the picture is exactly the window's width the arithmetic can leave a few hundredths of
        // a trillionth behind, and a margin of -0 is noise in every reading of it: while closed, the
        // picture is meant to sit exactly on the window with nothing cropped.
        if (left > -0.01)
        {
            left = 0;
        }

        return new Band(pictureWidth, pictureHeight, left, Math.Max(0, top));
    }
}
