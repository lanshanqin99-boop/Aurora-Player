using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Aurora;

/// <summary>
/// The startup card: a small, centred, borderless window that plays the reveal while the player is
/// built underneath it.
///
/// It exists as its own window rather than as a layer over the player for one reason — the player has
/// to be built, and building it takes the UI thread. If the reveal lived in the player's window it
/// could not be shown until that work was done, which is precisely the delay it is there to cover. On
/// its own, this window can be up in one frame; everything expensive then happens behind it, and
/// because the reveal runs on the compositor it keeps playing smoothly throughout.
/// </summary>
public sealed partial class SplashWindow : Window
{
    /// <summary>The card's size in effective pixels. Every position in the reveal is relative to it.</summary>
    public const double DesignWidth = 720;
    public const double DesignHeight = 540;

    private readonly IntPtr _hWnd;

    /// <summary>Raised on the UI thread when the card begins to fade and the player should come up.</summary>
    public event EventHandler? Revealing;

    /// <summary>Raised on the UI thread when the fade is done and the card can be closed.</summary>
    public event EventHandler? Finished;

    public SplashWindow()
    {
        InitializeComponent();

        _hWnd = WindowNative.GetWindowHandle(this);

        // The host backdrop that keeps the window surface transparent, exactly as the player does it.
        // Without it the window paints its own square background behind the Border, and because the
        // Border is rounded, that square shows through at all four corners — measured: a hard
        // rectangular edge around a rounded card.
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        AppWindow.Title = "Aurora";
        WindowFrame.SetIcon(AppWindow);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // The whole client area is the card; no title bar reserves any of it.
        ExtendsContentIntoTitleBar = true;

        // Same frame treatment as the other two windows, so the card's own rounded border is the only
        // edge on it.
        WindowFrame.RemoveBorder(_hWnd);

        // Out of Alt+Tab and off the taskbar: it is a splash, not a place the user can go.
        WindowFrame.MarkToolWindow(_hWnd);

        // And genuinely on top of everything, which the presenter alone does not achieve.
        WindowFrame.AssertTopmost(_hWnd);

        Centre(Size());
    }

    /// <summary>Sizes the card for the current display's scaling and puts it in the middle of the screen.</summary>
    private SizeInt32 Size()
    {
        double scale = DpiScale();
        return new SizeInt32(
            (int)Math.Round(DesignWidth * scale),
            (int)Math.Round(DesignHeight * scale));
    }

    private void Centre(SizeInt32 size)
    {
        RectInt32 work = DisplayArea
            .GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary)
            .WorkArea;

        AppWindow.Move(new PointInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2));

        // Set after the move: resizing a window that has not been placed yet puts it wherever the
        // system decides, which for a centred splash is the one place it must not be.
        AppWindow.ResizeClient(size);
    }

    private double DpiScale()
    {
        uint dpi = GetDpiForWindow(_hWnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>
    /// Starts the reveal. Everything it does is scheduled on the compositor and returns at once, so the
    /// caller is free to spend the next five seconds building the player.
    /// </summary>
    public void Begin() => SplashAnimation.Play(
        Stage,
        () => Revealing?.Invoke(this, EventArgs.Empty),
        () => Finished?.Invoke(this, EventArgs.Empty));


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
