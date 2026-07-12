using System.Diagnostics;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Measures whether the Explorer shell is actually blocked.
///
/// The source guide's central failure mode is a stalled sync-status query blocking Explorer's message pump,
/// which makes the desktop and taskbar appear frozen while other applications keep responding. Until now
/// OneLag inferred that from folder shape. This tests it: it asks the shell window to answer a null message
/// and times how long that takes, which is the same thing Windows itself uses to decide a window is hung.
/// </summary>
internal static class WindowsShellProbe
{
    private const uint WmNull = 0x0000;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ProbeTimeoutMilliseconds = 2_000;
    private const int MaxEnumeratedWindows = 500;

    public static ShellResponsiveness Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return ShellResponsiveness.Unavailable("unavailable-on-this-platform");
        }

        try
        {
            var explorerRunning = Process.GetProcessesByName("explorer").Length > 0;
            var shellWindow = FindWindow("Shell_TrayWnd", null);
            if (shellWindow == IntPtr.Zero)
            {
                return new ShellResponsiveness(
                    DateTimeOffset.UtcNow,
                    explorerRunning,
                    null,
                    0,
                    null,
                    "shell-tray-window-not-found");
            }

            var hung = IsHungAppWindow(shellWindow);

            var stopwatch = Stopwatch.StartNew();
            var answered = SendMessageTimeout(
                shellWindow,
                WmNull,
                UIntPtr.Zero,
                IntPtr.Zero,
                SmtoAbortIfHung,
                ProbeTimeoutMilliseconds,
                out _) != IntPtr.Zero;
            stopwatch.Stop();

            // A timed-out probe means the shell never pumped the message. Reporting the elapsed time as the
            // latency would understate it, so it is pinned to the full timeout instead.
            var latency = answered
                ? stopwatch.Elapsed.TotalMilliseconds
                : ProbeTimeoutMilliseconds;

            return new ShellResponsiveness(
                DateTimeOffset.UtcNow,
                explorerRunning,
                hung || !answered,
                CountHungWindows(),
                latency,
                answered ? "windows-shell-message-pump-probe" : "windows-shell-message-pump-timed-out");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return ShellResponsiveness.Unavailable("windows-shell-entrypoint-unavailable");
        }
    }

    private static int CountHungWindows()
    {
        var hung = 0;
        var visited = 0;

        EnumWindows(
            (handle, _) =>
            {
                if (++visited > MaxEnumeratedWindows)
                {
                    return false;
                }

                if (IsWindowVisible(handle) && IsHungAppWindow(handle))
                {
                    hung++;
                }

                return true;
            },
            IntPtr.Zero);

        return hung;
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out UIntPtr result);
}
