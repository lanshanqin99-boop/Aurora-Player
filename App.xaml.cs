using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.AppLifecycle;

namespace Aurora;

/// <summary>
/// The application object: the one place the operating system is judged before any of Aurora's windows
/// exist, the one place a file handed over by Windows is picked up, and the one place the startup
/// sequence is decided.
///
/// Startup happens in a deliberate order. The startup card goes up first — it is one small window whose
/// contents are a flat colour and a handful of composition visuals, so it can be on screen in a frame.
/// Only once it has actually been painted is the player built, and building it takes the UI thread for
/// as long as it takes: reading its XAML, starting libvlc, resolving the first surfaces. None of that
/// delays the card, and none of it stutters the reveal either, because the reveal is played by the
/// compositor and never asks the UI thread for anything again. The two overlap completely: the whole of
/// the player's construction happens inside the card's five seconds.
///
/// The card is not a delay bolted onto startup. Startup is the same length either way; the difference is
/// that it now has something to look at.
///
/// Aurora requires Windows 11 24H2 — build 26100 — or later, and it is built against that same version:
/// the project targets <c>net10.0-windows10.0.26100.0</c> and declares 26100 as its minimum, so every
/// API the compiler resolves is guaranteed to be present at run time and nothing has to be written twice
/// for two versions. What Aurora leans on for its looks is the Windows 11 window treatment specifically
/// — the DWM attributes that suppress the frame's own rounding and its 1px border, over a composited
/// transparent host. On Windows 10 those calls fail and the code falls back to disabling non-client
/// rendering, which also costs the window its shadow: a configuration that has never been built or
/// tested, and one this check exists to keep out of users' hands.
///
/// The package manifest carries the same minimum, so an older system cannot install Aurora at all. This
/// check covers the ways in — a sideloaded package, a developer run, a manifest that was edited — and it
/// is the only one that can say why in words.
/// </summary>
public partial class App : Application
{
    /// <summary>Windows 11 24H2. The lowest build whose window treatment Aurora is built and tested on.</summary>
    private const int MinimumBuild = 26100;

    private MainWindow? _window;
    private SplashWindow? _splash;

    /// <summary>The UI thread's queue, kept so a handed-over activation can be run on it.</summary>
    private DispatcherQueue? _dispatcher;

    /// <summary>
    /// Files named on the command line, put here by <see cref="Program"/> before the app starts.
    ///
    /// An unpackaged app takes its file associations from the installer's registry entries, and those
    /// hand the file over as an argument rather than raising the WinRT file activation that a packaged
    /// app receives. Reading the arguments is therefore the only way a double-click opens anything in
    /// the unpackaged build — the activation it gets is a plain launch carrying no files.
    /// </summary>
    internal static IReadOnlyList<string> StartupFiles { get; set; } = Array.Empty<string>();

    public App()
    {
        InitializeComponent();

        // A file opened while Aurora is already running arrives here rather than as a new process; see
        // Program, which does the handing over.
        try
        {
            AppInstance.GetCurrent().Activated += OnActivated;
        }
        catch (Exception)
        {
            // Without package identity there is nothing to redirect, and nothing will arrive here.
        }
    }

    /// <summary>
    /// Invoked when the application is launched. Nothing is created before the operating system has been
    /// checked, so an unsupported system never reaches the window code at all.
    ///
    /// A launch that arrives carrying files — a double-click, "Open with", or Aurora being the default
    /// handler — comes through here too, and is not something WinUI offers an override for: the
    /// activation is read back from the app, and its kind decides whether there is anything to open.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!OperatingSystemIsSupported())
        {
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Two sources, because two kinds of build reach this point. A packaged app is activated with its
        // files and finds them in the activation; an unpackaged app is handed them on the command line
        // and finds them in the field Program filled in. Both are ordinary, and a build only ever has
        // one of them.
        var files = new List<string>(PathsFrom(Activation()));
        foreach (string path in StartupFiles)
        {
            if (!files.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(path);
            }
        }

        Start(files);
    }

    /// <summary>
    /// A later launch's activation, handed over by <see cref="Program"/>. It arrives on a thread of the
    /// platform's choosing, so everything it touches happens on the UI thread instead — and the window
    /// is brought forward, because the user has just asked for it by opening a file.
    /// </summary>
    private void OnActivated(object? sender, AppActivationArguments args)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            Start(PathsFrom(args));
            _window?.Activate();
        });
    }

    /// <summary>
    /// The startup sequence: card up, player built behind it, player revealed as the card dissolves.
    /// </summary>
    private void Start(IReadOnlyList<string> files)
    {
        // A second activation, or a start after the card is gone, has a player already: the files simply
        // join it rather than a card being played again over a window that is already up.
        if (_window is not null)
        {
            _window.OpenFromAssociation(files);
            return;
        }

        try
        {
            _splash = new SplashWindow();

            _splash.Revealing += (_, _) => _window?.Reveal();
            _splash.Finished += (_, _) =>
            {
                _splash?.Close();
                _splash = null;
            };

            _splash.Activate();

            // Started before the player is built, and that order is the whole point: the timeline is
            // running on the compositor from here, so everything below happens inside it.
            _splash.Begin();

            // The player is built from the card's first painted frame rather than immediately. Built
            // immediately it would hold the UI thread before the card had ever been drawn, which is the
            // one failure this arrangement exists to avoid.
            CompositionTarget.Rendering += OnCardPainted;

            _ = RevealGuard();
        }
        catch (Exception)
        {
            // A card that cannot be shown is not a reason to fail to start: the player comes up on its
            // own, exactly as it did before there was a card.
            CloseCard();
            ShowPlayer(files);
        }

        void OnCardPainted(object? sender, object e)
        {
            CompositionTarget.Rendering -= OnCardPainted;

            // Queued rather than built here. This runs inside the compositor's own frame callback, and
            // creating and activating a window from in there deadlocks the UI thread — measured: the
            // process sits unresponsive at exactly this line, the card frozen on screen. The callback is
            // only wanted as the signal that the card has been painted; the work goes back on the queue,
            // where it belongs.
            _dispatcher?.TryEnqueue(() => ShowPlayer(files));
        }
    }

    /// <summary>
    /// Builds the player. Every millisecond of this is covered by the card, which is why it is allowed
    /// to be the slow part.
    /// </summary>
    private void ShowPlayer(IReadOnlyList<string> files)
    {
        if (_window is not null)
        {
            return;
        }

        // Held only if there is really a card to wait for: painted and composited offscreen,
        // transparent, and not moved on screen until the card hands over. Without a card the window
        // places itself as soon as it has a frame, exactly as before.
        _window = new MainWindow(holdForReveal: _splash is not null);
        _window.Activate();

        if (files.Count > 0)
        {
            _window.OpenFromAssociation(files);
        }
    }

    private void CloseCard()
    {
        try
        {
            _splash?.Close();
        }
        catch (Exception)
        {
            // Already gone.
        }

        _splash = null;
    }

    /// <summary>
    /// The last line of defence for the hand-over.
    ///
    /// The card is what hands the player over, and if it never does — the card's window dying, its
    /// timeline being dropped, the events not arriving — the player stays parked off the screen and
    /// Aurora is running but invisible: alive in Task Manager, with no window and nothing to close. That
    /// is not a state the app may be able to reach, so it is checked rather than assumed: a beat after
    /// the card should have finished, the card is closed and the player is brought up regardless, both
    /// of which are no-ops if the normal path already did them.
    /// </summary>
    private async Task RevealGuard()
    {
        await Task.Delay(TimeSpan.FromSeconds(SplashAnimation.RevealAt + 2.0));

        // Closing first: if the player has not been built yet, this is what stops it being created
        // holding on for a card that is no longer there.
        CloseCard();
        _window?.Reveal();
    }

    /// <summary>This launch's activation, or null when it cannot be read.</summary>
    private static AppActivationArguments? Activation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The files an activation was asked to open, or an empty list when it was not asked to open any.
    /// Paths rather than files: everything the queue does is by path, and a path is also what survives a
    /// virtual item or a cloud placeholder having nothing readable behind it.
    /// </summary>
    private static IReadOnlyList<string> PathsFrom(AppActivationArguments? activation)
    {
        var paths = new List<string>();

        if (activation is null)
        {
            return paths;
        }

        // A packaged app: the activation carries the files themselves.
        if (activation.Kind == ExtendedActivationKind.File
            && activation.Data is Windows.ApplicationModel.Activation.FileActivatedEventArgs fileArgs)
        {
            foreach (object item in fileArgs.Files)
            {
                if (item is Windows.Storage.StorageFile file && !string.IsNullOrEmpty(file.Path))
                {
                    paths.Add(file.Path);
                }
            }

            return paths;
        }

        // An unpackaged app: a later launch's command line, forwarded here by Program when it hands its
        // activation to the instance that is already running. The first launch does not come through
        // this path — Program reads its own arguments and sets StartupFiles instead.
        if (activation.Data is Windows.ApplicationModel.Activation.LaunchActivatedEventArgs launch)
        {
            paths.AddRange(FilePathsIn(SplitArguments(launch.Arguments)));
        }

        return paths;
    }

    /// <summary>
    /// Splits a raw command line into arguments, keeping a quoted run together. It only has to be right
    /// for file paths, which is all Aurora is ever handed, so it does not need to know about escapes.
    /// </summary>
    private static string[] SplitArguments(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && (c == ' ' || c == '\t'))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.ToArray();
    }

    /// <summary>
    /// The arguments that name a file which is really there. Anything else on the command line — a
    /// switch, an argument left over in a shortcut, a path that has since been deleted — is ignored
    /// rather than becoming a queue entry that cannot play.
    /// </summary>
    internal static IReadOnlyList<string> FilePathsIn(IEnumerable<string> arguments)
    {
        var paths = new List<string>();

        foreach (string argument in arguments)
        {
            if (string.IsNullOrWhiteSpace(argument)
                || argument.StartsWith("-", StringComparison.Ordinal)
                || argument.StartsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                if (System.IO.File.Exists(argument))
                {
                    paths.Add(argument);
                }
            }
            catch (Exception)
            {
                // A path the file system refuses to look at is not one that can be opened.
            }
        }

        return paths;
    }

    /// <summary>
    /// Whether this system can run Aurora. A build below the floor is told why, and the app then ends
    /// without creating anything.
    /// </summary>
    private static bool OperatingSystemIsSupported()
    {
        if (Environment.OSVersion.Version.Build >= MinimumBuild)
        {
            return true;
        }

        ReportUnsupportedWindows();
        Current.Exit();
        return false;
    }

    /// <summary>
    /// Says why Aurora will not start. A message box rather than a XAML dialog on purpose: there is no
    /// window to host one, and this has to work on the systems it is turning away.
    /// </summary>
    private static void ReportUnsupportedWindows()
    {
        string message =
            "你的Windows版本不符合最低要求" + Environment.NewLine + Environment.NewLine +
            "当前系统：" + Environment.OSVersion.Version + Environment.NewLine +
            "最低要求：Windows 11 24H2（内部版本 " + MinimumBuild + "）";

        MessageBox(
            IntPtr.Zero,
            message,
            "Aurora",
            MessageBoxOk | MessageBoxIconWarning | MessageBoxSetForeground | MessageBoxTopmost);
    }

    private const uint MessageBoxOk = 0x00000000;
    private const uint MessageBoxIconWarning = 0x00000030;
    private const uint MessageBoxSetForeground = 0x00010000;
    private const uint MessageBoxTopmost = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
