using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Aurora;

/// <summary>
/// The colour arithmetic both windows share, so the player and the queue paint the same palette with
/// the same code rather than with two drifting copies of it.
/// </summary>
internal static class AmbientGradient
{
    /// <summary>Length of the fade applied when the palette changes.</summary>
    public static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(600));

    /// <summary>
    /// The gradient's third stop: the background lifted toward the accent, so the sweep between the
    /// two ends of the surface is visible instead of a flat wash.
    /// </summary>
    public static Color Lift(AmbientPalette palette) => Mix(palette.Background, palette.Accent, 0.28);

    /// <summary>Linear-space blend, so lightening a dark colour does not wash it out.</summary>
    public static Color Mix(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0, 1);

        static double Linear(byte channel)
        {
            double v = channel / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static byte Srgb(double linear)
        {
            double v = linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
            return (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }

        double r = Linear(from.R) + ((Linear(to.R) - Linear(from.R)) * t);
        double g = Linear(from.G) + ((Linear(to.G) - Linear(from.G)) * t);
        double b = Linear(from.B) + ((Linear(to.B) - Linear(from.B)) * t);

        return Color.FromArgb(0xFF, Srgb(r), Srgb(g), Srgb(b));
    }

    /// <summary>Adds a colour fade for one gradient stop to a storyboard.</summary>
    public static void AddColorAnimation(Storyboard storyboard, GradientStop stop, Color to)
    {
        var animation = new ColorAnimation
        {
            To = to,
            Duration = FadeDuration,

            // A gradient stop is not an independent animation target, so this runs on the UI thread.
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, stop);
        Storyboard.SetTargetProperty(animation, "Color");
        storyboard.Children.Add(animation);
    }

    /// <summary>
    /// Fades a three-stop gradient to the palette. The gradient <em>is</em> the transition: animating
    /// the stops means the colour changes smoothly instead of snapping when the track changes.
    /// Callers that need the colours set at once, with no fade, use <see cref="SetStops"/>.
    /// </summary>
    public static void FadeTo(
        GradientStop primary, GradientStop secondary, GradientStop accent, AmbientPalette palette)
    {
        var storyboard = new Storyboard();

        AddColorAnimation(storyboard, primary, palette.Background);
        AddColorAnimation(storyboard, secondary, palette.BackgroundSecondary);
        AddColorAnimation(storyboard, accent, Lift(palette));

        storyboard.Begin();
    }

    /// <summary>Applies the palette's three colours directly, with no animation.</summary>
    public static void SetStops(
        GradientStop primary, GradientStop secondary, GradientStop accent, AmbientPalette palette)
    {
        primary.Color = palette.Background;
        secondary.Color = palette.BackgroundSecondary;
        accent.Color = Lift(palette);
    }
}
