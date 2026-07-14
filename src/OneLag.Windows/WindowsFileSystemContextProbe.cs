using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Captures the shape of the file namespace a native open-file dialog actually lands in.
///
/// Every native open-file dialog defaults to Documents. If Documents has been redirected into a cloud-synced
/// root via OneDrive's Known Folder Move, every such dialog enumerates a cloud-backed folder through the Cloud
/// Files filter plus the whole security filter stack, and a single dehydrated placeholder can block it on a
/// network round-trip. A dead mapped drive does the same thing for a different reason. Neither was previously
/// collected, because the log bundle this project works from contains no registry export.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileSystemContextProbe
{
    // Matches the cap WindowsPlatformProbe.SampleFilesOnDemandAttributes already uses for the same kind of
    // walk: enough to characterize a real OneDrive root without turning a diagnostic sample into an unbounded
    // recursive enumeration of a folder that can hold millions of items.
    private const int MaxDehydratedPlaceholderSamplesPerRoot = 10_000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;
    private const int NetUseTimeoutMilliseconds = 5_000;
    private const string DownloadsFolderRegistryValue = "{374DE290-123F-4565-9164-39C4925E467B}";

    private static readonly TimeSpan MappedDriveReachabilityTimeout = TimeSpan.FromSeconds(2);

    public static FileSystemContext Capture(IReadOnlyList<RootCandidate> roots)
    {
        if (!OperatingSystem.IsWindows())
        {
            return FileSystemContext.Unavailable("unavailable-on-this-platform");
        }

        try
        {
            var cloudRoots = (roots ?? Array.Empty<RootCandidate>())
                .Select(root => root.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();

            var knownFolders = CaptureKnownFolders(cloudRoots);
            var mappedDrives = CaptureMappedDrives();
            var placeholderCount = CountDehydratedPlaceholders(cloudRoots);

            return new FileSystemContext(
                DateTimeOffset.UtcNow,
                knownFolders,
                mappedDrives,
                placeholderCount,
                "windows-known-folders-and-mapped-drives");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FileSystemContext.Unavailable("windows-file-system-context-failed");
        }
    }

    private static IReadOnlyList<KnownFolderRedirect> CaptureKnownFolders(IReadOnlyList<string> cloudRoots)
    {
        var results = new List<KnownFolderRedirect>();
        AddKnownFolder(results, "Documents", TryGetSpecialFolder(Environment.SpecialFolder.MyDocuments), cloudRoots);
        AddKnownFolder(results, "Desktop", TryGetSpecialFolder(Environment.SpecialFolder.DesktopDirectory), cloudRoots);
        AddKnownFolder(results, "Pictures", TryGetSpecialFolder(Environment.SpecialFolder.MyPictures), cloudRoots);
        AddKnownFolder(results, "Downloads", TryGetDownloadsFolder(), cloudRoots);
        return results;
    }

    private static void AddKnownFolder(List<KnownFolderRedirect> results, string knownFolder, string? path, IReadOnlyList<string> cloudRoots)
    {
        var redirect = KnownFolderRedirectClassifier.Classify(knownFolder, path, cloudRoots);
        if (redirect is not null)
        {
            results.Add(redirect);
        }
    }

    private static string? TryGetSpecialFolder(Environment.SpecialFolder folder)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads has no <see cref="Environment.SpecialFolder"/> member. The documented way to get it is
    /// <c>SHGetKnownFolderPath(FOLDERID_Downloads)</c>, but that means marshaling a GUID through a COM-style
    /// P/Invoke for a single string; reading the same value out of User Shell Folders — which is exactly where
    /// Explorer itself stores it, including after a Known Folder Move — is a plain registry read consistent
    /// with the rest of this probe and avoids the extra native surface.
    /// </summary>
    private static string? TryGetDownloadsFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var raw = key?.GetValue(DownloadsFolderRegistryValue) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static IReadOnlyList<MappedDrive> CaptureMappedDrives()
    {
        var parsed = RunNetUse();
        if (parsed.Count == 0)
        {
            return parsed;
        }

        var withReachability = new List<MappedDrive>(parsed.Count);
        foreach (var drive in parsed)
        {
            withReachability.Add(drive with { Reachable = CheckReachableBounded(drive.Letter) });
        }

        return withReachability;
    }

    private static IReadOnlyList<MappedDrive> RunNetUse()
    {
        var startInfo = new ProcessStartInfo("net")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("use");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Array.Empty<MappedDrive>();
        }

        if (process is null)
        {
            return Array.Empty<MappedDrive>();
        }

        using (process)
        {
            try
            {
                var stdOutTask = process.StandardOutput.ReadToEndAsync();
                var stdErrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(NetUseTimeoutMilliseconds))
                {
                    TryKill(process);
                    return Array.Empty<MappedDrive>();
                }

                var stdOut = stdOutTask.GetAwaiter().GetResult();
                _ = stdErrTask.GetAwaiter().GetResult();

                // "There are no entries in the list." exits 0 with no data rows, which parses to an empty
                // list correctly; a genuinely failing invocation exits non-zero and is treated as no evidence
                // rather than "zero mapped drives".
                if (process.ExitCode != 0)
                {
                    return Array.Empty<MappedDrive>();
                }

                return NetUseOutputParser.ParseDrives(stdOut);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Array.Empty<MappedDrive>();
            }
        }
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

    /// <summary>
    /// A dead mapped drive is a classic, frequently-missed cause of exactly the freeze this probe exists to
    /// find, and <c>Directory.Exists</c> on a dead UNC path can itself block for 30+ seconds — the very bug
    /// being hunted. Running the check on a background task and refusing to wait past a short, fixed timeout
    /// means a hung share reports as "unknown" (<see langword="null"/>) instead of hanging the whole scan.
    /// The background task is deliberately abandoned rather than cancelled: there is no cooperative
    /// cancellation point inside a blocked Win32 file call, so cancelling the <see cref="Task"/> would not
    /// actually stop it. Letting it finish on its own thread and discarding the result is the correct, if
    /// slightly wasteful, way to bound a blocking syscall.
    /// </summary>
    private static bool? CheckReachableBounded(string letter)
    {
        try
        {
            var probeTask = Task.Run(() => Directory.Exists(letter + @"\"));
            return probeTask.Wait(MappedDriveReachabilityTimeout) ? probeTask.Result : null;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    private static int CountDehydratedPlaceholders(IReadOnlyList<string> cloudRoots)
    {
        var total = 0;
        foreach (var root in cloudRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            total += CountDehydratedPlaceholdersInRoot(root);
        }

        return total;
    }

    private static int CountDehydratedPlaceholdersInRoot(string root)
    {
        var sampled = 0;
        var recallOnDataAccess = 0;

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (sampled >= MaxDehydratedPlaceholderSamplesPerRoot)
                {
                    break;
                }

                try
                {
                    var attributes = (int)File.GetAttributes(path);
                    sampled++;
                    if ((attributes & FileAttributeRecallOnDataAccess) != 0)
                    {
                        recallOnDataAccess++;
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
                {
                    // A single inaccessible entry does not invalidate the rest of the sample.
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // The root disappeared or became inaccessible mid-walk; the partial count already gathered is
            // still meaningful evidence and is returned rather than discarded.
        }

        return recallOnDataAccess;
    }
}
