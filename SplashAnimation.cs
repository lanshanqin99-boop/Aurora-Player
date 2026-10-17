using System;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Aurora;

/// <summary>
/// The startup reveal: the logo drawn rather than faded in.
///
/// It runs entirely on the compositor. Every element is a composition visual and every change is a
/// composition animation scheduled with its own delay, so the whole five seconds is handed to the GPU
/// in one go and the UI thread is never asked for a frame. That is not only about cost: it is what
/// makes the reveal independent of the work happening behind it. The player is being built on the UI
/// thread during exactly these five seconds — reading its XAML, starting libvlc, resolving covers — and
/// a reveal driven from the UI thread would stutter through all of it. This one cannot: nothing it does
/// touches that thread again.
///
/// What it draws, and why each part is drawn the way it is:
///
/// <list type="bullet">
/// <item><b>The orbit is drawn, not revealed.</b> A trimmed ellipse: the stroke sweeps from its start
/// point round to its close, with a brighter head running just ahead of the drawn line so the leading
/// edge reads as light arriving rather than as a shape growing.</item>
/// <item><b>The mark is lit, not faded.</b> A band of light travels across it — a gradient mask sliding
/// left to right — uncovering the artwork in its wake while the whole mark comes up out of the dark.
/// A plain fade would show the finished logo all at once, which is the one thing this must not do.</item>
/// <item><b>The wordmark waits.</b> It arrives after the mark has settled, in a quarter of a second,
/// because it is a label and not the subject.</item>
/// <item><b>The last second is a scan.</b> A wide arc under the logo is uncovered from left to right by
/// a moving clip with a soft head on its edge, and what the light has passed over is left behind very
/// faintly — the trace of the scan.</item>
/// </list>
///
/// No particles, no blooming explosions and no video: one flat colour, two logo images, and three
/// pieces of geometry.
///
/// <b>The palette is black and white.</b> There used to be a blue-violet bloom behind the mark, a
/// coloured wash that gave the card a sky to sit in. It has been removed for the release: the visual
/// identity of an audio player should not depend on a colour the user has no say in, and a plain black
/// card with white artwork is the version that reads the same on every desktop and matches the player,
/// which is itself a black surface. The orbit, the sweep and the scan are unchanged — they are drawn in
/// white and neutral greys instead of blue, and the two logo images ship tinted to white.
/// </summary>
internal static class SplashAnimation
{
    // ---- the design, in effective pixels of the 720x540 card -------------------------------------
    //
    // The artwork's layout as measured in the original picture, and the orbit's measured geometry. The
    // scale that fits that layout into the card is derived from these rather than guessed, so the card
    // can be resized without moving anything relative to anything else.

    private const double ContentLeft = 346;
    private const double ContentTop = 224;
    private const double ContentRight = 1138;
    private const double ContentBottom = 743;

    private const double MarkLeft = 489;
    private const double MarkTop = 208;
    private const double MarkRight = 1025;
    private const double MarkBottom = 565;

    private const double WordLeft = 334;
    private const double WordTop = 576;
    private const double WordRight = 1150;
    private const double WordBottom = 755;

    private const double OrbitCentreX = 732.9;
    private const double OrbitCentreY = 424.3;
    private const double OrbitMajor = 248.9;
    private const double OrbitMinor = 79.3;
    private const double OrbitAngle = -21.67;

    private const double Padding = 70;

    // The arc along the bottom, in card coordinates. It is the lower half of a very wide ellipse whose
    // ends fall outside the card, so the light enters low on the left, crosses, and leaves low on the
    // right — a scan across the frame rather than a shape sitting in it.
    private const double ArcCentreX = 360;
    private const double ArcCentreY = 720;
    private const double ArcRadiusX = 390;
    private const double ArcRadiusY = 250;

    // ---- the timing, in seconds ------------------------------------------------------------------

    /// <summary>When the orbit starts drawing, and how long it takes.</summary>
    private const double RingDelay = 0.15;
    private const double RingDuration = 1.30;

    /// <summary>The orbit's leading head, as a fraction of the orbit's length.</summary>
    private const float RingHead = 0.045f;

    /// <summary>The light travelling across the mark, and the mark coming up out of the dark.</summary>
    private const double MarkSweepDelay = 0.80;
    private const double MarkSweepDuration = 1.70;
    private const double MarkRiseDelay = 0.85;
    private const double MarkRiseDuration = 1.35;

    /// <summary>The wordmark: late, and brief.</summary>
    private const double WordDelay = 3.15;
    private const double WordDuration = 0.25;

    /// <summary>The scan along the bottom, and the trace it leaves.</summary>
    private const double ArcDelay = 4.00;
    private const double ArcDuration = 0.85;
    private const double ArcSettle = 4.95;
    private const float ArcResidual = 0.25f;

    /// <summary>When the card starts fading and the player takes over, and how long the fade lasts.</summary>
    public const double RevealAt = 5.00;
    private const double FadeDuration = 0.28;

    private const int KeyFrames = 24;

    /// <summary>
    /// Builds the reveal on the card and plays it. Returns as soon as everything is scheduled.
    /// <paramref name="revealing"/> fires when the card begins to fade and the player should be brought
    /// up; <paramref name="finished"/> fires when the fade is done and the card can be closed.
    /// </summary>
    public static void Play(UIElement stage, Action revealing, Action finished)
    {
        Compositor compositor = ElementCompositionPreview.GetElementVisual(stage).Compositor;

        double scale = Math.Min(
            (SplashWindow.DesignWidth - 2 * Padding) / (ContentRight - ContentLeft),
            (SplashWindow.DesignHeight - 2 * Padding) / (ContentBottom - ContentTop));

        double ToX(double artworkX) => Padding + (artworkX - ContentLeft) * scale;
        double ToY(double artworkY) => Padding + (artworkY - ContentTop) * scale;

        var root = compositor.CreateContainerVisual();
        root.Size = new Vector2((float)SplashWindow.DesignWidth, (float)SplashWindow.DesignHeight);
        ElementCompositionPreview.SetElementChildVisual(stage, root);

        double orbitX = ToX(OrbitCentreX);
        double orbitY = ToY(OrbitCentreY);
        double orbitMajor = OrbitMajor * scale;
        double orbitMinor = OrbitMinor * scale;

        BuildOrbit(compositor, root, orbitX, orbitY, orbitMajor, orbitMinor);
        BuildMark(compositor, root, ToX(MarkLeft), ToY(MarkTop),
                  (MarkRight - MarkLeft) * scale, (MarkBottom - MarkTop) * scale);
        BuildWordmark(compositor, root, ToX(WordLeft), ToY(WordTop),
                      (WordRight - WordLeft) * scale, (WordBottom - WordTop) * scale);
        BuildScan(compositor, root);

        _ = RunTimeline(compositor, root, revealing, finished);
    }

    /// <summary>
    /// The orbit: a trimmed ellipse with a brighter head running ahead of the drawn end. Both are the
    /// same geometry trimmed to different lengths, which is what keeps the head exactly on the path.
    /// </summary>
    private static void BuildOrbit(Compositor compositor, ContainerVisual root,
                                   double x, double y, double major, double minor)
    {
        ShapeVisual Create(float thickness, Color colour, out CompositionEllipseGeometry geometry)
        {
            geometry = compositor.CreateEllipseGeometry();
            geometry.Center = new Vector2((float)major, (float)minor);
            geometry.Radius = new Vector2((float)major, (float)minor);

            var shape = compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = compositor.CreateColorBrush(colour);
            shape.StrokeThickness = thickness;

            var visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2((float)(major * 2), (float)(minor * 2));
            visual.Offset = new Vector3((float)(x - major), (float)(y - minor), 0);

            // Rotating about the visual's own centre rather than the card's origin.
            visual.CenterPoint = new Vector3((float)major, (float)minor, 0);
            visual.RotationAngleInDegrees = (float)OrbitAngle;
            visual.Shapes.Add(shape);

            root.Children.InsertAtTop(visual);
            return visual;
        }

        Create(2.6f, Color.FromArgb(238, 238, 238, 238), out CompositionEllipseGeometry ring);
        ShapeVisual headVisual = Create(4.6f, Color.FromArgb(255, 255, 255, 255), out CompositionEllipseGeometry head);

        // The sweep, sampled so the same easing can be applied to both the ring and its head with the
        // head simply shifted along it. A trim cannot be animated as "the end minus a constant", so the
        // two animations are built from one curve rather than from one animation.
        var end = compositor.CreateScalarKeyFrameAnimation();
        var start = compositor.CreateScalarKeyFrameAnimation();
        end.Duration = TimeSpan.FromSeconds(RingDuration);
        end.DelayTime = TimeSpan.FromSeconds(RingDelay);
        start.Duration = end.Duration;
        start.DelayTime = end.DelayTime;

        for (int i = 0; i <= KeyFrames; i++)
        {
            float t = i / (float)KeyFrames;
            float value = Ease(t);
            end.InsertKeyFrame(t, value);
            start.InsertKeyFrame(t, Math.Max(0f, value - RingHead));
        }

        ring.StartAnimation("TrimEnd", end);
        head.StartAnimation("TrimStart", start);
        head.StartAnimation("TrimEnd", end);

        // The head is light arriving: absent before the orbit starts, brightest while it is being drawn,
        // gone the moment the orbit closes so nothing is left sitting on the join.
        headVisual.StartAnimation("Opacity", Ramp(compositor,
            (0, 0f),
            (RingDelay, 0f),
            (RingDelay + RingDuration - 0.22, 1f),
            (RingDelay + RingDuration, 0f)));
    }

    /// <summary>
    /// The mark, uncovered by a band of light travelling across it.
    ///
    /// The mask is a gradient whose opaque part is behind the band and whose transparent part is ahead
    /// of it. Sliding that gradient sideways is what moves the band, and because the first stop is
    /// opaque the part already uncovered stays uncovered.
    /// </summary>
    private static void BuildMark(Compositor compositor, ContainerVisual root,
                                  double x, double y, double width, double height)
    {
        var mask = compositor.CreateLinearGradientBrush();
        mask.StartPoint = new Vector2(-1.0f, 0.5f);
        mask.EndPoint = new Vector2(0.0f, 0.5f);
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.00f, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.62f, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0.94f, Color.FromArgb(0, 255, 255, 255)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(1.00f, Color.FromArgb(0, 255, 255, 255)));

        var surface = compositor.CreateSurfaceBrush(
            LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/SplashMark.png")));

        var brush = compositor.CreateMaskBrush();
        brush.Source = surface;
        brush.Mask = mask;

        var mark = compositor.CreateSpriteVisual();
        mark.Size = new Vector2((float)width, (float)height);
        mark.Offset = new Vector3((float)x, (float)y, 0);
        mark.Brush = brush;
        mark.Opacity = 0;
        root.Children.InsertAtTop(mark);

        // The band starts clear of the mark on the left and ends clear of it on the right, so the
        // artwork is entirely ahead of it at the start and entirely behind it at the end.
        var from = compositor.CreateVector2KeyFrameAnimation();
        var to = compositor.CreateVector2KeyFrameAnimation();
        from.Duration = to.Duration = TimeSpan.FromSeconds(MarkSweepDuration);
        from.DelayTime = to.DelayTime = TimeSpan.FromSeconds(MarkSweepDelay);

        for (int i = 0; i <= KeyFrames; i++)
        {
            float t = i / (float)KeyFrames;
            float offset = -1.0f + 2.10f * Ease(t);
            from.InsertKeyFrame(t, new Vector2(offset, 0.5f));
            to.InsertKeyFrame(t, new Vector2(offset + 1.0f, 0.5f));
        }

        mask.StartAnimation("StartPoint", from);
        mask.StartAnimation("EndPoint", to);

        // Up out of the dark across the whole sweep, so the light is not uncovering a mark that is
        // already at full brightness behind it.
        mark.StartAnimation("Opacity", Ramp(compositor,
            (0, 0f),
            (MarkRiseDelay, 0f),
            (MarkRiseDelay + MarkRiseDuration, 1f)));
    }

    /// <summary>The wordmark: no drawing, just the last quarter of a second of arrival.</summary>
    private static void BuildWordmark(Compositor compositor, ContainerVisual root,
                                      double x, double y, double width, double height)
    {
        var word = compositor.CreateSpriteVisual();
        word.Size = new Vector2((float)width, (float)height);
        word.Offset = new Vector3((float)x, (float)y, 0);
        word.Brush = compositor.CreateSurfaceBrush(
            LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/SplashWord.png")));
        word.Opacity = 0;
        root.Children.InsertAtTop(word);

        // The only thing in the reveal that is simply allowed to arrive.
        word.StartAnimation("Opacity", Ramp(compositor,
            (0, 0f),
            (WordDelay, 0f),
            (WordDelay + WordDuration, 1f)));
    }

    /// <summary>
    /// The scan along the bottom: the arc is uncovered left to right by a clip, and a soft head rides
    /// the edge of the uncovering so the leading edge is light rather than a straight cut. What has been
    /// uncovered settles to a very faint trace when the scan is done.
    /// </summary>
    private static void BuildScan(Compositor compositor, ContainerVisual root)
    {
        var geometry = compositor.CreateEllipseGeometry();
        geometry.Center = new Vector2((float)ArcCentreX, (float)ArcCentreY);
        geometry.Radius = new Vector2((float)ArcRadiusX, (float)ArcRadiusY);

        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(150, 150, 150, 150));
        shape.StrokeThickness = 2f;

        var arc = compositor.CreateShapeVisual();
        arc.Size = new Vector2((float)SplashWindow.DesignWidth, (float)SplashWindow.DesignHeight);
        arc.Shapes.Add(shape);

        // The uncovering: a rectangle clip that widens from nothing to the full card.
        var clipGeometry = compositor.CreateRectangleGeometry();
        clipGeometry.Size = new Vector2(0f, (float)SplashWindow.DesignHeight);
        clipGeometry.Offset = Vector2.Zero;
        arc.Clip = compositor.CreateGeometricClip(clipGeometry);
        root.Children.InsertAtTop(arc);

        var widen = compositor.CreateVector2KeyFrameAnimation();
        widen.Duration = TimeSpan.FromSeconds(ArcDuration);
        widen.DelayTime = TimeSpan.FromSeconds(ArcDelay);
        widen.InsertKeyFrame(0f, new Vector2(0f, (float)SplashWindow.DesignHeight));
        widen.InsertKeyFrame(1f, new Vector2((float)SplashWindow.DesignWidth, (float)SplashWindow.DesignHeight));
        clipGeometry.StartAnimation("Size", widen);

        // The head, carried along the arc itself rather than along a straight line.
        var head = compositor.CreateSpriteVisual();
        const float headSize = 96f;
        head.Size = new Vector2(headSize, headSize);

        var headBrush = compositor.CreateRadialGradientBrush();
        headBrush.EllipseCenter = new Vector2(0.5f, 0.5f);
        headBrush.EllipseRadius = new Vector2(0.5f, 0.5f);
        headBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.0f, Color.FromArgb(150, 255, 255, 255)));
        headBrush.ColorStops.Add(compositor.CreateColorGradientStop(0.5f, Color.FromArgb(48, 255, 255, 255)));
        headBrush.ColorStops.Add(compositor.CreateColorGradientStop(1.0f, Color.FromArgb(0, 255, 255, 255)));
        head.Brush = headBrush;
        head.Opacity = 0;
        root.Children.InsertAtTop(head);

        var travel = compositor.CreateVector3KeyFrameAnimation();
        travel.Duration = TimeSpan.FromSeconds(ArcDuration);
        travel.DelayTime = TimeSpan.FromSeconds(ArcDelay);

        for (int i = 0; i <= KeyFrames; i++)
        {
            float t = i / (float)KeyFrames;
            double angle = Math.PI + Math.PI * t;
            float px = (float)(ArcCentreX + ArcRadiusX * Math.Cos(angle));
            float py = (float)(ArcCentreY + ArcRadiusY * Math.Sin(angle));
            travel.InsertKeyFrame(t, new Vector3(px - headSize / 2, py - headSize / 2, 0));
        }

        head.StartAnimation("Offset", travel);
        head.StartAnimation("Opacity", Ramp(compositor,
            (0, 0f),
            (ArcDelay, 0f),
            (ArcDelay + ArcDuration - 0.30, 1f),
            (ArcDelay + ArcDuration + 0.10, 0f)));

        // The trace the scan leaves behind.
        arc.StartAnimation("Opacity", Ramp(compositor,
            (0, 1f),
            (ArcSettle, 1f),
            (ArcSettle + 0.15, ArcResidual)));
    }

    /// <summary>
    /// Waits out the reveal, then fades the card and reports both moments. The card's own fade is a
    /// composition animation too, so the handover is the player arriving behind a card that is already
    /// dissolving rather than a cut between the two.
    /// </summary>
    private static async Task RunTimeline(Compositor compositor, ContainerVisual root,
                                          Action revealing, Action finished)
    {
        await Task.Delay(TimeSpan.FromSeconds(RevealAt));

        try
        {
            revealing();
        }
        catch (Exception)
        {
            // Bringing the player up is the caller's business; the card still has to go away.
        }

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromSeconds(FadeDuration);
        fade.InsertKeyFrame(0f, 1f);
        fade.InsertKeyFrame(1f, 0f);
        root.StartAnimation("Opacity", fade);

        await Task.Delay(TimeSpan.FromSeconds(FadeDuration));

        try
        {
            finished();
        }
        catch (Exception)
        {
            // Nothing left to do about it.
        }
    }

    /// <summary>
    /// An animation over one value, given the moments it should hold or reach each of them. Times are
    /// absolute seconds from the start of the reveal, which is what makes the whole timeline readable in
    /// one place instead of being spread across delays and durations.
    /// </summary>
    private static ScalarKeyFrameAnimation Ramp(Compositor compositor, params (double Time, float Value)[] points)
    {
        double total = points[^1].Time;

        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromSeconds(total);

        foreach ((double time, float value) in points)
        {
            animation.InsertKeyFrame((float)(time / total), value);
        }

        return animation;
    }

    private static float Ease(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
