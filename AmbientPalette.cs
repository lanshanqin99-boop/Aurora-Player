using Windows.UI;

namespace Aurora;

/// <summary>
/// The ambient colours a track contributes to the interface, all derived from its cover
/// art. Plain sRGB values: no Composition objects, no theme resources, so the palette can
/// be produced off the UI thread and handed to whatever draws the background.
/// </summary>
/// <param name="Background">
/// The environment colour for large surfaces: the cover's dominant hue, darkened and
/// desaturated so it can fill a window without glaring.
/// </param>
/// <param name="BackgroundSecondary">A companion shade for a restrained gradient.</param>
/// <param name="Accent">A brighter derivation for small interactive highlights.</param>
public readonly record struct AmbientPalette(Color Background, Color BackgroundSecondary, Color Accent)
{
    /// <summary>
    /// The fallback used whenever there is no usable cover: pure black, which is the
    /// product's resting background.
    /// </summary>
    public static AmbientPalette Black { get; } = new(
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
        Color.FromArgb(0xFF, 0x00, 0x00, 0x00));
}
