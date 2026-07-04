using System.Diagnostics;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

public sealed class WindowsPlatformProbe : PortablePlatformProbe
{
    private const long LargeOneDriveLogStoreBytes = 250L * 1024 * 1024;

    public override TelemetrySnapshot CaptureTelemetry()
    {
        var processes = new List<ProcessSample>();
        foreach (var process in Process.GetProcessesByName("OneDrive"))
        {
            using (process)
            {
                try
                {
                    processes.Add(new ProcessSample(
                        process.ProcessName,
                        process.Id,
                        process.WorkingSet64,
                        process.TotalProcessorTime,
                        TryGetProcessPath(process)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Process exited or access was denied between enumeration and read.
                }
            }
        }

        var logChurn = CountOneDriveLogChurn();
        var version = processes.Select(sample => TryGetFileVersion(sample.Path)).FirstOrDefault(version => !string.IsNullOrWhiteSpace(version));
        var evidenceState = OperatingSystem.IsWindows() ? "windows-process-and-log-metadata" : "portable-fallback";
        return new TelemetrySnapshot(DateTimeOffset.UtcNow, processes, logChurn, version, evidenceState);
    }

    public override SystemPressureSnapshot CaptureSystemPressure()
    {
        var topProcesses = Process.GetProcesses()
            .Select(TrySummarizeProcess)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Cast<string>()
            .ToArray();

        var driveState = TryGetSystemDriveState();
        return new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            "sampled-process-list-only",
            "unknown",
            driveState,
            "unknown",
            topProcesses,
            OperatingSystem.IsWindows() ? "windows-low-cost-snapshot" : "portable-fallback");
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
        _ = since;
        return Array.Empty<EventLogSummary>();
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

    private static string? TrySummarizeProcess(Process process)
    {
        using (process)
        {
            try
            {
                return $"{process.ProcessName}:{process.Id}:ws={process.WorkingSet64}";
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
