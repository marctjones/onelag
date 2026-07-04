using System.Diagnostics;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

public sealed class WindowsPlatformProbe : PortablePlatformProbe
{
    private const long LargeOneDriveLogStoreBytes = 250L * 1024 * 1024;
    private const int MaxEventLogEventsPerChannel = 200;
    private const int EventLogQueryTimeoutMilliseconds = 5_000;

    public override TelemetrySnapshot CaptureTelemetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CaptureTelemetry();
        }

        var processes = WindowsPerformanceSampler.SampleProcessesByName("OneDrive");
        var logChurn = CountOneDriveLogChurn();
        var version = processes.Select(sample => TryGetFileVersion(sample.Path)).FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
        var evidenceState = "windows-process-cpu-and-log-metadata";
        return new TelemetrySnapshot(DateTimeOffset.UtcNow, processes, logChurn, version, evidenceState);
    }

    public override SystemPressureSnapshot CaptureSystemPressure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CaptureSystemPressure();
        }

        var signals = WindowsPerformanceSampler.CaptureSignals();
        var topProcessSamples = WindowsPerformanceSampler.SampleTopProcesses(20);
        var topProcesses = topProcessSamples
            .Select(process => $"{process.Name}:{process.ProcessId}:cpu={process.CpuPercent:N1}:ws={process.WorkingSetBytes}")
            .ToArray();

        var driveState = TryGetSystemDriveState();
        return new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            DescribeCpuState(signals),
            DescribeMemoryState(signals),
            DescribeDiskState(signals, driveState),
            WindowsPerformanceSampler.CapturePowerState(),
            topProcesses,
            signals.Any(signal => signal.Value.HasValue)
                ? "windows-pdh-process-and-win32-memory-snapshot"
                : "windows-performance-counters-unavailable",
            signals,
            topProcessSamples);
    }

    public override OneDriveClientHealthSnapshot CaptureOneDriveClientHealth(IReadOnlyList<RootCandidate> roots, TelemetrySnapshot telemetry)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.CaptureOneDriveClientHealth(roots, telemetry);
        }

        var signals = new List<ClientHealthSignal>
        {
            new(
                Severity.Info,
                "internal-sync-database-not-parsed",
                "OneDrive internal sync databases are undocumented; OneLag inspects metadata and supported reset options instead of parsing or editing them.",
                "Do not edit OneDrive DAT or database files directly.")
        };

        var resetCommands = FindResetCommands();
        if (resetCommands.Count == 0)
        {
            signals.Add(new ClientHealthSignal(
                roots.Count > 0 ? Severity.Warning : Severity.Info,
                "reset-command-not-found",
                "No supported OneDrive reset executable path was found in the standard install locations.",
                "Use Microsoft Store app reset or reinstall guidance if OneDrive is installed differently."));
        }
        else
        {
            signals.Add(new ClientHealthSignal(
                Severity.Info,
                "reset-command-available",
                $"{resetCommands.Count:N0} supported OneDrive reset command candidate(s) were found.",
                "Review the dry-run plan before executing a reset."));
        }

        if (roots.Count > 0 && telemetry.OneDriveProcesses.Count == 0)
        {
            signals.Add(new ClientHealthSignal(
                Severity.Info,
                "onedrive-process-not-running",
                "Local OneDrive sync roots were detected, but no OneDrive process was running at scan time.",
                "This can be normal when OneDrive is paused, signed out, not started, or managed by work policy."));
        }

        if (telemetry.OneDriveLogFilesChangedLastMinute >= 10)
        {
            signals.Add(new ClientHealthSignal(
                Severity.Warning,
                "onedrive-log-churn-high",
                $"{telemetry.OneDriveLogFilesChangedLastMinute:N0} OneDrive log files changed in the last minute.",
                "Consider reset only if sync is stuck after checking visible OneDrive status and work policy."));
        }

        AddLogStoreSignals(signals);
        AddSettingsStoreSignals(signals);

        return new OneDriveClientHealthSnapshot(
            DateTimeOffset.UtcNow,
            false,
            OperatingSystem.IsWindows() ? "windows-metadata-only-undocumented-database-not-parsed" : "portable-fallback",
            signals,
            resetCommands);
    }

    public override IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(DateTimeOffset since)
    {
        if (!OperatingSystem.IsWindows())
        {
            return base.ReadRecentEventSummaries(since);
        }

        var window = DateTimeOffset.UtcNow - since.ToUniversalTime();
        if (window <= TimeSpan.Zero)
        {
            window = TimeSpan.FromMinutes(10);
        }

        if (window > TimeSpan.FromHours(24))
        {
            window = TimeSpan.FromHours(24);
        }

        var milliseconds = Math.Max(1, (long)window.TotalMilliseconds);
        var summaries = new List<EventLogSummary>();
        summaries.AddRange(ReadRecentEventSummaries("System", milliseconds));
        summaries.AddRange(ReadRecentEventSummaries("Application", milliseconds));

        return summaries
            .GroupBy(summary => (summary.LogName, summary.Provider, summary.EventId, summary.Level))
            .Select(group => new EventLogSummary(
                group.Key.LogName,
                group.Key.Provider,
                group.Key.EventId,
                group.Key.Level,
                group.Sum(summary => summary.Count),
                group.Select(summary => summary.NewestTimestamp).Max()))
            .OrderByDescending(summary => summary.NewestTimestamp)
            .ThenByDescending(summary => summary.Count)
            .ThenBy(summary => summary.Provider, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToArray();
    }

    public override string? GetForegroundProcessName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
            {
                return null;
            }

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static string? TryGetFileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int CountOneDriveLogChurn()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return 0;
        }

        var logRoot = Path.Combine(localAppData, "Microsoft", "OneDrive", "logs");
        if (!Directory.Exists(logRoot))
        {
            return 0;
        }

        var cutoff = DateTimeOffset.Now.AddMinutes(-1);
        try
        {
            return Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories)
                .Take(10_000)
                .Count(path => File.GetLastWriteTime(path) >= cutoff.LocalDateTime);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<OneDriveResetCommand> FindResetCommands()
    {
        var commands = new List<OneDriveResetCommand>();
        foreach (var candidate in GetOneDriveExecutableCandidates())
        {
            if (File.Exists(candidate.Path))
            {
                commands.Add(new OneDriveResetCommand(candidate.Path, "/reset", candidate.Source));
            }
        }

        return commands
            .DistinctBy(command => command.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<(string Path, string Source)> GetOneDriveExecutableCandidates()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return (Path.Combine(localAppData, "Microsoft", "OneDrive", "OneDrive.exe"), "local-appdata");
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return (Path.Combine(programFiles, "Microsoft OneDrive", "OneDrive.exe"), "program-files");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return (Path.Combine(programFilesX86, "Microsoft OneDrive", "OneDrive.exe"), "program-files-x86");
        }
    }

    private static void AddLogStoreSignals(List<ClientHealthSignal> signals)
    {
        var logRoot = GetOneDriveLogRoot();
        if (logRoot is null)
        {
            signals.Add(new ClientHealthSignal(
                Severity.Info,
                "onedrive-log-root-unavailable",
                "OneDrive log root could not be resolved from LocalAppData.",
                "No log metadata was read."));
            return;
        }

        if (!Directory.Exists(logRoot))
        {
            signals.Add(new ClientHealthSignal(
                Severity.Info,
                "onedrive-log-root-missing",
                "OneDrive log root was not present.",
                "This can be normal if OneDrive has not run for this profile."));
            return;
        }

        try
        {
            long count = 0;
            long bytes = 0;
            DateTime newest = DateTime.MinValue;
            foreach (var path in Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories).Take(10_000))
            {
                var info = new FileInfo(path);
                count++;
                bytes += Math.Max(0, info.Length);
                if (info.LastWriteTimeUtc > newest)
                {
                    newest = info.LastWriteTimeUtc;
                }
            }

            var severity = bytes >= LargeOneDriveLogStoreBytes ? Severity.Warning : Severity.Info;
            signals.Add(new ClientHealthSignal(
                severity,
                "onedrive-log-store-metadata",
                $"OneDrive log store has {count:N0} sampled file(s), {bytes:N0} byte(s), newest UTC write {(newest == DateTime.MinValue ? "unknown" : newest.ToString("O"))}.",
                "Only metadata was read; log contents were not captured."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            signals.Add(new ClientHealthSignal(
                Severity.Warning,
                "onedrive-log-store-inaccessible",
                "OneDrive log store metadata could not be read.",
                "Access denied or disappearing files can indicate a managed or actively changing client state."));
        }
    }

    private static void AddSettingsStoreSignals(List<ClientHealthSignal> signals)
    {
        var settingsRoot = GetOneDriveSettingsRoot();
        if (settingsRoot is null)
        {
            signals.Add(new ClientHealthSignal(
                Severity.Info,
                "onedrive-settings-root-unavailable",
                "OneDrive settings root could not be resolved from LocalAppData.",
                "No settings metadata was read."));
            return;
        }

        if (!Directory.Exists(settingsRoot))
        {
            signals.Add(new ClientHealthSignal(
                Severity.Info,
                "onedrive-settings-root-missing",
                "OneDrive settings root was not present.",
                "This can be normal if OneDrive has not run for this profile."));
            return;
        }

        try
        {
            var datFiles = Directory.EnumerateFiles(settingsRoot, "*.dat", SearchOption.AllDirectories)
                .Take(10_000)
                .Select(path => new FileInfo(path))
                .ToArray();
            var zeroByteDatFiles = datFiles.Count(file => file.Exists && file.Length == 0);
            var newest = datFiles
                .Where(file => file.Exists)
                .Select(file => file.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            signals.Add(new ClientHealthSignal(
                zeroByteDatFiles > 0 ? Severity.Warning : Severity.Info,
                "onedrive-settings-dat-metadata",
                $"OneDrive settings store has {datFiles.Length:N0} DAT file(s), {zeroByteDatFiles:N0} zero-byte DAT file(s), newest UTC write {(newest == DateTime.MinValue ? "unknown" : newest.ToString("O"))}.",
                "Only metadata was read; DAT file contents were not parsed or modified."));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            signals.Add(new ClientHealthSignal(
                Severity.Warning,
                "onedrive-settings-store-inaccessible",
                "OneDrive settings store metadata could not be read.",
                "A supported OneDrive reset is safer than directly editing unreadable cache files."));
        }
    }

    private static string? GetOneDriveLogRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "Microsoft", "OneDrive", "logs");
    }

    private static string? GetOneDriveSettingsRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "Microsoft", "OneDrive", "settings");
    }

    private static string TryGetSystemDriveState()
    {
        try
        {
            var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var root = Path.GetPathRoot(string.IsNullOrWhiteSpace(systemRoot) ? Environment.CurrentDirectory : systemRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return "unknown";
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? $"free={drive.AvailableFreeSpace};total={drive.TotalSize};format={drive.DriveFormat}" : "drive-not-ready";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static string DescribeCpuState(IReadOnlyList<PerformanceSignal> signals)
    {
        var cpu = Value(signals, "processor-total-percent");
        var queue = Value(signals, "processor-queue-length");
        return $"processor={Format(cpu, "unknown")}% ; queue={Format(queue, "unknown")}";
    }

    private static string DescribeMemoryState(IReadOnlyList<PerformanceSignal> signals)
    {
        var available = Value(signals, "memory-available-mb");
        var commit = Value(signals, "memory-commit-percent");
        var paging = Value(signals, "paging-file-usage-percent");
        return $"available={Format(available, "unknown")}MB ; commit={Format(commit, "unknown")}% ; paging={Format(paging, "unknown")}%";
    }

    private static string DescribeDiskState(IReadOnlyList<PerformanceSignal> signals, string driveState)
    {
        var queue = Value(signals, "physical-disk-queue-length");
        var active = Value(signals, "physical-disk-active-percent");
        var bytes = Value(signals, "physical-disk-bytes-per-second");
        return $"queue={Format(queue, "unknown")} ; active={Format(active, "unknown")}% ; bytesPerSecond={Format(bytes, "unknown")} ; systemDrive={driveState}";
    }

    private static double? Value(IEnumerable<PerformanceSignal> signals, string kind)
    {
        return signals.FirstOrDefault(signal => signal.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string Format(double? value, string fallback)
    {
        return value.HasValue ? value.Value.ToString("N1") : fallback;
    }

    private static IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(string logName, long milliseconds)
    {
        var query = $"*[System[TimeCreated[timediff(@SystemTime) <= {milliseconds}] and (Level=1 or Level=2 or Level=3)]]";
        try
        {
            using var process = Process.Start(CreateWevtutilStartInfo(logName, query));
            if (process is null)
            {
                return Array.Empty<EventLogSummary>();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(EventLogQueryTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return Array.Empty<EventLogSummary>();
            }

            if (process.ExitCode != 0)
            {
                _ = error.GetAwaiter().GetResult();
                return Array.Empty<EventLogSummary>();
            }

            return EventLogXmlParser.Parse(logName, output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Array.Empty<EventLogSummary>();
        }
    }

    private static ProcessStartInfo CreateWevtutilStartInfo(string logName, string query)
    {
        var startInfo = new ProcessStartInfo("wevtutil")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("qe");
        startInfo.ArgumentList.Add(logName);
        startInfo.ArgumentList.Add($"/q:{query}");
        startInfo.ArgumentList.Add("/f:xml");
        startInfo.ArgumentList.Add($"/c:{MaxEventLogEventsPerChannel}");
        startInfo.ArgumentList.Add("/rd:true");
        return startInfo;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
