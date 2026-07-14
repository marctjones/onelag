using System.Diagnostics;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Runs <c>fltmc filters</c> and hands its stdout to <see cref="FltmcOutputParser"/>.
///
/// This is deliberately the thinnest possible shell: everything that can be decided from the text belongs in
/// the pure parser, which is what makes the parser testable off Windows. This class exists only to launch the
/// process, bound how long it is allowed to run, and translate the ways it can fail into an evidence state
/// that says why — an empty filter list would read as "no filters attached", which on a machine that requires
/// elevation to enumerate them at all would be a lie, not a negative finding.
/// </summary>
internal static class WindowsFilterDriverProbe
{
    private const int TimeoutMilliseconds = 5_000;

    public static FilterDriverStack Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return FilterDriverStack.Unavailable("unavailable-on-this-platform");
        }

        try
        {
            return Run();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return FilterDriverStack.Unavailable("fltmc-failed");
        }
    }

    private static FilterDriverStack Run()
    {
        var startInfo = new ProcessStartInfo("fltmc")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("filters");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Win32Exception here means the OS could not find or launch fltmc.exe at all — a stripped-down
            // Windows image, a broken PATH, or a locked-down environment — which is a distinct failure from
            // running it and having it refuse.
            return FilterDriverStack.Unavailable("fltmc-not-found");
        }

        if (process is null)
        {
            return FilterDriverStack.Unavailable("fltmc-not-found");
        }

        using (process)
        {
            var stdOutTask = process.StandardOutput.ReadToEndAsync();
            var stdErrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMilliseconds))
            {
                TryKill(process);
                return FilterDriverStack.Unavailable("fltmc-timed-out");
            }

            var stdOut = stdOutTask.GetAwaiter().GetResult();
            var stdErr = stdErrTask.GetAwaiter().GetResult();

            // fltmc requires an elevated token to enumerate filters; without one it exits non-zero and prints
            // an access-denied sentence instead of a table. Reporting that explicitly is the whole point of
            // this probe: a filter list that reads as empty on an unelevated run would look like "no filters
            // attached" when it actually means "could not ask".
            if (process.ExitCode != 0 || LooksLikeAccessDenied(stdOut) || LooksLikeAccessDenied(stdErr))
            {
                return FilterDriverStack.Unavailable("fltmc-requires-elevation");
            }

            var filters = FltmcOutputParser.ParseFilters(stdOut);
            return FltmcOutputParser.BuildStack(filters, DateTimeOffset.UtcNow, FltmcOutputParser.SuccessEvidenceState);
        }
    }

    private static bool LooksLikeAccessDenied(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && (text.Contains("access is denied", StringComparison.OrdinalIgnoreCase)
                || text.Contains("administrator", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process had already exited between the timeout check and the kill attempt.
        }
    }
}
