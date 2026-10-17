using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace Aurora;

/// <summary>
/// The entry point, which exists for one reason: so that a second launch never becomes a second player.
///
/// Aurora is registered as a handler for audio files, and the whole point of that is opening a file
/// when the app is already running. Without this, each double-click would start another copy — another
/// window, another libvlc, two things playing at once — because a full-trust desktop app is
/// multi-instanced by default. Here a launch that finds an instance already running hands its
/// activation over to it and exits, so the file lands in the window that is already there.
///
/// It is the generated entry point with that decision put in front of it, and the body below is the
/// generated one verbatim: the same COM wrappers, the same dispatcher-queue synchronization context,
/// and the same <see cref="App"/>. The project defines <c>DISABLE_XAML_GENERATED_MAIN</c> so that this
/// one is used instead.
/// </summary>
public static class Program
{
    /// <summary>The name the running instance is found by. One name: one Aurora.</summary>
    private const string InstanceKey = "Aurora";

    [STAThread]
    private static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        // The first launch's files. The installer writes ordinary registry associations, so Windows
        // starts the app with the file as an argument rather than raising the WinRT file activation a
        // packaged app would receive; reading them here is what makes a double-click open the file.
        // A launch handed over to the instance already running carries its arguments through the
        // activation instead, which App handles separately.
        App.StartupFiles = App.FilePathsIn(args);

        if (HandOverToRunningInstance())
        {
            return;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// True when another Aurora is already running and this launch has been given to it. False when
    /// this is that instance — or when there is no package identity to arrange the hand-over through,
    /// in which case the app starts as it always did rather than not starting at all.
    /// </summary>
    private static bool HandOverToRunningInstance()
    {
        try
        {
            AppInstance running = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (running.IsCurrent)
            {
                return false;
            }

            // What this launch was asked to do — the files it was opened with — goes with it.
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();

            // RedirectActivationToAsync has to be pumped rather than awaited here: this thread has no
            // dispatcher yet, and the hand-over completes on one.
            using var handover = new ManualResetEventSlim(false);
            _ = RedirectAsync(running, activation, handover);

            // Bounded: a running instance that never answers must not leave a process hanging with no
            // window at all.
            handover.Wait(TimeSpan.FromSeconds(10));
            return true;
        }
        catch (Exception)
        {
            return false;
        }

        static async Task RedirectAsync(AppInstance running, AppActivationArguments activation, ManualResetEventSlim handover)
        {
            try
            {
                await running.RedirectActivationToAsync(activation);
            }
            catch (Exception)
            {
                // The hand-over is a courtesy; failing it only means this launch exits.
            }
            finally
            {
                handover.Set();
            }
        }
    }
}
