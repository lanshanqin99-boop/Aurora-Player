using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace Aurora;

/// <summary>
/// Shared window-frame cleanup for Aurora's borderless windows.
///
/// The presenter asks for no border, but a window can still carry WS_BORDER /
/// WS_CAPTION / WS_EX_CLIENTEDGE, and DWM paints a 1px border of its own on top of
/// that. This removes all of them and stops DWM rounding the frame, so the XAML
/// Border is the only visible edge. Used by both the player and the playlist window.
/// </summary>
internal static class WindowFrame
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    /// <summary>WS_BORDER — a thin frame drawn by the window itself.</summary>
    private const long WsBorder = 0x00800000L;

    /// <summary>WS_CAPTION — WS_BORDER | WS_DLGFRAME. It re-adds a frame.</summary>
    private const long WsCaption = 0x00C00000L;

    /// <summary>WS_EX_CLIENTEDGE — a sunken edge drawn around the client area.</summary>
    private const long WsExClientEdge = 0x00000200L;

    /// <summary>WS_EX_TOOLWINDOW — keeps a window out of Alt+Tab and the taskbar.</summary>
    private const long WsExToolWindow = 0x00000080L;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>HWND_TOPMOST — the band above ordinary windows.</summary>
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);

    private const int DwmwaNcRenderingPolicy = 2;
    private const int DwmNcrpDisabled = 1;

    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_DONOTROUND — stop DWM adding a rounded edge of its own.</summary>
    private const int DwmcpDoNotRound = 1;

    private const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_COLOR_NONE — asks DWM to draw no border at all.</summary>
    private const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>
    /// Removes the window's frame: the XAML Border owns the visible edge and its 24px
    /// rounding. WS_THICKFRAME and WS_SYSMENU are deliberately left alone.
    /// </summary>
    public static void RemoveBorder(IntPtr hWnd)
    {
        bool stylesChanged = false;

        long style = GetWindowLongPtr(hWnd, GwlStyle).ToInt64();

        if ((style & WsBorder) != 0)
        {
            style &= ~WsBorder;
            stylesChanged = true;
        }

        if ((style & WsCaption) != 0)
        {
            style &= ~WsCaption;
            stylesChanged = true;
        }

        if (stylesChanged)
        {
            SetWindowLongPtr(hWnd, GwlStyle, new IntPtr(style));
        }

        long exStyle = GetWindowLongPtr(hWnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExClientEdge) != 0)
        {
            SetWindowLongPtr(hWnd, GwlExStyle, new IntPtr(exStyle & ~WsExClientEdge));
            stylesChanged = true;
        }

        // SWP_FRAMECHANGED makes the style edits take effect without a resize; the
        // other flags keep the window exactly where it is.
        if (stylesChanged)
        {
            SetWindowPos(
                hWnd,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }

        int noDwmRounding = DwmcpDoNotRound;
        DwmSetWindowAttribute(hWnd, DwmwaWindowCornerPreference, ref noDwmRounding, sizeof(int));

        // DWM paints a 1px border of its own on top of the window frame;
        // DWMWA_COLOR_NONE suppresses it.
        int noBorder = DwmColorNone;
        int hr = DwmSetWindowAttribute(hWnd, DwmwaBorderColor, ref noBorder, sizeof(int));

        // Only if DWM refuses to drop the border do we disable non-client rendering,
        // which removes the frame at the cost of the shadow too.
        if (hr != 0)
        {
            int ncPolicy = DwmNcrpDisabled;
            DwmSetWindowAttribute(hWnd, DwmwaNcRenderingPolicy, ref ncPolicy, sizeof(int));
        }
    }

    /// <summary>
    /// WS_EX_TOOLWINDOW — keeps a window out of Alt+Tab and off the taskbar. The startup card is not a
    /// place the user can go, so it is not listed as one.
    /// </summary>
    public static void MarkToolWindow(IntPtr hWnd)
    {
        long exStyle = GetWindowLongPtr(hWnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(hWnd, GwlExStyle, new IntPtr(exStyle | WsExToolWindow));
    }

    /// <summary>
    /// Puts the window in the topmost band.
    ///
    /// Asking the presenter for an always-on-top window is not enough on its own: the band is lost
    /// somewhere in the window's creation, and what is left only *intends* to float. Nothing else in
    /// this app noticed because the player re-asserts the band itself when it places its window; the
    /// startup card has no such moment, and without this it can come up behind whatever is already on
    /// the screen — measured: the card running under another window.
    /// </summary>
    public static void AssertTopmost(IntPtr hWnd)
    {
        SetWindowPos(hWnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// The icon the taskbar, Alt+Tab and the window switcher show.
    ///
    /// The packaged executable is built from a generic host stub and carries no icon of its own, so
    /// without this the one place Aurora would still look unfinished is the place it lives: its own
    /// window button. The icon file ships beside the executable, and the package's own tile and Start
    /// entry come from the manifest's logo assets instead — this covers the window.
    /// </summary>
    public static void SetIcon(AppWindow window)
    {
        try
        {
            string icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");

            if (System.IO.File.Exists(icon))
            {
                window.SetIcon(icon);
            }
        }
        catch (Exception)
        {
            // An icon is cosmetic: a window without one is still a window.
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
