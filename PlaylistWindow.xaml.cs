using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Aurora;

/// <summary>
/// Floating queue view that always mirrors <see cref="PlaybackService"/>. It holds
/// no playlist of its own: the ListView is bound directly to the service's
/// collection, so a reorder moves rows inside that one collection and no copy of
/// the queue exists anywhere.
///
/// Reordering is the ListView's own drag-and-drop, switched on by CanDragItems,
/// CanReorderItems and AllowDrop in PlaylistWindow.xaml. Nothing here captures the
/// pointer on press: taking the capture is what stops the list from ever starting
/// its drag, which is why a hand-rolled reorder used to be the only path and why it
/// left the list unable to reorder itself.
///
/// Visually it matches the main player: borderless, transparent host with a 24px
/// rounded Border as the only painted surface.
/// </summary>
public sealed partial class PlaylistWindow : Window
{
    private const int RowHeight = 54;
    private const int HeaderHeight = 48;
    private const int FooterHeight = 52;

    /// <summary>
    /// Room the list keeps while it is empty. The add flow is a dialog with three
    /// choices, and the footer card that opens it must never be clipped by a window
    /// squeezed down to its minimum.
    /// </summary>
    private const int EmptyListHeight = 72;

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;

    private readonly PlaybackService _playback;
    private readonly MainWindow _owner;
    private readonly IntPtr _hWnd;
    private readonly AppWindow _appWindow;

    /// <summary>The endless gradient-direction animation, created once.</summary>
    private Storyboard? _ambientDrift;

    /// <summary>True while the window writes the selection itself, so it is not a click.</summary>
    private bool _suppressSelectionSync;

    /// <summary>True while the queue is standing on the same picture as the player.</summary>
    private bool _auroraShown;

    /// <summary>The band of that picture last written here, so an unchanged one is not written again.</summary>
    private IdleCanvas.Band? _appliedBand;

    /// <summary>Native handle of this window, used as the owner target and for dialogs.</summary>
    internal IntPtr Handle => _hWnd;

    public PlaylistWindow(PlaybackService playback, MainWindow owner)
    {
        InitializeComponent();

        _playback = playback;
        _owner = owner;
        _hWnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;

        ConfigureWindow();

        TrackList.ItemsSource = _playback.Items;

        // A named handler, so it can be removed again: the service outlives this window, and a
        // lambda kept the closed window subscribed. Every open/close cycle then left another
        // handler behind, and each one repainted a window that no longer existed.
        _playback.PlaylistChanged += OnPlaylistChanged;

        RefreshFromService();

        // The window is new, so it is still wherever Windows happened to put it: a height is not a
        // place. This is what positions it under the player for the first time.
        FollowOwner();

        // The photograph is scaled to cover the pair, so the player has to know how tall this window
        // is before it can place its own share of it. Reporting the shell's own size, rather than the
        // height that was last asked for, keeps the two in step through the layout that follows a
        // resize and through whatever DPI scaling XAML applies to it.
        //
        // A named handler, and taken back when the window closes. This was a lambda, and a lambda on an
        // element's own event is a reference the element holds to the window that owns it: the shell is
        // part of the tree, so the tree kept the closed window alive and every open left another one
        // behind. Measured with a heap dump after sixty open/close cycles, thirty-one closed
        // PlaylistWindow instances were still on the heap. This was the only subscription in this class
        // that captured the window and was never released.
        Shell.SizeChanged += OnShellSizeChanged;

        Closed += OnClosed;
    }

    /// <summary>
    /// Lets go of everything this window subscribed to. The service outlives the window, and the shell
    /// is part of the window's own tree, so a handler left on either one keeps the closed window alive.
    /// </summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _playback.PlaylistChanged -= OnPlaylistChanged;
        Shell.SizeChanged -= OnShellSizeChanged;

        // The drift is a RepeatBehavior.Forever storyboard, and a storyboard is not stopped by the
        // window it animates going away.
        try
        {
            _ambientDrift?.Stop();
        }
        catch (Exception)
        {
            // A storyboard that will not stop must not prevent anything below.
        }

        _ambientDrift = null;
    }

    /// <summary>
    /// Reports this window's height to the player, which needs it to place the photograph's share.
    /// </summary>
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) =>
        _owner.NotifyQueueSurface(Shell.ActualHeight);

    /// <summary>Mirrors the service into this window whenever the queue changes.</summary>
    private void OnPlaylistChanged(object? sender, EventArgs e) => RefreshFromService();

    /// <summary>
    /// Paints this window with the player's palette. The palette is passed in — the queue never
    /// resolves one of its own and never reads the cover — so the two surfaces always agree, whether
    /// the track changes, the cover changes or the colour changes.
    ///
    /// <paramref name="animate"/> is false when the window is opened onto a palette that is already
    /// on screen: there is nothing to transition from, and fading in from black would be a visible
    /// mismatch rather than a smooth change.
    /// </summary>
    internal void ApplyPalette(AmbientPalette palette, bool animate)
    {
        try
        {
            if (animate)
            {
                AmbientGradient.FadeTo(AmbientStopPrimary, AmbientStopSecondary, AmbientStopAccent, palette);
            }
            else
            {
                AmbientGradient.SetStops(AmbientStopPrimary, AmbientStopSecondary, AmbientStopAccent, palette);
            }
        }
        catch (Exception)
        {
            // Animation is cosmetic: the colours still have to be right.
            AmbientGradient.SetStops(AmbientStopPrimary, AmbientStopSecondary, AmbientStopAccent, palette);
        }

        StartAmbientDrift();
    }

    /// <summary>
    /// Shows or hides the queue's share of the player's idle picture. It is told rather than deciding
    /// for itself: the surface belongs to the pair, so a queue that picked its own would be able to
    /// disagree with the player about what is on screen.
    ///
    /// <paramref name="animate"/> is false when the window opens onto a surface that is already up —
    /// there is nothing to transition from, and fading in from nothing would read as a mismatch
    /// rather than as a change.
    /// </summary>
    internal void ShowAuroraSurface(bool show, bool animate)
    {
        try
        {
            if (show && !EnsureIdleImage())
            {
                // No picture on disk: the gradient is a fine surface on its own.
                return;
            }

            bool changed = show != _auroraShown;
            _auroraShown = show;

            if (changed)
            {
                if (animate)
                {
                    var fade = new Duration(TimeSpan.FromMilliseconds(650));
                    var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
                    var storyboard = new Storyboard();

                    IdleSurface.AddFade(
                        storyboard, IdleBackground, show ? IdleSurface.Opacity : 0.0, fade, ease);
                    IdleSurface.AddFade(storyboard, IdleScrim, show ? 1.0 : 0.0, fade, ease);

                    storyboard.Begin();
                }
                else
                {
                    IdleBackground.Opacity = show ? IdleSurface.Opacity : 0.0;
                    IdleScrim.Opacity = show ? 1.0 : 0.0;
                }
            }

        }
        catch (Exception)
        {
            // Cosmetic only: the gradient stays as the surface.
        }
    }

    /// <summary>
    /// Places this window's share of the picture. The band arrives already measured, because the
    /// picture belongs to the pair and only one window may decide where it falls: measuring it here as
    /// well would give two answers the moment the two windows disagreed about the gap between them.
    /// </summary>
    internal void ApplyIdleBand(IdleCanvas.Band band)
    {
        try
        {
            // Written only when it differs: a band set to the one already in force is work asked of the
            // layout for nothing.
            if (_appliedBand is IdleCanvas.Band applied && applied == band)
            {
                return;
            }

            _appliedBand = band;
            IdleSurface.Apply(IdleBackground, band);
        }
        catch (Exception)
        {
            // Cosmetic only.
        }
    }

    /// <summary>
    /// Paints this window's copy of the idle picture once, from the same file and the same list of
    /// places the player looks in, so the two halves cannot end up showing different photographs.
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
    /// Keeps the queue's gradient turning on the same slow loop as the player's, so "the background
    /// is moving" is one state shared by both windows rather than a property of the player only.
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

    // ---- Visual language ---------------------------------------------------

    /// <summary>Re-reads the queue: count, selection, dynamic height and placement.</summary>
    public void RefreshFromService()
    {
        CountText.Text = _playback.Items.Count == 0
            ? "列表为空"
            : $"{_playback.Items.Count} 首";

        SyncSelectionToService();

        // The dividers belong to positions, so they are re-marked whenever the queue is re-read.
        TrackGrouping.Recompute(_playback.Items);

        ResizeAndReposition();
    }

    /// <summary>
    /// Puts the queue into the same z-order band as the player, and keeps it directly above
    /// it. Called after the owner has been set, because the band has to follow the owner.
    ///
    /// This is the fix for the queue and the player coming apart: an owned window is kept
    /// directly above its owner only when both sit in the same band. With the player in the
    /// topmost band and the queue left in the normal band, the queue dropped *below* the
    /// player — measured, not assumed — and an ordinary application could be raised in
    /// between. Matching the owner's band is what makes the pair behave as one window, and it
    /// is deliberately tied to the owner's band rather than being topmost in its own right:
    /// if the player is ever not topmost, the queue is not either.
    /// </summary>
    internal void JoinOwnerBand()
    {
        try
        {
            bool ownerIsTopmost = (_owner.AppWindow.Presenter as OverlappedPresenter)?.IsAlwaysOnTop ?? false;

            // HWND_TOP keeps the queue in the normal band; HWND_TOPMOST lifts it into the
            // topmost band. Either way the insert-after handle is the owner itself, which is
            // what pins the queue immediately above it.
            IntPtr band = ownerIsTopmost ? HwndTopmost : IntPtr.Zero;

            SetWindowPos(_hWnd, band, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

            // Then order it directly above the owner inside that band.
            SetWindowPos(
                _hWnd,
                _owner.Handle,
                0,
                0,
                0,
                0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        catch (Exception)
        {
            // Ordering is best-effort; the owned relationship still keeps the pair related.
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // No chrome; the shell Border is the whole surface.
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;

            // NOT topmost here on purpose. The queue must share its owner's band, and the
            // band is applied once the owner is known; see JoinOwnerBand.
            presenter.IsAlwaysOnTop = false;
        }

        // The playlist is interactive, so unlike the main player it stays activatable.
        // A tool window still keeps it out of Alt+Tab and the taskbar.
        long styles = GetWindowLongPtr(_hWnd, GwlExStyle).ToInt64();
        styles |= WsExToolWindow;
        SetWindowLongPtr(_hWnd, GwlExStyle, new IntPtr(styles));

        // The same icon as the player, for the window switcher and the taskbar preview.
        WindowFrame.SetIcon(_appWindow);

        // Same white-frame fix as the main player, shared through WindowFrame, which
        // also stops DWM rounding the frame (the shell Border owns the 24px radius).
        WindowFrame.RemoveBorder(_hWnd);

        // Same transparent host backdrop as the main player.
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();
    }

    /// <summary>
    /// Gives the window the height its queue needs, leaving it where it is.
    ///
    /// Height and placement are kept apart deliberately. The height follows the number of rows; the
    /// position follows the player. Running either of them when its own input has not changed means
    /// moving a window that was already correct — and on a window whose surface is transparent, every
    /// one of those moves is a frame with nothing painted in it. This is the path a queue change takes,
    /// and a queue change cannot move the window: the player has not moved, so there is nowhere to move
    /// it to. Measured before this split: adding a 1256-file folder re-placed the window 65 times in
    /// two seconds, each one a few pixels along, purely because this ran on every queue event.
    /// </summary>
    private void ResizeAndReposition()
    {
        RectInt32 current = CurrentRect();

        // The screen limit is measured from where the window already sits, which is where it will stay.
        int height = QueueHeight(current.Y);

        if (height == current.Height)
        {
            return;
        }

        Place(new RectInt32(current.X, current.Y, current.Width, height));
    }

    /// <summary>
    /// Puts the window back under the player: the player's width, the player's left edge, one gap below
    /// it, and the height the queue needs.
    ///
    /// This is called when the player moves and only then, because the player's position is the one
    /// input the queue cannot work out for itself.
    /// </summary>
    public void FollowOwner()
    {
        var player = _owner.AppWindow;
        PointInt32 playerPosition = player.Position;
        SizeInt32 playerSize = player.Size;

        // The gap is the shared one the idle picture is continuous across, converted to the physical
        // pixels the window is placed in. Placing the window from a second hard-coded number is how
        // the picture would develop a seam: the two would drift apart the moment either changed.
        int gap = (int)Math.Round(IdleCanvas.Gap * _owner.DpiScale);
        int below = playerPosition.Y + playerSize.Height + gap;

        Place(new RectInt32(playerPosition.X, below, playerSize.Width, QueueHeight(below)));
    }

    /// <summary>
    /// How tall the window has to be, given where its top edge is: the header, a row per track and the
    /// footer, never more than three player heights, and never past the bottom of the work area.
    /// </summary>
    private int QueueHeight(int top)
    {
        int count = _playback.Items.Count;
        int maxByPlayer = _owner.AppWindow.Size.Height * 3;

        DisplayArea area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        int maxByScreen = work.Y + work.Height - top;

        // An empty queue keeps enough room for the whole footer card and its "add"
        // affordance to be drawn, instead of being squeezed to a sliver.
        int emptyMinimum = HeaderHeight + EmptyListHeight + FooterHeight;

        int contentHeight = HeaderHeight + (count * RowHeight) + FooterHeight;

        return Math.Max(emptyMinimum, Math.Min(contentHeight, Math.Min(maxByPlayer, maxByScreen)));
    }

    /// <summary>
    /// The one place this window is ever moved. Asking for the size and place it already has does
    /// nothing except make the compositor present it again, so that request is not made at all.
    /// </summary>
    private void Place(RectInt32 target)
    {
        if (target.Equals(CurrentRect()))
        {
            return;
        }

        _appWindow.MoveAndResize(target);
    }

    /// <summary>Where the window actually is and how big it actually is, in physical pixels.</summary>
    private RectInt32 CurrentRect()
        => new(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);

    /// <summary>
    /// Puts the highlight back on whatever is playing. Selection is written only by this
    /// window, and the write is flagged so the selection handler does not mistake it for a
    /// click and start the track again.
    /// </summary>
    private void SyncSelectionToService()
    {
        int current = _playback.CurrentIndex;
        int wanted = current >= 0 && current < TrackList.Items.Count ? current : -1;

        if (TrackList.SelectedIndex == wanted)
        {
            return;
        }

        _suppressSelectionSync = true;
        try
        {
            TrackList.SelectedIndex = wanted;
        }
        finally
        {
            _suppressSelectionSync = false;
        }
    }

    /// <summary>
    /// The end of the list's own drag: the row has already been moved inside
    /// <see cref="PlaybackService.Items"/> by the ListView, and this brings the service's current
    /// index back in line with where the playing track now sits.
    ///
    /// The collection is never rebuilt and never re-sorted here — the same
    /// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> is reordered in place,
    /// which is exactly what the ListView renders from, so the row does not need to be re-created.
    /// </summary>
    private void OnTrackListDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Reordering only makes sense for a move that completed inside this list.
        if (args.DropResult != DataPackageOperation.Move)
        {
            return;
        }

        // The playing track is still in the queue; only its index moved.
        _playback.NotifyQueueReordered();

        // A drag changes what neighbours what, so the group dividers are re-marked: the row that was
        // dropped among another folder's rows may now be the first of its own group, or no longer be.
        TrackGrouping.Recompute(_playback.Items);

        // The highlight follows the track that is playing, not the slot it was dragged to.
        SyncSelectionToService();
    }

    /// <summary>
    /// Clicking a row plays it. Selection is what WinUI guarantees for a click, so this is the
    /// authoritative trigger: it does not depend on the pointer sequence surviving from press to
    /// release.
    ///
    /// A selection change caused by this window's own bookkeeping — for instance after a drag —
    /// must not start playback, which is what the suppression flag is for.
    /// </summary>
    private void OnTrackListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionSync)
        {
            return;
        }

        int index = TrackList.SelectedIndex;
        if (index < 0 || index >= _playback.Items.Count || index == _playback.CurrentIndex)
        {
            return;
        }

        _playback.SetCurrent(index);
    }

    // ---- Commands ----------------------------------------------------------

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AudioTrack track })
        {
            return;
        }

        int index = _playback.Items.IndexOf(track);
        if (index >= 0)
        {
            _playback.RemoveAt(index);
        }
    }

    /// <summary>
    /// The file picker, straight away. This is the left half of the footer, and it is the one path that
    /// adds individual files rather than a folder and everything under it.
    /// </summary>
    private void OnAddFilesClicked(object sender, RoutedEventArgs e)
        => Append(PickerInterop.PickFiles(_owner.Handle, PlaybackService.SupportedFileExtensions));

    /// <summary>
    /// The folder picker, straight away. <see cref="PlaybackService.AppendPaths"/> walks a folder
    /// itself, so a folder is handed over exactly as a file would be.
    /// </summary>
    private void OnAddFolderClicked(object sender, RoutedEventArgs e)
    {
        string? folder = PickerInterop.PickFolder(_owner.Handle);
        Append(folder is null ? Array.Empty<string>() : new[] { folder });
    }

    /// <summary>Adds whatever the picker returned, if it returned anything at all.</summary>
    private void Append(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        _playback.AppendPaths(paths);
        RefreshFromService();
    }

    // ---- Native interop ----------------------------------------------------

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
