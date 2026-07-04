namespace OneLag.Core;

public interface IPlatformProbe
{
    IReadOnlyList<RootCandidate> DiscoverOneDriveRoots();

    TelemetrySnapshot CaptureTelemetry();

    SystemPressureSnapshot CaptureSystemPressure();

    IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(DateTimeOffset since);

    string? GetForegroundProcessName();
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

    public virtual IReadOnlyList<EventLogSummary> ReadRecentEventSummaries(DateTimeOffset since)
    {
        _ = since;
        return Array.Empty<EventLogSummary>();
    }

    public virtual string? GetForegroundProcessName() => null;

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
