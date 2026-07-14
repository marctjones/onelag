using System.Runtime.Versioning;
using Microsoft.Win32;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Reads registered Explorer shell extensions straight out of the registry.
///
/// This is the collector <see cref="ShellExtensionInventory"/> exists for: icon overlay handlers run
/// synchronously on the Explorer UI thread and Windows honours only the first
/// <see cref="ShellExtensionInventory.IconOverlayLimit"/> by sort order, so a crowded overlay list both blocks
/// the shell and silently drops handlers past the cutoff. The log bundle this project works from contains no
/// registry export, so this was previously completely uninspectable.
///
/// Everything that can be decided from a resolved CLSID/name/path belongs in the pure
/// <see cref="ShellExtensionClassifier"/> instead of here, so that decision is testable off Windows; this class
/// exists only to talk to the registry and never to throw doing it.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsShellExtensionProbe
{
    private const string IconOverlayKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";
    private const string ApprovedExtensionsKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";

    public static ShellExtensionInventory Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ShellExtensionInventory.Unavailable("unavailable-on-this-platform");
        }

        try
        {
            var overlays = ReadIconOverlays();
            var approved = ReadApprovedExtensions();

            // A CLSID can legitimately appear in both keys (an icon overlay handler is, separately, also an
            // "approved" shell extension). The overlay entry wins on collision because IconOverlay is the more
            // specific and more diagnostically relevant classification for this probe's purpose.
            var byClsid = new Dictionary<string, ShellExtensionInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in approved)
            {
                byClsid[extension.Clsid] = extension;
            }

            foreach (var extension in overlays)
            {
                byClsid[extension.Clsid] = extension;
            }

            var extensions = byClsid.Values
                .OrderBy(extension => extension.Kind, StringComparer.Ordinal)
                .ThenBy(extension => extension.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ShellExtensionInventory(
                DateTimeOffset.UtcNow,
                extensions,
                overlays.Count,
                overlays.Count(extension => !extension.IsMicrosoft),
                "windows-registry-shell-extension-enumeration");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ObjectDisposedException)
        {
            return ShellExtensionInventory.Unavailable("windows-shell-extension-registry-read-failed");
        }
    }

    /// <summary>
    /// Subkey names under this key are the overlay handlers' display names, and are deliberately read
    /// untrimmed: many begin with one or more spaces, a documented trick vendors use to sort ahead of everyone
    /// else and win the ~15-slot race Explorer enforces. Trimming them here would destroy the exact signal
    /// this probe exists to surface.
    /// </summary>
    private static IReadOnlyList<ShellExtensionInfo> ReadIconOverlays()
    {
        var results = new List<ShellExtensionInfo>();

        using var key = Registry.LocalMachine.OpenSubKey(IconOverlayKeyPath);
        if (key is null)
        {
            return results;
        }

        foreach (var name in key.GetSubKeyNames())
        {
            string? clsid;
            using (var subKey = key.OpenSubKey(name))
            {
                clsid = subKey?.GetValue(null) as string;
            }

            if (string.IsNullOrWhiteSpace(clsid))
            {
                continue;
            }

            var dllPath = ResolveInprocServerPath(clsid);
            var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(name, dllPath);
            results.Add(new ShellExtensionInfo(clsid, name, ShellExtensionKinds.IconOverlay, dllPath, isMicrosoft));
        }

        return results;
    }

    /// <summary>
    /// The Approved key does not record which kind of extension each CLSID implements (context menu, property
    /// sheet, drag-drop handler, ...) — that would mean walking each CLSID's "Implemented Categories" subkey
    /// for a payoff this probe does not need. <see cref="ShellExtensionKinds.ContextMenu"/> is used as the
    /// default label because it is overwhelmingly the most common category of entry in this key; it is a
    /// best-effort label, not a verified one.
    /// </summary>
    private static IReadOnlyList<ShellExtensionInfo> ReadApprovedExtensions()
    {
        var results = new List<ShellExtensionInfo>();

        using var key = Registry.LocalMachine.OpenSubKey(ApprovedExtensionsKeyPath);
        if (key is null)
        {
            return results;
        }

        foreach (var clsid in key.GetValueNames())
        {
            if (string.IsNullOrWhiteSpace(clsid))
            {
                continue;
            }

            var name = key.GetValue(clsid) as string;
            var displayName = string.IsNullOrWhiteSpace(name) ? clsid : name;
            var dllPath = ResolveInprocServerPath(clsid);
            var isMicrosoft = ShellExtensionClassifier.IsMicrosoft(displayName, dllPath);
            results.Add(new ShellExtensionInfo(clsid, displayName, ShellExtensionKinds.ContextMenu, dllPath, isMicrosoft));
        }

        return results;
    }

    /// <summary>
    /// Resolves a CLSID to the DLL that implements it, where that is cheap: a single registry read, no COM
    /// activation. A missing or inaccessible key just means the resolution is unavailable for that CLSID, not
    /// that the whole probe failed.
    /// </summary>
    private static string? ResolveInprocServerPath(string clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
            var path = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Environment.ExpandEnvironmentVariables(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
