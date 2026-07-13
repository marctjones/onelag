using System.Diagnostics;
using System.Text;
using OneLag.Core;

namespace OneLag.Windows;

public sealed record LogCollectionScope(
    TimeSpan EventWindow,
    bool IncludeWindowsTree = true,
    bool IncludeCrashDumps = true,
    bool IncludeSystemInfo = true,
    bool AllEventChannels = false);

/// <summary>
/// Enumerates the actual log sources on a Windows machine so analysis can run over real bytes.
///
/// This yields items lazily and never throws for a single unreadable file or an empty channel — collection
/// of "everything" over the Windows tree means constantly hitting files held open or access-denied, and one
/// of those must not abort the walk. Bounding is the caller's job (LogCollectionService caps by size and
/// count); this is deliberately generous about what it offers up.
/// </summary>
public sealed class WindowsLogCollector
{
    private static readonly string[] OneDriveLogExtensions = { ".odl", ".odlgz", ".odlsent", ".aodl", ".aold" };

    /// <summary>
    /// Directories under the Windows tree that hold the logs worth having, collected in full. The broad
    /// recursive `*.log` sweep catches the rest; these guarantee the high-value ones are not lost to the
    /// count cap first.
    /// </summary>
    private static readonly string[] WindowsLogSubtrees =
    {
        @"Logs",
        @"Panther",
        @"debug",
        @"System32\LogFiles",
        @"System32\sru",
        @"System32\winevt\Logs",
        @"ServiceProfiles\LocalService\AppData\Local\Temp",
        @"SoftwareDistribution\DataStore\Logs",
        @"INF"
    };

    /// <summary>
    /// A broad, bounded set of channels that matter for lag, hardware, drivers, and sync. Use
    /// <see cref="LogCollectionScope.AllEventChannels"/> for literally every channel.
    /// </summary>
    private static readonly string[] CoreEventChannels =
    {
        "System",
        "Application",
        "Setup",
        "Security",
        "Microsoft-Windows-Kernel-Power/Thermal-Operational",
        "Microsoft-Windows-Kernel-PnP/Configuration",
        "Microsoft-Windows-Kernel-PnP/Device Configuration",
        "Microsoft-Windows-Kernel-WHEA/Errors",
        "Microsoft-Windows-Kernel-WHEA/Operational",
        "Microsoft-Windows-WHEA-Logger/Operational",
        "Microsoft-Windows-StorPort/Health",
        "Microsoft-Windows-Storage-Storport/Operational",
        "Microsoft-Windows-Storage-Storport/Health",
        "Microsoft-Windows-Disk/Operational",
        "Microsoft-Windows-Ntfs/Operational",
        "Microsoft-Windows-Bluetooth-BthLEServices/Operational",
        "Microsoft-Windows-Bluetooth-BthMini/Operational",
        "Microsoft-Windows-WLAN-AutoConfig/Operational",
        "Microsoft-Windows-Dwm-Core/Diagnostic",
        "Microsoft-Windows-DeviceSetupManager/Admin",
        "Microsoft-Windows-DeviceSetupManager/Operational",
        "Microsoft-Windows-DriverFrameworks-UserMode/Operational",
        "Microsoft-Windows-UserPnp/DeviceInstall",
        "Microsoft-Windows-WindowsUpdateClient/Operational",
        "Microsoft-Windows-Windows Defender/Operational",
        "Microsoft-Windows-Diagnostics-Performance/Operational",
        "Microsoft-Windows-Kernel-Boot/Operational",
        "Microsoft-Windows-Kernel-Processor-Power/Diagnostic",
        "Microsoft-Windows-USB-USBHUB3-Analytic",
        "Microsoft-Windows-Display/Operational"
    };

    public IEnumerable<CollectionItem> Enumerate(LogCollectionScope scope, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new TextCollectionItem(
                CollectionCategory.SystemInfo,
                "collection-note.txt",
                "Log collection ran on a non-Windows host, so no Windows logs could be collected.");
            yield break;
        }

        foreach (var item in EnumerateOneDriveLogs())
        {
            yield return item;
        }

        foreach (var item in EnumerateEventLogs(scope, now))
        {
            yield return item;
        }

        if (scope.IncludeSystemInfo)
        {
            foreach (var item in EnumerateSystemInfo())
            {
                yield return item;
            }
        }

        if (scope.IncludeCrashDumps)
        {
            foreach (var item in EnumerateCrashDumps())
            {
                yield return item;
            }
        }

        if (scope.IncludeWindowsTree)
        {
            foreach (var item in EnumerateWindowsTree())
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<CollectionItem> EnumerateOneDriveLogs()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            yield break;
        }

        var logRoot = Path.Combine(localAppData, "Microsoft", "OneDrive", "logs");
        if (!Directory.Exists(logRoot))
        {
            yield break;
        }

        foreach (var path in EnumerateFilesSafely(logRoot, "*"))
        {
            if (OneDriveLogExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return new FileCollectionItem(
                    CollectionCategory.OneDriveLog,
                    Path.Combine("onedrive", RelativeTo(logRoot, path)),
                    path);
            }
        }
    }

    private IEnumerable<CollectionItem> EnumerateEventLogs(LogCollectionScope scope, DateTimeOffset now)
    {
        var windowMs = (long)scope.EventWindow.TotalMilliseconds;
        var query = $"*[System[TimeCreated[timediff(@SystemTime) <= {windowMs}]]]";

        var channels = scope.AllEventChannels ? ListAllChannels() : CoreEventChannels;

        foreach (var channel in channels)
        {
            var (content, ok) = ExportChannel(channel, query);
            if (!ok)
            {
                continue;
            }

            yield return new TextCollectionItem(
                CollectionCategory.EventLog,
                Path.Combine("eventlogs", SafeChannelFileName(channel) + ".xml"),
                content,
                $"wevtutil:{channel}");
        }
    }

    private static IReadOnlyList<string> ListAllChannels()
    {
        var (output, ok) = RunProcess("wevtutil", "el");
        if (!ok)
        {
            return CoreEventChannels;
        }

        return output
            .Split('\n', '\r')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
    }

    private static (string Content, bool Ok) ExportChannel(string channel, string query)
    {
        var (output, ok) = RunProcess("wevtutil", $"qe \"{channel}\" \"/q:{query}\" /f:RenderedXml /e:Events");

        // An empty or disabled channel is normal, not a failure worth surfacing. Only channels that actually
        // yielded events are worth a file.
        if (!ok || string.IsNullOrWhiteSpace(output) || !output.Contains("<Event", StringComparison.Ordinal))
        {
            return (string.Empty, false);
        }

        return (output, true);
    }

    private static IEnumerable<CollectionItem> EnumerateSystemInfo()
    {
        foreach (var (name, file, args, category) in new[]
        {
            ("driverquery", "drivers.csv", "/v /fo csv", CollectionCategory.SystemInfo),
            ("systeminfo", "systeminfo.txt", string.Empty, CollectionCategory.SystemInfo),
            ("powercfg", "power-requests.txt", "/requests", CollectionCategory.SystemInfo)
        })
        {
            var (output, ok) = RunProcess(name, args);
            if (ok && !string.IsNullOrWhiteSpace(output))
            {
                yield return new TextCollectionItem(category, Path.Combine("systeminfo", file), output, name);
            }
        }
    }

    private static IEnumerable<CollectionItem> EnumerateCrashDumps()
    {
        var windir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        foreach (var dumpRoot in new[]
        {
            Path.Combine(windir, "Minidump"),
            Path.Combine(windir, "LiveKernelReports")
        })
        {
            if (!Directory.Exists(dumpRoot))
            {
                continue;
            }

            foreach (var path in EnumerateFilesSafely(dumpRoot, "*.dmp"))
            {
                yield return new FileCollectionItem(
                    CollectionCategory.CrashDump,
                    Path.Combine("crashdumps", RelativeTo(dumpRoot, path)),
                    path);
            }
        }
    }

    private static IEnumerable<CollectionItem> EnumerateWindowsTree()
    {
        var windir = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        if (!Directory.Exists(windir))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // High-value subtrees first, in full, so they are not lost to the count cap before the broad sweep.
        foreach (var subtree in WindowsLogSubtrees)
        {
            var root = Path.Combine(windir, subtree);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in EnumerateFilesSafely(root, "*"))
            {
                if (IsCollectableWindowsLog(path) && seen.Add(path))
                {
                    yield return WindowsTreeItem(windir, path);
                }
            }
        }

        // Then a broad recursive sweep of the whole tree for anything with a log-like extension.
        foreach (var path in EnumerateFilesSafely(windir, "*"))
        {
            if (IsCollectableWindowsLog(path) && seen.Add(path))
            {
                yield return WindowsTreeItem(windir, path);
            }
        }
    }

    private static bool IsCollectableWindowsLog(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".etl", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && path.Contains("LogFiles", StringComparison.OrdinalIgnoreCase);
    }

    private static CollectionItem WindowsTreeItem(string windir, string path)
    {
        return new FileCollectionItem(
            CollectionCategory.WindowsLog,
            Path.Combine("windows", RelativeTo(windir, path)),
            path);
    }

    private static IEnumerable<string> EnumerateFilesSafely(string root, string pattern)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerator<string> enumerator;
        try
        {
            enumerator = Directory.EnumerateFiles(root, pattern, options).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        while (true)
        {
            string current;
            try
            {
                if (!enumerator.MoveNext())
                {
                    break;
                }

                current = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            yield return current;
        }
    }

    private static string RelativeTo(string root, string path)
    {
        return Path.GetRelativePath(root, path);
    }

    private static string SafeChannelFileName(string channel)
    {
        return channel.Replace('/', '-').Replace('\\', '-').Replace(' ', '-');
    }

    private static (string Output, bool Ok) RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };

            if (!process.Start())
            {
                return (string.Empty, false);
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }

                return (output, false);
            }

            return (output, process.ExitCode == 0);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (string.Empty, false);
        }
    }
}
