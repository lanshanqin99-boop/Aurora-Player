using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Aurora;

/// <summary>
/// A frameless, always-on-top, non-activating overlay window that hosts the player.
/// The window only owns shell concerns: size, placement, z-order, native dragging
/// and binding the controls to <see cref="PlaybackService"/>.
///
/// The window is intentionally a plain borderless shell: no region/DWM rounding.
/// All visual rounding and material comes from the root Border in MainWindow.xaml,
/// composited by XAML.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Logical (unscaled) window size: 400 x 200 at 100% DPI.</summary>
    private const int WindowWidth = 400;
    private const int WindowHeight = 200;

    private const int GwlExStyle = -20;

    /// <summary>Owner-window slot (what Win32 calls the parent slot on a top-level window).</summary>
    private const int GwlpHwndParent = -8;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;

    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_DONOTROUND — stop DWM adding a rounded edge of its own.</summary>
    private const int DwmcpDoNotRound = 1;

    private readonly IntPtr _hWnd;
    private readonly SubclassProc _subclassProc;
    private readonly PlaybackService _playback = new();
    private DispatcherQueueTimer? _progressTimer;
    private PlaylistWindow? _playlistWindow;

    /// <summary>
    /// The system media session, which is what a headset's buttons actually press. Null if the session
    /// could not be taken, in which case the app behaves as it did before and the window's own buttons
    /// are all there is.
    /// </summary>
    private MediaControls? _mediaControls;

    /// <summary>True while the user drags the seek bar, which pauses position syncing.</summary>
    private bool _isSeeking;

    /// <summary>
    /// True while an EndReached advance is being processed, so a second callback from
    /// libvlc cannot start a parallel load of the same transition.
    /// </summary>
    private bool _advancing;

    /// <summary>
    /// Source of cover-driven palettes. Reading and analysing a cover never happens on
    /// the UI thread, and results are cached per file.
    /// </summary>
    private readonly CoverPaletteService _palettes = new();

    /// <summary>File whose palette the shell is currently showing, to ignore stale results.</summary>
    private string? _ambientPath;

    /// <summary>The endless gradient-direction animation, created once.</summary>
    private Storyboard? _ambientDrift;

    /// <summary>The endless highlight drift, created once.</summary>
    private Storyboard? _highlightDrift;

    /// <summary>Last progress fraction the highlight was moved to, so it only animates on a change.</summary>
    private double _highlightFraction = -1;

    /// <summary>
    /// The palette currently on screen. Kept so a queue window that is opened later can be given the
    /// same colours immediately instead of waiting for the next track change.
    /// </summary>
    private AmbientPalette? _appliedPalette;

    /// <summary>
    /// The last album palette, kept so the shell can go back to it after the aurora has stood in. It
    /// is the colour the cover provided, and survives the default surface replacing it on screen.
    /// </summary>
    private AmbientPalette? _albumPalette;

    /// <summary>The aurora's own palette, resolved once from the same service the covers use.</summary>
    private AmbientPalette? _auroraPalette;

    /// <summary>True once the aurora's palette has been asked for, so a missing picture is asked once.</summary>
    private bool _auroraPaletteResolved;

    /// <summary>False until the idle surface has been shown once, so the fades only run on a change.</summary>
    private bool _idleSurfaceShown;

    /// <summary>True while the surface is being decided, so the palette switch cannot re-enter it.</summary>
    private bool _updatingIdleSurface;

    /// <summary>
    /// Height of the queue window while it is open, in effective pixels, or 0 while it is not. The
    /// photograph is scaled to cover the pair, so the player cannot place its own share of it without
    /// knowing how far down the pair reaches.
    /// </summary>
    private double _queueSurfaceHeight;

    /// <summary>
    /// The band of the idle photograph last written to this window. It is kept so an unchanged band is
    /// not written again: most of the times this is measured, nothing about it has moved.
    /// </summary>
    private IdleCanvas.Band? _appliedPlayerBand;

    /// <summary>
    /// The palette the current track's cover gave, or null while it is still being read.
    /// </summary>
    private AmbientPalette? _trackPalette;

    /// <summary>
    /// True once the current track's cover has been read, whatever it gave. This is what separates
    /// "there is no colour" from "the colour has not arrived yet", and the two need separating: the
    /// first is a black surface the photograph should stand in for, and the second is a colour on its
    /// way that the photograph must stay out of the way of.
    /// </summary>
    private bool _trackPaletteResolved;

    /// <summary>Native handle of this window, used to own file/folder dialogs.</summary>
    internal IntPtr Handle => _hWnd;

    public MainWindow(bool holdForReveal = false)
    {
        InitializeComponent();

        _holdForReveal = holdForReveal;
        _hWnd = WindowNative.GetWindowHandle(this);
        _subclassProc = OnWindowMessage;

        // Host backdrop that keeps the window surface fully transparent, so the
        // shell Border in MainWindow.xaml is the only thing that paints.
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        ConfigureWindow();
        HookPlayback();

        // A window that opens with nothing playing is idle from its first frame; this is what puts
        // the aurora surface up without waiting for a state change that may never come.
        UpdateIdleSurface();

        // The shell has no size at this point — it reports 0x0 until XAML has laid it out — and the
        // photograph is positioned from that size, so its band is measured whenever the surface
        // changes instead of once here, where it would be measured as nothing.
        Shell.SizeChanged += (_, _) => UpdateIdleGeometry();

        // The system media session, so a headset's own buttons reach the transport they mean. Its
        // failure is not the window's: without it the app still plays, it is simply unreachable from
        // outside.
        try
        {
            _mediaControls = new MediaControls(DispatcherQueue, ResumePlayback, PausePlayback, StepNext, StepPrevious);
        }
        catch (Exception)
        {
            _mediaControls = null;
        }

        // The session has to be told where things stand once it exists: the state was already pushed
        // while it was still being created, so without this it would sit at "opened" until something
        // happened to start playing.
        RefreshTransportState();
    }

    // ---- Visual language ---------------------------------------------------

    // ---- Ambient background (cover-driven) ---------------------------------

    /// <summary>
    /// Resolves the current track's palette off the UI thread and fades the shell to it.
    /// A track without a usable cover resolves to <see cref="AmbientPalette.Black"/>, so
    /// the background returns to pure black rather than keeping the previous album's
    /// colour.
    /// </summary>
    private void RefreshAmbientAsync(AudioTrack? track)
    {
        string? path = track?.FilePath;

        // Same file: the shell already shows its palette.
        if (string.Equals(path, _ambientPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ambientPath = path;

        // A different file means its cover has not been read yet, which is a state of its own rather
        // than the same as "read, and it gave nothing". Collapsing them is what put a black window over
        // every track whose cover was still on its way — and this library is mostly files with no cover
        // at all, so the two cases have to be told apart for the surface to be right on either.
        _trackPalette = null;
        _trackPaletteResolved = false;

        if (path is null)
        {
            _trackPalette = AmbientPalette.Black;
            _trackPaletteResolved = true;

            // No track at all, so the surface is the photograph and it supplies the colours; see the
            // cover case below for why the black is not applied.
            UpdateIdleSurface();
            return;
        }

        _ = ResolveAsync(path);

        async System.Threading.Tasks.Task ResolveAsync(string filePath)
        {
            AmbientPalette palette;
            try
            {
                palette = await _palettes.GetPaletteAsync(filePath);
            }
            catch (Exception)
            {
                // Any failure — no cover, damaged image, undecodable format — is black.
                palette = AmbientPalette.Black;
            }

            // The track may have moved on while the cover was being read.
            if (!string.Equals(filePath, _ambientPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Read, whatever it gave. Marked before the colours are applied, because applying them is
            // what asks the surface question again.
            _trackPalette = palette;
            _trackPaletteResolved = true;

            // A track whose cover gives nothing is not a surface. The photograph stands in for it and
            // that surface brings its own colours, so the black is not applied at all: applying it
            // would only have to be undone a moment later, and undoing a colour mid-fade is how a black
            // flash gets in front of the picture. The surface is asked for instead, and it is what puts
            // the right colours there.
            if (!UseAuroraSurface())
            {
                ApplyPalette(palette);
            }
            else
            {
                UpdateIdleSurface();
            }
        }
    }

    /// <summary>
    /// Fades the shell's three gradient stops to the palette. The gradient is the
    /// transition: animating the stops means the colour changes smoothly instead of
    /// snapping when the track changes.
    /// </summary>
    private void ApplyPalette(AmbientPalette palette)
    {
        try
        {
            AmbientGradient.FadeTo(AmbientStopPrimary, AmbientStopSecondary, AmbientStopAccent, palette);
        }
        catch (Exception)
        {
            // Animation is cosmetic: fall back to setting the colours directly.
            AmbientGradient.SetStops(AmbientStopPrimary, AmbientStopSecondary, AmbientStopAccent, palette);
        }

        StartAmbientDrift();

        // The highlight is derived from the very same palette, so it belongs to the same track as
        // the background it sits on.
        UpdateHighlight(palette);
        StartHighlightBreathe();

        // The queue window shares this palette: it is handed the same instance rather than resolving
        // one of its own, so the two surfaces cannot disagree.
        _appliedPalette = palette;

        // Remembered as the album colour, so the shell can return to it after the default surface has
        // stood in. A palette that yielded nothing is not worth returning to.
        const byte BlackLevel = 8;
        if (palette.Background.R > BlackLevel
            || palette.Background.G > BlackLevel
            || palette.Background.B > BlackLevel)
        {
            _albumPalette = palette;
        }

        _playlistWindow?.ApplyPalette(palette, animate: true);

        // A colour arriving is exactly when the surface can change: this is the track's colour landing,
        // and the photograph has to be out of its way by then. Without this the photograph stayed up
        // over a track whose colour had already been resolved, and only went when some other event
        // asked the question again.
        UpdateIdleSurface();
    }

    /// <summary>
    /// Sets the shell's colours with no fade. Used where the surface itself is changing over — the
    /// aurora coming up or the album colour taking its place — because there is no meaningful colour
    /// to transition from there, and animating would only flash through black.
    /// </summary>
    private void ApplyPaletteNow(AmbientPalette palette)
    {
        try
        {
            AmbientGradient.SetStops(AmbientStopPrimary, AmbientStopSecondary, AmbientStopAccent, palette);
        }
        catch (Exception)
        {
            // Cosmetic only.
        }

        StartAmbientDrift();
        UpdateHighlight(palette);
        StartHighlightBreathe();

        _appliedPalette = palette;
        _playlistWindow?.ApplyPalette(palette, animate: false);

        // See ApplyPalette: the surface is re-decided whenever the colours change.
        UpdateIdleSurface();
    }

    /// <summary>
    /// Shows or hides the idle aurora surface. It belongs to the one state where the player holds no
    /// track at all: the window before the first track is started, and again if the queue is emptied.
    /// The moment a track is loaded the photograph goes, whether or not that track's cover gives a
    /// colour — a track with no cover is a black surface, not the aurora, because the surface is
    /// meant to be the music's and the aurora is not the music's.
    /// </summary>
    private void UpdateIdleSurface()
    {
        // The surface is asked for again from inside the palette switch, because a colour arriving is
        // exactly when the answer can change. That re-entry is a no-op: this call is already deciding.
        if (_updatingIdleSurface)
        {
            return;
        }

        _updatingIdleSurface = true;
        try
        {
            bool idle = UseAuroraSurface();

            if (idle != _idleSurfaceShown)
            {
                if (idle && !EnsureIdleImage())
                {
                    // No picture on disk, so nothing is replacing the album colour: a colourless surface
                    // is the honest fallback, rather than the last track's colour standing there.
                    ApplyPalette(AmbientPalette.Black);
                    return;
                }

                // The fade is what keeps the two surfaces from cutting into each other: the aurora
                // withdraws as the album colour arrives, and comes back when the track lets go.
                var fade = new Duration(TimeSpan.FromMilliseconds(650));
                var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

                var storyboard = new Storyboard();

                IdleSurface.AddFade(storyboard, IdleBackground, idle ? IdleSurface.Opacity : 0.0, fade, ease);
                IdleSurface.AddFade(storyboard, IdleScrim, idle ? 1.0 : 0.0, fade, ease);

                storyboard.Begin();

                // Recorded before the colours are switched, so a re-entry from there sees the surface
                // that is already on its way in rather than the one it is replacing.
                _idleSurfaceShown = idle;

                // The queue stands on the same picture, so it is told which surface is up in the same
                // breath. The two windows are one surface split across two windows, not two windows that
                // happen to look alike.
                _playlistWindow?.ShowAuroraSurface(idle, animate: true);

                // And its share of the picture is measured again, because the surface it is measured
                // against has just changed height.
                UpdateIdleGeometry();

                // The colours follow the surface, not the other way round: the shell and the bar take
                // the aurora's own palette while the photograph is up, and go back to the album's when
                // the cover provides one.
                SwitchSurfacePalette(idle);
            }
            else if (idle && _appliedPalette != _auroraPalette)
            {
                // Already standing on the photograph, and its colours are put there even so. This call
                // can be the one that follows a track whose cover gave nothing, and the surface's own
                // colours are the only ones that will ever be put there for it: without this, the black
                // that a colourless track asks for is what stays behind the picture.
                //
                // The guard is what keeps it from being work for its own sake. It only runs while the
                // photograph's palette is not the one already on screen, so the case above is handled
                // and the ordinary call — the surface has not changed and already wears its own colours
                // — does nothing at all. The comparison is by value: a palette is a record struct, so
                // two that describe the same colours are the same palette.
                //
                // It is deliberately not done for the album surface. There the colours arrive with their
                // own fade, and re-applying them here would cut that fade short.
                SwitchSurfacePalette(true);
            }
        }
        catch (Exception)
        {
            // Cosmetic only: a missing idle surface leaves the album colour in place.
        }
        finally
        {
            _updatingIdleSurface = false;
        }
    }

    /// <summary>
    /// Paints the aurora once, from the file itself rather than from a package URI: the image ships
    /// next to the executable, and reading it by path is what makes it load whether the app is
    /// running from a build output or from its installed package. The locations it looks in, and the
    /// order it looks in them, are shared with the queue — both windows stand on the same picture.
    /// </summary>
    private bool EnsureIdleImage()
    {
        if (IdleBackground.Source is not null)
        {
            return true;
        }

        foreach (string path in IdleSurface.ImageCandidates())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                IdleBackground.Source = new BitmapImage(new Uri(path));
                return true;
            }
            catch (Exception)
            {
                // Try the next location.
            }
        }

        return false;
    }

    /// <summary>
    /// Places both windows' shares of the idle photograph, and hands the queue its own. The band is
    /// measured here, once, and pushed to the queue: the picture belongs to the pair, so a window that
    /// measured its own share could disagree with the other about where the picture falls.
    ///
    /// The measurement comes from the window's client size rather than the shell's laid-out size. The
    /// two differ by a rounding step — measured: a 400x200 client area lays the shell out at 199 tall
    /// — and the queue is placed below the player using the client size, so measuring the picture from
    /// the shell's would put the picture one pixel out of step with the window it is drawn in, at the
    /// one place where that shows: the gap between the two halves.
    /// </summary>
    private void UpdateIdleGeometry()
    {
        try
        {
            double scale = GetDpiScale();
            if (scale <= 0)
            {
                return;
            }

            SizeInt32 client = AppWindow.ClientSize;
            double width = client.Width / scale;
            double height = client.Height / scale;

            if (width < 1 || height < 1)
            {
                return;
            }

            IdleCanvas.Band playerBand = IdleCanvas.ForPlayer(width, height, _queueSurfaceHeight);

            // Only written when it differs. The photograph is measured against the pair's height, so a
            // queue that changes height rescales it — but most calls here change nothing at all, and a
            // property written with the value it already has is work asked of the layout for no reason.
            if (_appliedPlayerBand is not IdleCanvas.Band applied || applied != playerBand)
            {
                _appliedPlayerBand = playerBand;
                IdleSurface.Apply(IdleBackground, playerBand);
            }

            _playlistWindow?.ApplyIdleBand(IdleCanvas.ForQueue(width, height, _queueSurfaceHeight));
        }
        catch (Exception)
        {
            // Cosmetic only: the surface keeps whatever band it already had.
        }
    }

    /// <summary>
    /// Measures and places the idle photograph again. The queue asks for this when it opens, because
    /// its share has to be placed against a player that is already laid out.
    /// </summary>
    internal void MeasureIdleSurface() => UpdateIdleGeometry();

    /// <summary>
    /// Tells the player how tall the queue is. The photograph is scaled to cover the pair, so the
    /// player cannot measure its own share of it without knowing how far down the pair reaches: a
    /// queue that grew would otherwise push its last rows past the picture's end, which is the black
    /// block that appeared under the list. Called with zero once the queue is gone.
    /// </summary>
    internal void NotifyQueueSurface(double queueHeight)
    {
        _queueSurfaceHeight = Math.Max(0, queueHeight);

        // The queue's band is handed back from here, so this is also what re-measures it.
        UpdateIdleGeometry();
    }

    /// <summary>DPI scale of the monitor the player is on; the queue sits under it, so they share one.</summary>
    internal double DpiScale => GetDpiScale();

    /// <summary>
    /// Whether the window should be showing the aurora rather than the album's own colour. Three cases,
    /// and the middle one is the whole point:
    ///
    /// 1. Nothing is loaded — the photograph, because there is no album colour at all.
    /// 2. A track is loaded and its cover has not been read yet — <em>not</em> the photograph. Whatever
    ///    were shown here would have to be taken back a moment later, and showing the photograph is
    ///    what made a track's own colour look like it never arrived.
    /// 3. The cover has been read — its colour if it had one, and the photograph if it did not, because
    ///    a black window is not a surface. Most of a spoken-word library has no cover, so this is the
    ///    case that decides what the window looks like most of the time.
    ///
    /// It still does not consult the palette that happens to be applied. That is a lagging quantity,
    /// and reading it here is what broke cover colouring in the first place.
    /// </summary>
    private bool UseAuroraSurface()
    {
        if (!_playback.IsCurrentTrackLoaded)
        {
            return true;
        }

        if (!_trackPaletteResolved)
        {
            return false;
        }

        return HasNoColour(_trackPalette);
    }

    /// <summary>
    /// True when a palette carries no colour to show: the palette that means "the cover gave nothing",
    /// and equally the absence of any palette at all — nothing has been applied yet, so the aurora has
    /// nothing to displace.
    ///
    /// Folding those two into one case is what the aurora's arrival depends on. The photograph's own
    /// palette is resolved off the UI thread and arrives after the first frame, and while it is on its
    /// way the window has no palette at all; a test that accepted only an already-applied black
    /// palette threw that arrival away, so the surface kept the defaults it starts with for as long as
    /// it stayed idle. Those defaults are a black background and a white bar.
    /// </summary>
    private static bool HasNoColour(AmbientPalette? palette)
    {
        // Measured in plain channel values: at or below this the palette is telling us there was no
        // colour to take, which is AmbientPalette.Black.
        const byte BlackLevel = 8;

        Color background = palette?.Background ?? AmbientPalette.Black.Background;

        return background.R <= BlackLevel
            && background.G <= BlackLevel
            && background.B <= BlackLevel;
    }

    /// <summary>
    /// Puts the colours that belong to the surface now on screen: the aurora's own palette while the
    /// photograph is up, the album's otherwise. The album palette is the one already applied, so
    /// coming back needs nothing new; leaving needs the aurora resolved once.
    /// </summary>
    private void SwitchSurfacePalette(bool aurora)
    {
        if (!aurora)
        {
            if (_albumPalette is AmbientPalette album)
            {
                ApplyPaletteNow(album);
            }

            return;
        }

        AmbientPalette? auroraPalette = ResolveAuroraPalette();
        if (auroraPalette is AmbientPalette resolved)
        {
            ApplyPaletteNow(resolved);
        }
    }

    /// <summary>
    /// The aurora's palette, resolved once and kept. It comes from the very same service and the very
    /// same extractor the covers use, so the default surface is coloured by the same rules as any
    /// album — nothing about the colour pipeline changes, only which picture it is pointed at.
    ///
    /// The first call only starts the work off the UI thread. Resolving a palette is asynchronous, and
    /// waiting for it from the UI thread — which is the thread that would have to run its
    /// continuation — would deadlock.
    /// </summary>
    private AmbientPalette? ResolveAuroraPalette()
    {
        if (!_auroraPaletteResolved)
        {
            _auroraPaletteResolved = true;

            string? path = IdleSurface.ImageCandidates().FirstOrDefault(File.Exists);
            if (path is not null)
            {
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try
                    {
                        // The picture file goes through the same service a cover does; it is only
                        // read from where a picture lives instead of from inside an audio file.
                        AmbientPalette palette = await _palettes.GetImagePaletteAsync(path);
                        RunOnUi(() =>
                        {
                            _auroraPalette = palette;

                            // If the aurora is what should be on screen, take the colour now; otherwise
                            // the next switch will use it. Nothing is playing at this point in the
                            // normal case, so this is what gives the idle surface its own colours
                            // instead of leaving the window on the black-and-white defaults it starts
                            // with.
                            if (UseAuroraSurface())
                            {
                                ApplyPaletteNow(palette);
                            }
                        });
                    }
                    catch (Exception)
                    {
                        // No colour from the default picture: the plain surface stands on its own.
                    }
                });
            }
        }

        return _auroraPalette;
    }

    /// <summary>
    /// Recolours the soft highlight from the palette. The colour is the accent mixed toward the
    /// background's own hue, carried at a low alpha: a wash over the large field, not a second
    /// colour competing with it.
    ///
    /// A background judged black has no colour to take a highlight from — that is the coverless
    /// case and a genuinely black sleeve — so the highlight is white there, which is the only light
    /// that reads on black.
    /// </summary>
    private void UpdateHighlight(AmbientPalette palette)
    {
        try
        {
            // A palette that carries no colour is the one case where the light has to be white: there
            // is nothing in it to tint a highlight with, and white is the only light that reads on a
            // surface that dark.
            bool black = HasNoColour(palette);

            Color light = black
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : AmbientGradient.Mix(palette.Background, palette.Accent, 0.55);

            HighlightCore.Color = Color.FromArgb(0x46, light.R, light.G, light.B);
            HighlightMid.Color = Color.FromArgb(0x14, light.R, light.G, light.B);

            // The bar is part of the same palette, so it is coloured in the same call and can never
            // disagree with the surface it sits on. The light is handed over rather than derived a
            // second time, so the bar and the highlight cannot drift apart either.
            UpdateProgressFill(palette, black, light);
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// Colours the progress bar from the palette on screen. The played part runs from the accent at
    /// the start of the fill to that accent lifted at the head, so the played length reads as one
    /// light brightening towards the play position; the unplayed remainder is the same light at a low
    /// alpha, so the bar is one object in the track's colours rather than a coloured strip inside a
    /// white bar. The gradient is painted by XAML — this only sets its two stops, with no glow, halo
    /// or light trailing off either end.
    ///
    /// A palette that carries no colour has no fill and no remainder to tint, so the bar falls back to
    /// white there, which is the only light that reads on black.
    /// </summary>
    private void UpdateProgressFill(AmbientPalette palette, bool black, Color light)
    {
        Color fill = black ? Color.FromArgb(0xFF, 0xE8, 0xE8, 0xE8) : palette.Accent;
        Color head = black
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : AmbientGradient.Mix(palette.Accent, Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), 0.55);

        ProgressFillTail.Color = fill;
        ProgressFillHead.Color = head;

        // The same alpha the bar's remainder has always been drawn at, so the change is the colour of
        // that remainder and not its weight.
        ProgressTrack.Background = new SolidColorBrush(Color.FromArgb(0x33, light.R, light.G, light.B));
    }

    /// <summary>
    /// Moves the highlight so it links the progress bar to the transport controls: its centre sits
    /// on the bar's play position horizontally and on the play button's height vertically, so the
    /// light reads as coming from where the track currently is and reaching up over the buttons.
    ///
    /// The travel is eased over most of a second rather than snapped, so it is continuous motion
    /// instead of a band stepping across the window; and the retarget only happens when the position
    /// has actually moved a percent, so a slow track costs almost nothing.
    /// </summary>
    private void MoveHighlightTo(double fraction, bool immediate = false)
    {
        try
        {
            if (Shell.ActualWidth <= 0 || Shell.ActualHeight <= 0)
            {
                return;
            }

            // Only follow a real change, so an idle bar does not restart an animation four times a
            // second with the same destination. A drag is exempt: it must track the pointer exactly.
            if (!immediate && Math.Abs(fraction - _highlightFraction) < 0.01 && _highlightFraction >= 0)
            {
                return;
            }

            _highlightFraction = fraction;

            Rect bar = SeekArea.TransformToVisual(Shell)
                .TransformBounds(new Rect(0, 0, SeekArea.ActualWidth, SeekArea.ActualHeight));
            Rect button = PlayPauseButton.TransformToVisual(Shell)
                .TransformBounds(new Rect(0, 0, PlayPauseButton.ActualWidth, PlayPauseButton.ActualHeight));

            double x = (bar.X + (bar.Width * Math.Clamp(fraction, 0, 1))) / Shell.ActualWidth;

            // The light reaches from the bar up across the transport row, so it is centred between
            // them rather than sitting on the tiny bar itself.
            double y = ((bar.Y + button.Y + (button.Height / 2)) / 2) / Shell.ActualHeight;

            var target = new Point(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
            AnimateHighlightTo(target, immediate);
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// Slides the highlight's centre to a point. Playing eases over most of a second so the wash
    /// drifts rather than steps; a drag sets the point outright, because the light has to be under
    /// the pointer while the pointer is moving.
    /// </summary>
    private void AnimateHighlightTo(Point target, bool immediate)
    {
        var duration = new Duration(immediate ? TimeSpan.Zero : TimeSpan.FromMilliseconds(850));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        var storyboard = new Storyboard();

        foreach (string property in new[] { "Center", "GradientOrigin" })
        {
            var slide = new PointAnimation
            {
                To = target,
                Duration = duration,
                EasingFunction = immediate ? null : ease,
            };

            Storyboard.SetTarget(slide, HighlightBrush);
            Storyboard.SetTargetProperty(slide, property);
            storyboard.Children.Add(slide);
        }

        storyboard.Begin();
    }

    /// <summary>
    /// The highlight's breathing: never fully off, never strong enough to touch readability. It is
    /// the only part of the highlight that runs on its own — where it sits is driven by the progress
    /// bar, which is why nothing here loops over its position.
    /// </summary>
    private void StartHighlightBreathe()
    {
        if (_highlightDrift is not null)
        {
            return;
        }

        try
        {
            var breathe = new DoubleAnimation
            {
                From = 0.5,
                To = 0.85,
                Duration = new Duration(TimeSpan.FromSeconds(11)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            };

            Storyboard.SetTarget(breathe, AmbientHighlight);
            Storyboard.SetTargetProperty(breathe, "Opacity");

            _highlightDrift = new Storyboard();
            _highlightDrift.Children.Add(breathe);
            _highlightDrift.Begin();
        }
        catch (Exception)
        {
            // A static highlight is an acceptable outcome.
            _highlightDrift = null;
        }
    }

    /// <summary>
    /// Keeps the gradient's direction turning, slowly and forever. This is the "flow" of
    /// the ambient colour: because the three stops sit at different points along the
    /// gradient, rotating it sweeps the album's colour across the surface without any
    /// effect, blur or draw call — the brush is re-evaluated by the render thread.
    ///
    /// It is started once and restarted only when the window's storyboards are cleared.
    /// </summary>
    private void StartAmbientDrift()
    {
        if (_ambientDrift is not null)
        {
            return;
        }

        try
        {
            const double period = 26;

            // A closed loop of start points around an off-centre anchor. The easing makes
            // the drift speed up and slow down, so it never reads as a mechanical spin.
            var drift = new PointAnimationUsingKeyFrames
            {
                Duration = new Duration(TimeSpan.FromSeconds(period)),
                RepeatBehavior = RepeatBehavior.Forever,
            };

            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            drift.KeyFrames.Add(new EasingPointKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = new Point(0.00, 0.00),
                EasingFunction = ease,
            });
            drift.KeyFrames.Add(new EasingPointKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period / 4)),
                Value = new Point(1.00, 0.24),
                EasingFunction = ease,
            });
            drift.KeyFrames.Add(new EasingPointKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period / 2)),
                Value = new Point(1.00, 1.00),
                EasingFunction = ease,
            });
            drift.KeyFrames.Add(new EasingPointKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period * 3 / 4)),
                Value = new Point(0.00, 0.76),
                EasingFunction = ease,
            });
            drift.KeyFrames.Add(new EasingPointKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(period)),
                Value = new Point(0.00, 0.00),
                EasingFunction = ease,
            });

            Storyboard.SetTarget(drift, AmbientBrush);
            Storyboard.SetTargetProperty(drift, "StartPoint");

            _ambientDrift = new Storyboard();
            _ambientDrift.Children.Add(drift);
            _ambientDrift.Begin();
        }
        catch (Exception)
        {
            // A static gradient is an acceptable outcome.
            _ambientDrift = null;
        }
    }

    // ---- Playback wiring ---------------------------------------------------

    private void HookPlayback()
    {
        _playback.StateChanged += (_, _) => RunOnUi(() =>
        {
            RefreshTransportState();

            // Starting playback is the other moment the ambient colour is allowed to load: a
            // queue whose first entry was merely adopted becomes a track the user is listening
            // to, and it raises StateChanged rather than PlaylistChanged. The call is idempotent
            // — RefreshAmbientAsync returns immediately for the file it is already showing.
            if (_playback.IsCurrentTrackLoaded)
            {
                RefreshAmbientAsync(_playback.CurrentTrack);
            }

            // The idle surface is the counterpart of that: it belongs to the moments when the player
            // holds no track at all, which is exactly what this event also reports.
            UpdateIdleSurface();
        });
        _playback.PlaylistChanged += (_, _) => RunOnUi(() =>
        {
            RefreshTrackInfo();

            // Emptying the queue takes the track away entirely, which is the one thing that brings the
            // idle surface back; nothing else in this event changes the surface.
            UpdateIdleSurface();
            // The queue window mirrors the service, so the highlight follows every
            // track change, including the automatic advance when a track ends.
            _playlistWindow?.RefreshFromService();

            // The ambient background follows the current track's cover art — but only once
            // that track has actually been started. Adding files to an empty queue adopts
            // the first entry as current without playing it, and reading a cover at that
            // moment would put file I/O and image decoding on the add path for a track the
            // user has not asked to hear. IsCurrentTrackLoaded is what separates the two.
            if (_playback.IsCurrentTrackLoaded)
            {
                RefreshAmbientAsync(_playback.CurrentTrack);
            }

            // Tracks whose tags carried no duration get one from the media parser,
            // which then raises PlaylistChanged again and repaints the queue.
            ResolveMissingDurations();
        });

        // EndReached fires on a libvlc thread and must not call back into the player
        // synchronously, so the advance is queued to the UI thread. The guard matters:
        // libvlc can deliver more than one of these around a Stop/Play transition, and
        // each one used to start another load, which is what turned a single Play into a
        // runaway loop of Media replacements.
        _playback.EndReached += (_, _) => RunOnUi(() =>
        {
            if (_advancing)
            {
                return;
            }

            _advancing = true;
            try
            {
                // Sequential playback: the next track, wrapping at the end of the list.
                _playback.Next();

                RefreshTrackInfo();
                RefreshTransportState();
                RefreshProgress();
                _playlistWindow?.RefreshFromService();
            }
            finally
            {
                _advancing = false;
            }
        });

        _progressTimer = DispatcherQueue.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        _progressTimer.IsRepeating = true;
        _progressTimer.Tick += (_, _) => RefreshProgress();
        _progressTimer.Start();

        Closed += (_, _) =>
        {
            _progressTimer?.Stop();

            // Released before the player goes, so the session cannot outlive the sound it describes.
            _mediaControls?.Dispose();
            _mediaControls = null;

            _playlistWindow?.Close();
            _playback.Dispose();
        };

        RefreshTrackInfo();
        RefreshTransportState();
        RefreshProgress();

        // Show the starting track's ambient colour as soon as the window is up. Nothing is
        // loaded yet, so this reads no cover: the first colour arrives when playback starts.
        if (_playback.IsCurrentTrackLoaded)
        {
            RefreshAmbientAsync(_playback.CurrentTrack);
        }
    }

    /// <summary>
    /// Fire-and-forget duration backfill. Failures are swallowed on purpose: a track
    /// with no readable duration simply keeps showing "--:--".
    /// </summary>
    private void ResolveMissingDurations()
    {
        _ = RunDurationsAsync();

        async System.Threading.Tasks.Task RunDurationsAsync()
        {
            try
            {
                await _playback.ResolveMissingDurationsAsync();
            }
            catch (Exception)
            {
                // Durations are cosmetic; never let this surface to the user.
            }
        }
    }

    private void RunOnUi(Action action)
    {        if (DispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => action());
        }
    }

    /// <summary>Updates the title/artist readout from the current track.</summary>
    private void RefreshTrackInfo()
    {
        AudioTrack? track = _playback.CurrentTrack;
        if (track is null)
        {
            // Nothing playing: the artist line stays empty rather than announcing a state. There is
            // no placeholder text anywhere in the header.
            TitleText.Text = "Aurora";
            ArtistText.Text = string.Empty;
            return;
        }

        // The header runs the same display filter as the queue, so a polluted tag or file name can
        // only ever show its readable prefix here as well.
        string title = DisplayRules.PlayerTitleFor(track.FilePath, track.Title);
        string artist = DisplayRules.PlayerArtistFor(track.FilePath, track.Artist);

        TitleText.Text = title;
        ArtistText.Text = artist.Length == 0
            ? $"{_playback.CurrentIndex + 1}/{_playback.Playlist.Count}"
            : $"{artist} · {_playback.CurrentIndex + 1}/{_playback.Playlist.Count}";

        // The same track, told to the system: this is what a headset's own display shows and what the
        // media keys use to decide that Aurora is the app they belong to.
        _mediaControls?.SetTrack(title, artist, track.FilePath);
    }

    /// <summary>Updates the play/pause glyph from the player state, and the system session with it.</summary>
    private void RefreshTransportState()
    {
        PlayPauseIcon.Glyph = _playback.IsPlaying ? "\uE769" : "\uE768";
        _mediaControls?.SetPlaying(_playback.IsPlaying);
    }

    /// <summary>Syncs the progress bar and time labels with the playback position.</summary>
    private void RefreshProgress()
    {
        TimeSpan duration = _playback.Duration;
        TimeSpan position = _playback.Position;

        UpdateText(DurationText, Format(duration), ref _durationText);
        UpdateText(PositionText, Format(position), ref _positionText);

        // The system panel's progress line, told the same position the window is showing.
        _mediaControls?.SetPosition(position, duration);

        if (_isSeeking || duration <= TimeSpan.Zero)
        {
            // While dragging, the fill follows the pointer, not the player.
            return;
        }

        SetProgressFraction(Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1));
    }

    // ---- Seek bar ----------------------------------------------------------

    private void SetProgressFraction(double fraction)
    {
        // The track Border has no fixed width, so size the fill against the seek area.
        double available = SeekArea.ActualWidth;
        ProgressFill.Width = Math.Max(0, available * fraction);

        // The background highlight follows the same position, so the light and the fill agree. While
        // the user is dragging, both are set at once: an eased follow would trail the pointer.
        MoveHighlightTo(fraction, immediate: _isSeeking);
    }

    private string? _durationText;
    private string? _positionText;

    /// <summary>
    /// Writes a label only when its text actually changes.
    ///
    /// The progress tick runs four times a second for as long as the window is open, and for most of
    /// that time the two labels hold the same strings they held on the previous tick: nothing is
    /// playing, or something is paused. Assigning them anyway is not free — a write to a TextBlock's
    /// text invalidates the layout of its row — so the value is compared first. What is displayed is
    /// identical, because the assignment it skips would have written the same characters.
    /// </summary>
    private static void UpdateText(TextBlock target, string text, ref string? last)
    {
        if (string.Equals(text, last, StringComparison.Ordinal))
        {
            return;
        }

        last = text;
        target.Text = text;
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}"
            : $"{value.Minutes}:{value.Seconds:D2}";

    // ---- Playback commands -------------------------------------------------

    private async void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        // A folder cannot be picked with a file dialog, so the user first chooses
        // which kind of source to open.
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "打开",
            PrimaryButtonText = "选择文件夹",
            SecondaryButtonText = "选择文件",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            OpenFolder();
        }
        else if (result == ContentDialogResult.Secondary)
        {
            OpenFiles();
        }
    }

    private void OpenFiles()
    {
        IReadOnlyList<string> files = PickerInterop.PickFiles(_hWnd, PlaybackService.SupportedFileExtensions);
        if (files.Count == 0)
        {
            return;
        }

        _playback.LoadFrom(files);

        // The first entry is ready and starts playing.
        _playback.PlayCurrent();

        RefreshTrackInfo();
        RefreshTransportState();
        RefreshProgress();
    }

    private void OpenFolder()
    {
        string? folder = PickerInterop.PickFolder(_hWnd);
        if (folder is null)
        {
            return;
        }

        _playback.LoadFrom(new[] { folder });

        // The first entry is ready and starts playing.
        _playback.PlayCurrent();

        RefreshTrackInfo();
        RefreshTransportState();
        RefreshProgress();
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e) => TogglePlayback();

    private void TogglePlayback()
    {
        _playback.TogglePlayPause();
        RefreshTransportState();
    }

    /// <summary>
    /// Resumes, if there is anything to resume. It is separate from the toggle because a headset sends
    /// <c>Play</c> and <c>Pause</c> as different buttons: a toggle would start playing on the way in
    /// and stop on the way out, but it would also do the opposite of what was asked whenever the two
    /// sides disagreed about the current state.
    /// </summary>
    private void ResumePlayback()
    {
        if (!_playback.IsPlaying)
        {
            TogglePlayback();
        }
    }

    /// <summary>Pauses, if anything is playing. See <see cref="ResumePlayback"/>.</summary>
    private void PausePlayback()
    {
        if (_playback.IsPlaying)
        {
            TogglePlayback();
        }
    }

    /// <summary>
    /// Shows or hides the queue window. It is shown without Activate() so opening it
    /// never depends on the player taking focus.
    ///
    /// The player is registered as the queue window's owner before it is shown, and the queue
    /// is then placed in the owner's z-order band. Both steps are required: ownership alone
    /// only orders two windows that are already in the same band, so without the second step
    /// the queue could fall behind the player or have another application raised between them.
    /// </summary>
    private void OnListToggleClicked(object sender, RoutedEventArgs? e)
    {
        if (_playlistWindow is null)
        {
            _playlistWindow = new PlaylistWindow(_playback, this);
            _playlistWindow.Closed += (_, _) =>
            {
                _playlistWindow = null;

                // The photograph covers the pair, so a queue that is gone has to come out of that
                // measurement as well as off the screen.
                NotifyQueueSurface(0);
            };

            // GWLP_HWNDPARENT is the owner slot, not the parent slot: unlike
            // SetParent, this moves no coordinates and leaves both windows top-level.
            SetWindowLongPtr(_playlistWindow.Handle, GwlpHwndParent, _hWnd);

            _playlistWindow.Activate();

            // After Activate, so the band is applied once the window is really on screen.
            _playlistWindow.JoinOwnerBand();

            // And with the palette that is already on screen, so a queue opened mid-track matches
            // the player immediately instead of waiting for the next track change.
            if (_appliedPalette is AmbientPalette palette)
            {
                _playlistWindow.ApplyPalette(palette, animate: false);
            }

            // The surface it opens onto too, for the same reason: the queue is a continuation of the
            // player, so it has to arrive already standing on whatever the player is standing on.
            _playlistWindow.ShowAuroraSurface(_idleSurfaceShown, animate: false);

            // And with its share of the picture placed, which needs a player that is already laid out.
            MeasureIdleSurface();

            return;
        }

        _playlistWindow.Close();
        _playlistWindow = null;
    }

    /// <summary>
    /// Opens the files Windows handed over — a double-click, "Open with", or Aurora being the default
    /// handler. They are added to whatever is already in the queue rather than replacing it, and the
    /// first of them starts playing, because opening a file is a request to hear it.
    /// </summary>
    internal void OpenFromAssociation(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _playback.AppendPaths(paths);

        // Adding orders the new files by title, so the one that was opened has to be found again
        // rather than assumed to be where it was handed over.
        int index = -1;
        for (int i = 0; i < _playback.Playlist.Count; i++)
        {
            if (string.Equals(_playback.Playlist[i].FilePath, paths[0], StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            _playback.SetCurrent(index);
            _playback.PlayCurrent();
        }

        RefreshTrackInfo();
        RefreshTransportState();
        RefreshProgress();
    }

    private void OnPreviousClicked(object sender, RoutedEventArgs e) => StepPrevious();

    private void OnNextClicked(object sender, RoutedEventArgs e) => StepNext();

    /// <summary>
    /// The previous/next the window's own buttons and a headset's buttons both end up in, so the two
    /// cannot drift apart: whatever the transport does after a step is done in one place.
    /// </summary>
    private void StepPrevious()
    {
        _playback.Previous();
        RefreshTrackInfo();
        RefreshTransportState();
        RefreshProgress();
    }

    private void StepNext()
    {
        _playback.Next();
        RefreshTrackInfo();
        RefreshTransportState();
        RefreshProgress();
    }

    // ---- Seek bar ----------------------------------------------------------

    /// <summary>
    /// True only when there is something to seek: a current track, a loaded media and a
    /// duration the player can actually map a position onto. Everything on the seek path
    /// returns early when this is false, so an idle player cannot be asked to seek.
    /// </summary>
    private bool CanSeek => _playback.CurrentTrack is not null && _playback.CanSeek;

    private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Nothing loaded: the bar neither captures the pointer nor moves.
        if (!CanSeek)
        {
            return;
        }

        _isSeeking = true;
        SeekArea.CapturePointer(e.Pointer);
        SeekToPointer(e);
    }

    private void OnSeekPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isSeeking && CanSeek)
        {
            SeekToPointer(e);
        }
    }

    private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;

        if (!CanSeek)
        {
            SeekArea.ReleasePointerCapture(e.Pointer);
            return;
        }

        SeekToPointer(e);
        SeekArea.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>
    /// Maps a pointer position on the bar to a fraction and seeks there. The guard is
    /// repeated here because this is reached from three different pointer handlers, and
    /// the track can disappear between the press and the release.
    /// </summary>
    private void SeekToPointer(PointerRoutedEventArgs e)
    {
        double width = SeekArea.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double x;
        try
        {
            x = e.GetCurrentPoint(SeekArea).Position.X;
        }
        catch (Exception)
        {
            // The pointer is gone: nothing to map.
            return;
        }

        double fraction = Math.Clamp(x / width, 0, 1);

        TimeSpan duration = _playback.Duration;
        if (!CanSeek || duration <= TimeSpan.Zero)
        {
            // Idle: only the visual fill follows the pointer.
            SetProgressFraction(fraction);
            return;
        }

        SetProgressFraction(fraction);

        var target = TimeSpan.FromSeconds(duration.TotalSeconds * fraction);
        UpdateText(PositionText, Format(target), ref _positionText);
        _playback.Seek(target);
    }

    // ---- Window shell ------------------------------------------------------

    /// <summary>
    /// Applies every window-level setting in one place: borderless presenter,
    /// fixed size, top-right placement, no-activate styles and the message hook.
    /// </summary>
    private void ConfigureWindow()
    {
        var appWindow = AppWindow;
        appWindow.Title = "Aurora";

        // The window's own icon, which the packaged executable cannot supply.
        WindowFrame.SetIcon(appWindow);

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // No chrome at all: no caption, no border, no resizing affordances.
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // The whole client area is content, so the system drag region is supplied by
        // SetTitleBar(DragRegion) below rather than by the window chrome.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);

        // The playlist rides under the player, so it follows every move.
        appWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange)
            {
                _playlistWindow?.FollowOwner();
            }
        };

        // Non-activating overlay: clicks and hovers never pull focus away from the
        // foreground app. WS_EX_TOOLWINDOW also keeps it out of Alt+Tab and the taskbar.
        ApplyExtendedStyles();

        // Clicks must not activate the window either, which covers the drag.
        SetWindowSubclass(_hWnd, _subclassProc, UIntPtr.Zero, UIntPtr.Zero);

        // The shell Border owns the 24px rounding; DWM must not add its own rounded
        // edge on top, which would otherwise leave a visible rim.
        int noDwmRounding = DwmcpDoNotRound;
        DwmSetWindowAttribute(
            _hWnd, DwmwaWindowCornerPreference, ref noDwmRounding, sizeof(int));

        // The frame goes before the client size is set, and the order matters. Removing
        // WS_BORDER/WS_CAPTION grows the client area, so sizing first and stripping the frame
        // afterwards left the window larger than designed — measured: a requested 400x200 client area
        // ended up 406x205. With the frame already gone, ResizeClient lands exactly on the design
        // size. That size is then final before the window is ever placed, so the first frame it
        // appears in cannot come with a resize.
        WindowFrame.RemoveBorder(_hWnd);

        // ResizeClient works in physical pixels; convert from the window's current DPI.
        double scale = GetDpiScale();
        int width = (int)Math.Round(WindowWidth * scale);
        int height = (int)Math.Round(WindowHeight * scale);
        appWindow.ResizeClient(new SizeInt32(width, height));

        ParkUntilFirstFrame(width);

        // Then re-assert topmost. The presenter was asked for it above, but the band is lost
        // along the way — measured: the window ends up with no WS_EX_TOPMOST at all, so the
        // player only *intends* to float. That matters beyond the player: an owned window is
        // kept directly above its owner inside the normal band and nothing else, so once the
        // player is not really topmost another application can be inserted between the player
        // and the queue. Putting the band back is what keeps the two together, because the
        // queue then rides above the player inside the same topmost band rather than standing
        // on its own.
        //
        // SWP_NOACTIVATE keeps the window's non-activating behaviour exactly as it was.
        SetWindowPos(
            _hWnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>HWND_TOPMOST — insert at the top of the topmost band.</summary>
    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Gap kept between the window and the work area's top-right corner.</summary>
    private const int WindowMargin = 24;

    /// <summary>
    /// Where the window belongs, held while it waits offscreen for its first painted frame.
    /// Null once it has been placed.
    /// </summary>
    private PointInt32? _pendingPlacement;

    /// <summary>True once the shell has been loaded and laid out.</summary>
    private bool _shellLoaded;

    /// <summary>
    /// True while the window is being built behind the startup card. It is painted and composited as
    /// usual — it simply is not moved on screen, and it stays transparent, until the card hands over.
    /// </summary>
    private readonly bool _holdForReveal;

    /// <summary>
    /// Keeps the window offscreen until it has actually been painted, then puts it in place.
    ///
    /// The window is shown by <c>App.OnLaunched</c> right after this constructor returns, but XAML
    /// has not laid anything out at that moment — measured: the shell still reported 0×0 when
    /// <c>Activate()</c> made the window visible, and the first laid-out frame arrived about 80 ms
    /// later. With the transparent backdrop those frames have nothing painted in them, so the
    /// desktop shows through where the black panel will be: that is the startup flicker.
    ///
    /// The remedy is a placement, not a delay. The window is moved completely outside the work area
    /// and stays shown there, which is what keeps it composited — XAML lays out and the compositor
    /// produces frames exactly as before, just somewhere nobody can see. It is moved into place on
    /// the first composited frame after <see cref="FrameworkElement.Loaded"/>, so the first frame
    /// ever drawn at that position already carries the finished surface. No timer, no sleep and no
    /// forced layout or redraw is involved: the trigger is the real frame, not elapsed time.
    /// </summary>
    private void ParkUntilFirstFrame(int width)
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        // Resolved now, while the window is still on the display it was created on. Once it has been
        // parked outside that display, asking which display it is on would answer a different one.
        _pendingPlacement = new PointInt32(
            workArea.X + workArea.Width - width - WindowMargin,
            workArea.Y + WindowMargin);

        // Fully outside the work area: shown and composited, but not on screen.
        AppWindow.Move(new PointInt32(workArea.X - width - WindowMargin, workArea.Y));

        // Both conditions are required. Loaded alone can still precede the first painted frame, and
        // a compositor tick alone can precede layout.
        Shell.Loaded += (_, _) => _shellLoaded = true;
        CompositionTarget.Rendering += OnFirstCompositedFrame;
    }

    /// <summary>
    /// Moves the window to its real position once it has something painted to show.
    ///
    /// While the startup card is up this is where the window would otherwise appear on screen behind it,
    /// so instead it stops here: painted, composited, correct, and off the display until
    /// <see cref="Reveal"/> is called.
    /// </summary>
    private void OnFirstCompositedFrame(object? sender, object e)
    {
        if (!_shellLoaded || _pendingPlacement is not PointInt32 target)
        {
            return;
        }

        // The handler goes either way; what differs is whether the placement happens now or at the
        // hand-over.
        CompositionTarget.Rendering -= OnFirstCompositedFrame;

        if (_holdForReveal)
        {
            // Left exactly where it is: painted and composited off the screen, and placed by the
            // card's hand-over — see Reveal.
            return;
        }

        // One placement only.
        _pendingPlacement = null;

        // Position and band together, deliberately: placing the window without naming the band it
        // belongs to would leave the two decisions in separate calls, and this window's band is not
        // whatever the default ordering happens to give it. SWP_NOACTIVATE keeps the non-activating
        // behaviour intact.
        SetWindowPos(_hWnd, HwndTopmost, target.X, target.Y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// Brings the player up as the startup card dissolves: into its place in one move, and up from
    /// transparent over the same quarter second the card is fading out, so the two overlap rather than
    /// replacing one another.
    ///
    /// The fade deliberately does <b>not</b> set the shell's opacity to zero first and then animate the
    /// visual. That is two owners for one value: XAML holds the property at zero, and any later pass that
    /// re-applies it cancels the animation and leaves the window invisible — measured: the player placed
    /// itself correctly and then could not be seen again, the process alive and only Task Manager able to
    /// end it. The animation's own first keyframe does the same job, and if it is ever dropped the
    /// property's value of one is what shows, so the window fails visible rather than hidden.
    /// </summary>
    internal void Reveal()
    {
        // Placement is attempted whenever there is somewhere to go, not only once a frame has been seen.
        // Those were two conditions and both had to hold, so a window that never observed its first
        // composited frame stayed parked off the screen for good.
        if (_pendingPlacement is not PointInt32 target)
        {
            return;
        }

        _pendingPlacement = null;

        SetWindowPos(_hWnd, HwndTopmost, target.X, target.Y, 0, 0, SwpNoSize | SwpNoActivate);

        // A composition fade rather than a XAML one: the window is already painted, so this costs the
        // UI thread nothing and cannot stutter at the exact moment the two windows overlap.
        Visual shell = ElementCompositionPreview.GetElementVisual(Shell);
        Compositor compositor = shell.Compositor;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromSeconds(0.28);
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);

        shell.StartAnimation("Opacity", fade);
    }

    private double GetDpiScale()
    {
        uint dpi = GetDpiForWindow(_hWnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private void ApplyExtendedStyles()
    {
        long styles = GetWindowLongPtr(_hWnd, GwlExStyle).ToInt64();
        styles |= WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(_hWnd, GwlExStyle, new IntPtr(styles));
    }

    /// <summary>
    /// Window hook. Clicks are answered with MA_NOACTIVATE so that dragging never
    /// pulls focus away from whatever the user is working in.
    /// </summary>
    private IntPtr OnWindowMessage(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr idSubclass,
        UIntPtr refData)
    {
        if (message == WmMouseActivate)
        {
            return new IntPtr(MaNoActivate);
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    // ---- Native interop ----------------------------------------------------

    private delegate IntPtr SubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr idSubclass,
        UIntPtr refData);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam);
}
