using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Aurora;

/// <summary>
/// File/folder picking through the Win32 common item dialogs (IFileOpenDialog).
///
/// The WinRT Windows.Storage.Pickers types cannot be used with this window: their
/// modal loop only runs once the calling window is foreground, and this app's
/// window is a non-activating overlay (WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW) whose
/// SetForegroundWindow request Windows refuses. IFileOpenDialog takes an owner
/// HWND instead and needs no foreground rights.
///
/// The COM interfaces come from the CsWin32 source generator, so the vtable layout
/// (including the IFileDialog members that IFileOpenDialog inherits) is taken
/// straight from the Windows SDK metadata rather than hand-written.
/// </summary>
internal static class PickerInterop
{
    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED) — the user dismissed the dialog.</summary>
    private const int HResultCancelled = unchecked((int)0x800704C7);

    /// <summary>CLSID_FileOpenDialog — also used for folder mode, via FOS_PICKFOLDERS.</summary>
    private static readonly Guid ClsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");

    /// <summary>
    /// Shows a multi-select file dialog. Returns the chosen paths, or an empty list
    /// when the user cancels.
    /// </summary>
    public static IReadOnlyList<string> PickFiles(IntPtr owner, IReadOnlyList<string> extensions)
    {
        IFileOpenDialog dialog = CreateDialog();
        try
        {
            ApplyFilters(dialog, extensions);

            // GetOptions → SetOptions, keeping the dialog's existing options.
            AddOptions(
                dialog,
                FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_ALLOWMULTISELECT);

            if (!TryShow(dialog, owner))
            {
                return Array.Empty<string>();
            }

            return CollectPaths(dialog);
        }
        finally
        {
            Release(dialog);
        }
    }

    /// <summary>
    /// Shows a folder picker. Returns the chosen folder, or null when the user
    /// cancels. FOS_ALLOWMULTISELECT is deliberately not requested in this mode.
    /// </summary>
    public static string? PickFolder(IntPtr owner)
    {
        IFileOpenDialog dialog = CreateDialog();
        try
        {
            AddOptions(
                dialog,
                FILEOPENDIALOGOPTIONS.FOS_PICKFOLDERS | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM);

            if (!TryShow(dialog, owner))
            {
                return null;
            }

            IReadOnlyList<string> paths = CollectPaths(dialog);
            return paths.Count > 0 ? paths[0] : null;
        }
        finally
        {
            Release(dialog);
        }
    }

    private static IFileOpenDialog CreateDialog()
    {
        Type type = Type.GetTypeFromCLSID(ClsidFileOpenDialog, throwOnError: true)!;
        return (IFileOpenDialog)Activator.CreateInstance(type)!;
    }

    /// <summary>
    /// Adds dialog options, preserving whatever the dialog already has.
    /// GetOptions is only exposed as the raw pointer member, hence the unsafe read.
    /// </summary>
    private static unsafe void AddOptions(IFileOpenDialog dialog, FILEOPENDIALOGOPTIONS extra)
    {
        FILEOPENDIALOGOPTIONS options;
        ((IFileDialog)dialog).GetOptions(&options);
        ((IFileDialog)dialog).SetOptions(options | extra);
    }

    private static unsafe void ApplyFilters(IFileOpenDialog dialog, IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0)
        {
            return;
        }

        var allPatterns = new List<string>(extensions.Count);
        foreach (string extension in extensions)
        {
            allPatterns.Add("*" + extension);
        }

        var names = new List<string>(extensions.Count + 1) { "音频文件" };
        var specs = new List<string>(extensions.Count + 1) { string.Join(";", allPatterns) };
        foreach (string extension in extensions)
        {
            names.Add(extension.TrimStart('.').ToUpperInvariant() + " 音频");
            specs.Add("*" + extension);
        }

        // COMDLG_FILTERSPEC carries PCWSTR pointers, so unmanaged copies are made and
        // held for the whole call; a pinned managed string would not stay pinned once
        // control leaves a fixed block, leaving a dangling pointer for COM to read.
        var filters = new COMDLG_FILTERSPEC[names.Count];
        var allocated = new List<IntPtr>(names.Count * 2);
        try
        {
            for (int i = 0; i < names.Count; i++)
            {
                IntPtr name = Marshal.StringToHGlobalUni(names[i]);
                allocated.Add(name);
                IntPtr spec = Marshal.StringToHGlobalUni(specs[i]);
                allocated.Add(spec);

                filters[i] = new COMDLG_FILTERSPEC
                {
                    pszName = new PCWSTR((char*)name),
                    pszSpec = new PCWSTR((char*)spec),
                };
            }

            unsafe
            {
                // The friendly SetFileTypes overload is not generated for this
                // project, so the raw SDK signature (count + pointer) is used.
                fixed (COMDLG_FILTERSPEC* first = filters)
                {
                    ((IFileDialog)dialog).SetFileTypes((uint)filters.Length, first);
                }
            }
        }
        finally
        {
            foreach (IntPtr block in allocated)
            {
                Marshal.FreeHGlobal(block);
            }
        }
    }

    /// <summary>Runs the modal dialog. False means the user cancelled.</summary>
    private static bool TryShow(IFileOpenDialog dialog, IntPtr owner)
    {
        try
        {
            // CsWin32 projects Show as void, throwing on a failing HRESULT.
            dialog.Show(new HWND(owner));
            return true;
        }
        catch (COMException ex) when (ex.HResult == HResultCancelled)
        {
            return false;
        }
    }

    /// <summary>Reads each result item's file-system path, releasing every interface.</summary>
    private static unsafe IReadOnlyList<string> CollectPaths(IFileOpenDialog dialog)
    {
        var paths = new List<string>();
        dialog.GetResults(out IShellItemArray results);

        try
        {
            results.GetCount(out uint count);
            for (uint index = 0; index < count; index++)
            {
                results.GetItemAt(index, out IShellItem item);
                try
                {
                    unsafe
                    {
                        // Raw signature: (SIGDN, PWSTR*). The friendly overload is
                        // not generated for this project.
                        PWSTR name;
                        item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &name);
                        try
                        {
                            paths.Add(name.ToString());
                        }
                        finally
                        {
                            // GetDisplayName allocates with CoTaskMemAlloc; free it here.
                            Marshal.FreeCoTaskMem((IntPtr)name.Value);
                        }
                    }
                }
                finally
                {
                    Release(item);
                }
            }
        }
        finally
        {
            Release(results);
        }

        return paths;
    }

    private static void Release(object comObject) => Marshal.ReleaseComObject(comObject);
}
