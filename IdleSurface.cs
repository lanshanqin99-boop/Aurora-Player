using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Aurora;

/// <summary>
/// The idle photograph as the two windows use it: how strongly it is shown, how it is brought in and
/// out, where the file may live, and how a band of it is applied to a window.
///
/// Everything here is shared deliberately. Opacity, fade and placement are one set of decisions for
/// both windows, so "the same treatment" is not two implementations that have to be kept in step.
/// The arithmetic deciding which part of the picture each window shows lives in
/// <see cref="IdleCanvas"/>, and the shading each window draws is the same brush in both XAML files.
/// </summary>
internal static class IdleSurface
{
    /// <summary>
    /// Opacity of the photograph while it is up. It is meant to be read as a scene rather than as a
    /// wash, so the photograph carries almost all of its own darkening and this stays high enough
    /// that the aurora keeps its colour.
    /// </summary>
    internal const double Opacity = 0.55;

    /// <summary>
    /// Places one window's share of the picture: the photograph is laid out at its full size and slid
    /// up by the band's offset, so the window becomes a viewport onto it rather than a frame the
    /// picture has to be fitted into. It is anchored to the top-left, which is what makes the offset
    /// mean "this much of the pair is above me" in either window.
    ///
    /// The shading is deliberately not placed here. It is the same brush in both XAML files and it
    /// stretches to whichever window it is in, which is what keeps the player's own darkening exactly
    /// as it was: the queue gets the same treatment over its own height rather than the pair sharing
    /// one ramp, which would have lightened the player's lower half.
    /// </summary>
    internal static void Apply(Image picture, IdleCanvas.Band band)
    {
        picture.Width = band.Width;
        picture.Height = band.Height;

        // The offset is negative in both directions when the picture is larger than the window, which
        // it is: the window is a viewport onto the picture, and its own edges do the cropping.
        picture.Margin = new Thickness(band.Left, -band.Top, 0, 0);

        // Filled rather than fitted: the band is already measured at the size the picture is meant to
        // be drawn at, so filling this box distorts nothing — and a uniform fit here would be free to
        // re-crop what the band arithmetic has decided.
        picture.Stretch = Stretch.Fill;
    }

    /// <summary>
    /// One opacity fade on a storyboard, eased unless the duration is zero. Shared so both windows
    /// bring the picture in over the same interval.
    /// </summary>
    internal static void AddFade(
        Storyboard storyboard, DependencyObject target, double to, Duration duration, EasingFunctionBase? ease)
    {
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = ease,
        };

        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
    }

    /// <summary>
    /// Where the photograph may live, in the order it is looked for. Read from the file itself rather
    /// than through a package URI: the picture ships next to the executable and is read by path, which
    /// loads whether the app runs from a build output or from its installed package. The folder beside
    /// the app comes first, then the project's own background folder, so the picture can be replaced
    /// from either without a rebuild.
    /// </summary>
    internal static IEnumerable<string> ImageCandidates()
    {
        const string name = "IdleBackground.jpg";

        yield return Path.Combine(AppContext.BaseDirectory, "Assets", name);
        yield return Path.Combine(AppContext.BaseDirectory, name);
        yield return Path.Combine(AppContext.BaseDirectory, "..", "original background", name);
        yield return Path.Combine(AppContext.BaseDirectory, "..", "orignal background.jpg");
    }
}
