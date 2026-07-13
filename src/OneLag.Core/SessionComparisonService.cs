using System.Text;

namespace OneLag.Core;

public sealed record ComparedSession(
    string Name,
    string Directory,
    int Samples,
    int Markers,
    int Episodes,
    double MaxTimerDriftMilliseconds,
    IReadOnlyList<string> Configurations);

public sealed record SessionComparison(
    IReadOnlyList<ComparedSession> Sessions,
    ContextCorrelationResult Correlation,
    IReadOnlyList<string> TopDrivers);

/// <summary>
/// Compares watch sessions recorded in different hardware configurations.
///
/// This is the A/B the user has usually already run by accident — fine undocked in the office, slow docked at
/// home — made from recorded evidence instead of memory. Sessions are pooled and their episodes grouped by the
/// configuration they actually happened in, so lag that tracks the dock rather than the sync load is visible
/// as a difference in episodes per hour rather than an impression.
/// </summary>
public sealed class SessionComparisonService
{
    private readonly WatchService watchService;

    public SessionComparisonService(WatchService watchService)
    {
        this.watchService = watchService;
    }

    public SessionComparison Compare(IReadOnlyList<string> directories)
    {
        ArgumentNullException.ThrowIfNull(directories);
        if (directories.Count == 0)
        {
            throw new ArgumentException("At least one watch session directory is required.", nameof(directories));
        }

        var sessions = new List<ComparedSession>();
        var pooledSamples = new List<WatchSample>();
        var pooledMarkers = new List<WatchMarker>();

        foreach (var directory in directories)
        {
            var full = Path.GetFullPath(directory);
            var samples = watchService.ReadSamples(full);
            var markers = watchService.ReadMarkers(full);
            var episodes = WatchEpisodeDetector.Detect(samples, markers);

            sessions.Add(new ComparedSession(
                Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)),
                full,
                samples.Count,
                markers.Count,
                episodes.Count,
                samples.Count == 0 ? 0 : samples.Max(sample => sample.TimerDriftMilliseconds),
                samples
                    .Where(sample => sample.HostContext is not null)
                    .Select(WatchContextCorrelation.DescribeContext)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(context => context, StringComparer.Ordinal)
                    .ToArray()));

            pooledSamples.AddRange(samples);
            pooledMarkers.AddRange(markers);
        }

        var ordered = pooledSamples.OrderBy(sample => sample.Timestamp).ToArray();
        var pooledEpisodes = WatchEpisodeDetector.Detect(ordered, pooledMarkers);
        var correlation = WatchContextCorrelation.Correlate(ordered, pooledEpisodes, InferInterval(ordered));

        return new SessionComparison(sessions, correlation, Array.Empty<string>());
    }

    public string BuildReport(SessionComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var builder = new StringBuilder();
        builder.AppendLine("# OneLag Session Comparison");
        builder.AppendLine();

        builder.AppendLine("## Sessions");
        builder.AppendLine();
        builder.AppendLine("| Session | Samples | Markers | Episodes | Max drift | Configurations |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var session in comparison.Sessions)
        {
            var configurations = session.Configurations.Count == 0
                ? "none captured"
                : string.Join("; ", session.Configurations);
            builder.AppendLine($"| {session.Name} | {session.Samples:N0} | {session.Markers:N0} | {session.Episodes:N0} | {session.MaxTimerDriftMilliseconds:N0} ms | {configurations} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Lag By Configuration");
        builder.AppendLine();

        if (comparison.Correlation.Buckets.Count == 0)
        {
            builder.AppendLine("No host context was captured in any session, so lag cannot be attributed to the machine's configuration. Re-record with a build that captures display, dock, and Bluetooth state.");
            return builder.ToString();
        }

        builder.AppendLine("| Configuration | Samples | Observed | Episodes | Episodes/hour | Max drift | Max DPC |");
        builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var bucket in comparison.Correlation.Buckets)
        {
            var dpc = bucket.MaxDpcPercent.HasValue ? $"{bucket.MaxDpcPercent.Value:N1}%" : "unknown";
            builder.AppendLine($"| {bucket.Context} | {bucket.Samples:N0} | {bucket.Observed:hh\\:mm\\:ss} | {bucket.Episodes:N0} | {bucket.EpisodesPerHour:N1} | {bucket.MaxTimerDriftMilliseconds:N0} ms | {dpc} |");
        }

        if (!string.IsNullOrWhiteSpace(comparison.Correlation.Conclusion))
        {
            builder.AppendLine();
            builder.AppendLine("## Conclusion");
            builder.AppendLine();
            builder.AppendLine(comparison.Correlation.Conclusion);
        }

        builder.AppendLine();
        builder.AppendLine("## Next Step");
        builder.AppendLine();
        builder.AppendLine("If lag concentrates in one configuration, run `onelag trace dpc` from an elevated terminal *while in that configuration* to name the driver responsible.");

        return builder.ToString();
    }

    private static TimeSpan InferInterval(IReadOnlyList<WatchSample> samples)
    {
        if (samples.Count < 2)
        {
            return TimeSpan.FromSeconds(2);
        }

        var deltas = samples
            .Zip(samples.Skip(1), (first, second) => (second.Timestamp - first.Timestamp).TotalSeconds)
            .Where(delta => delta is > 0 and < 300)
            .OrderBy(delta => delta)
            .ToArray();

        return deltas.Length == 0
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(deltas[deltas.Length / 2]);
    }
}
