namespace OneLag.Core;

public interface IPlatformProbe
{
    IReadOnlyList<RootCandidate> DiscoverOneDriveRoots();

    TelemetrySnapshot CaptureTelemetry();

    SystemPressureSnapshot CaptureSystemPressure();

    OneDriveClientHealthSnapshot CaptureOneDriveClientHealth(IReadOnlyList<RootCandidate> roots, TelemetrySnapshot telemetry);

    IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(DateTimeOffset since);

    string? GetForegroundProcessName();

    HostContext CaptureHostContext();

    ShellResponsiveness CaptureShellResponsiveness();

    /// <summary>
    /// Runs a bounded kernel trace and attributes DPC/ISR time to driver images. Heavy and elevation-gated,
    /// so it is never part of the default scan path.
    /// </summary>
    DriverLatencyAttribution CaptureDriverLatency(TimeSpan duration, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates the file-system filter (minifilter) stack. Every file open traverses every attached filter.
    /// </summary>
    FilterDriverStack CaptureFilterDriverStack();

    /// <summary>
    /// Captures commit headroom, system uptime, the top processes by private bytes, and any process Windows
    /// itself has flagged as a leak candidate.
    /// </summary>
    MemoryPressureDetail CaptureMemoryPressure();

    /// <summary>
    /// Enumerates registered Explorer shell extensions, particularly icon overlay handlers, which run
    /// synchronously on the shell UI thread.
    /// </summary>
    ShellExtensionInventory CaptureShellExtensions();

    /// <summary>
    /// Captures where the shell's known folders actually point and whether any mapped network drive is dead —
    /// the two things that decide how expensive a native open-file dialog is.
    /// </summary>
    FileSystemContext CaptureFileSystemContext(IReadOnlyList<RootCandidate> roots);
}

public class PortablePlatformProbe : IPlatformProbe
{
    public virtual IReadOnlyList<RootCandidate> DiscoverOneDriveRoots()
    {
        var roots = new Dictionary<string, RootCandidate>(StringComparer.OrdinalIgnoreCase);
        AddEnvironmentRoot(roots, "OneDrive", "personal-or-unknown");
        AddEnvironmentRoot(roots, "OneDriveConsumer", "personal");
        AddEnvironmentRoot(roots, "OneDriveCommercial", "work-or-school");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile) && Directory.Exists(profile))
        {
            foreach (var directory in Directory.EnumerateDirectories(profile, "OneDrive*"))
            {
                roots.TryAdd(directory, new RootCandidate(directory, "user-profile", "medium", GuessAccountKind(directory)));
            }
        }

        return roots.Values.OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public virtual TelemetrySnapshot CaptureTelemetry()
    {
        return new TelemetrySnapshot(DateTimeOffset.UtcNow, Array.Empty<ProcessSample>(), 0, null, "unavailable-on-this-platform");
    }

    public virtual SystemPressureSnapshot CaptureSystemPressure()
    {
        return new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            "unknown",
            "unknown",
            "unknown",
            "unknown",
            Array.Empty<string>(),
            "portable-fallback");
    }

    public virtual OneDriveClientHealthSnapshot CaptureOneDriveClientHealth(IReadOnlyList<RootCandidate> roots, TelemetrySnapshot telemetry)
    {
        _ = roots;
        _ = telemetry;
        return new OneDriveClientHealthSnapshot(
            DateTimeOffset.UtcNow,
            false,
            "portable-fallback-undocumented-database-not-parsed",
            new[]
            {
                new ClientHealthSignal(
                    Severity.Info,
                    "internal-sync-database-not-parsed",
                    "OneDrive internal sync databases are undocumented and are not parsed by OneLag.",
                    "Use Microsoft-supported reset or repair paths instead of editing OneDrive cache files.")
            },
            Array.Empty<OneDriveResetCommand>());
    }

    public virtual IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(DateTimeOffset since)
    {
        _ = since;
        return Array.Empty<EventLogSummary>();
    }

    public virtual string? GetForegroundProcessName() => null;

    public virtual HostContext CaptureHostContext()
    {
        return HostContext.Unavailable("unavailable-on-this-platform");
    }

    public virtual ShellResponsiveness CaptureShellResponsiveness()
    {
        return ShellResponsiveness.Unavailable("unavailable-on-this-platform");
    }

    public virtual DriverLatencyAttribution CaptureDriverLatency(TimeSpan duration, CancellationToken cancellationToken)
    {
        _ = duration;
        _ = cancellationToken;
        return DriverLatencyAttribution.Unavailable("unavailable-on-this-platform");
    }

    public virtual FilterDriverStack CaptureFilterDriverStack()
    {
        return FilterDriverStack.Unavailable("unavailable-on-this-platform");
    }

    public virtual MemoryPressureDetail CaptureMemoryPressure()
    {
        return MemoryPressureDetail.Unavailable("unavailable-on-this-platform");
    }

    public virtual ShellExtensionInventory CaptureShellExtensions()
    {
        return ShellExtensionInventory.Unavailable("unavailable-on-this-platform");
    }

    public virtual FileSystemContext CaptureFileSystemContext(IReadOnlyList<RootCandidate> roots)
    {
        _ = roots;
        return FileSystemContext.Unavailable("unavailable-on-this-platform");
    }

    private static void AddEnvironmentRoot(Dictionary<string, RootCandidate> roots, string variableName, string accountKind)
    {
        var value = Environment.GetEnvironmentVariable(variableName);
        if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
        {
            roots.TryAdd(value, new RootCandidate(value, $"env:{variableName}", "high", accountKind));
        }
    }

    private static string GuessAccountKind(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains(" - ", StringComparison.Ordinal) ? "work-or-school" : "personal-or-unknown";
    }
}
