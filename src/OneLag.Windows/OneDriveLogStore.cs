namespace OneLag.Windows;

public sealed record OneDriveLogChurn(
    int FilesChangedLastMinute,
    int FilesSampled,
    bool Truncated,
    string EvidenceState);

/// <summary>
/// Reads OneDrive's log store as a filesystem, not as logs.
///
/// OneDrive writes `.odl`, `.odlgz`, and `.odlsent` files under
/// `%LocalAppData%\Microsoft\OneDrive\logs\{Personal,Business1,...}`. The format is binary, obfuscated, and
/// undocumented, and parsing it is an explicit non-goal of this project. What the source guide actually
/// relies on is churn: more than five log files written per minute indicates the sync engine is thrashing.
/// That is pure file metadata, so it is separated from the platform probe here — with the log root and the
/// clock injected — so it can be tested against a synthetic log store on any operating system.
/// </summary>
public static class OneDriveLogStore
{
    /// <summary>
    /// A pathological log store should not turn a diagnostic scan into a directory walk of a hundred
    /// thousand files. When the cap is hit the result says so rather than presenting a truncated count as a
    /// complete one.
    /// </summary>
    public const int MaxSampledFiles = 10_000;

    public static string? DefaultLogRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        return Path.Combine(localAppData, "Microsoft", "OneDrive", "logs");
    }

    public static OneDriveLogChurn Measure(string? logRoot, DateTimeOffset now, TimeSpan? window = null)
    {
        var churnWindow = window ?? TimeSpan.FromMinutes(1);

        if (string.IsNullOrWhiteSpace(logRoot))
        {
            return new OneDriveLogChurn(0, 0, false, "onedrive-log-root-unknown");
        }

        if (!Directory.Exists(logRoot))
        {
            // No log store is not the same as no churn. Reporting zero here would look like a measured
            // "OneDrive is quiet" when nothing was measured at all.
            return new OneDriveLogChurn(0, 0, false, "onedrive-log-root-not-found");
        }

        var cutoff = now - churnWindow;

        try
        {
            var changed = 0;
            var sampled = 0;
            var truncated = false;

            foreach (var path in Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories))
            {
                if (sampled >= MaxSampledFiles)
                {
                    truncated = true;
                    break;
                }

                sampled++;

                DateTimeOffset lastWrite;
                try
                {
                    lastWrite = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The sync engine rotates these files while we walk them. A file that vanished mid-walk
                    // is not evidence of anything.
                    continue;
                }

                // A clock that jumped, or a file written "in the future", must not be counted as churn that
                // just happened.
                if (lastWrite >= cutoff && lastWrite <= now)
                {
                    changed++;
                }
            }

            var evidenceState = truncated
                ? $"onedrive-log-metadata-truncated-at-{MaxSampledFiles}"
                : "onedrive-log-metadata";

            return new OneDriveLogChurn(changed, sampled, truncated, evidenceState);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return new OneDriveLogChurn(0, 0, false, "onedrive-log-root-unreadable");
        }
    }
}
