using System.Diagnostics;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

public sealed class WindowsPlatformProbe : PortablePlatformProbe
{
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
