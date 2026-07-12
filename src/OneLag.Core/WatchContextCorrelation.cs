namespace OneLag.Core;

public sealed record ContextBucket(
    string Context,
    int Samples,
    TimeSpan Observed,
    int Episodes,
    double EpisodesPerHour,
    double MaxTimerDriftMilliseconds,
    double MeanTimerDriftMilliseconds,
    double? MaxDpcPercent);

public sealed record ContextCorrelationResult(
    IReadOnlyList<ContextBucket> Buckets,
    string? Conclusion);

/// <summary>
/// Correlates lag episodes against the machine's physical configuration.
///
/// This is the question a snapshot scan can never answer: does the lag follow the dock, the external
/// displays, or the Bluetooth radio? A user who is fine undocked in the office and slow docked at home has
/// already run this experiment by accident; this makes the same comparison from recorded evidence.
/// </summary>
public static class WatchContextCorrelation
{
    private const int MinimumSamplesPerBucket = 10;

    public static ContextCorrelationResult Correlate(
        IReadOnlyList<WatchSample> samples,
        IReadOnlyList<WatchEpisode> episodes,
        TimeSpan sampleInterval)
    {
        var contextual = samples.Where(sample => sample.HostContext is not null).ToArray();
        if (contextual.Length == 0)
        {
            return new ContextCorrelationResult(Array.Empty<ContextBucket>(), null);
        }

        var buckets = contextual
            .GroupBy(DescribeContext, StringComparer.Ordinal)
            .Select(group => BuildBucket(group.Key, group.ToArray(), episodes, sampleInterval))
            .OrderByDescending(bucket => bucket.EpisodesPerHour)
            .ThenBy(bucket => bucket.Context, StringComparer.Ordinal)
            .ToArray();

        return new ContextCorrelationResult(buckets, Conclude(buckets));
    }

    /// <summary>
    /// The configuration key a sample was taken under. Lag rates are compared across these.
    /// </summary>
    public static string DescribeContext(WatchSample sample)
    {
        var host = sample.HostContext;
        if (host is null)
        {
            return "unknown";
        }

        var parts = new List<string>();

        parts.Add(host.IndirectDisplayCount > 0
            ? "indirect-display"
            : host.ExternalDisplayCount > 0
                ? "external-display"
                : "internal-display-only");

        parts.Add(host.BluetoothRadioEnabled switch
        {
            true when (host.ConnectedBluetoothDevices ?? 0) > 0 => "bluetooth-connected",
            true => "bluetooth-on",
            false => "bluetooth-off",
            _ => "bluetooth-unknown"
        });

        if (host.DockState != DockStates.Unknown)
        {
            parts.Add(host.DockState);
        }

        return string.Join(" + ", parts);
    }

    private static ContextBucket BuildBucket(
        string context,
        IReadOnlyList<WatchSample> samples,
        IReadOnlyList<WatchEpisode> episodes,
        TimeSpan sampleInterval)
    {
        var observed = sampleInterval > TimeSpan.Zero
            ? TimeSpan.FromTicks(sampleInterval.Ticks * samples.Count)
            : TimeSpan.Zero;

        var windows = samples
            .Select(sample => (Start: sample.Timestamp - sampleInterval, End: sample.Timestamp))
            .ToArray();

        var episodeCount = episodes.Count(episode =>
            windows.Any(window => episode.StartedAt <= window.End && episode.FinishedAt >= window.Start));

        var hours = observed.TotalHours;
        var episodesPerHour = hours > 0 ? episodeCount / hours : 0;

        var maxDpc = samples
            .Select(sample => PressureClassifier.Value(
                sample.SystemPressure.Signals ?? Array.Empty<PerformanceSignal>(),
                "processor-dpc-percent-max-core")
                ?? PressureClassifier.Value(
                    sample.SystemPressure.Signals ?? Array.Empty<PerformanceSignal>(),
                    "processor-dpc-percent"))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .DefaultIfEmpty(double.NaN)
            .Max();

        return new ContextBucket(
            context,
            samples.Count,
            observed,
            episodeCount,
            episodesPerHour,
            samples.Max(sample => sample.TimerDriftMilliseconds),
            samples.Average(sample => sample.TimerDriftMilliseconds),
            double.IsNaN(maxDpc) ? null : maxDpc);
    }

    private static string? Conclude(IReadOnlyList<ContextBucket> buckets)
    {
        var comparable = buckets.Where(bucket => bucket.Samples >= MinimumSamplesPerBucket).ToArray();
        if (comparable.Length < 2)
        {
            return comparable.Length == 1
                ? $"The machine was only observed in one configuration (`{comparable[0].Context}`), so lag cannot yet be attributed to the dock, displays, or Bluetooth. Record a session in a different configuration to compare."
                : null;
        }

        var worst = comparable.MaxBy(bucket => bucket.EpisodesPerHour)!;
        var best = comparable.MinBy(bucket => bucket.EpisodesPerHour)!;

        if (worst.EpisodesPerHour <= 0)
        {
            return "No lag episodes were recorded in any configuration.";
        }

        if (best.EpisodesPerHour <= 0)
        {
            return $"Every lag episode happened in `{worst.Context}` ({worst.EpisodesPerHour:N1} per hour), and none at all in `{best.Context}`. The configuration itself is implicated: the difference between those two states is where to look, not OneDrive.";
        }

        var ratio = worst.EpisodesPerHour / best.EpisodesPerHour;
        if (ratio >= 3)
        {
            return $"Lag was {ratio:N1}x more frequent in `{worst.Context}` ({worst.EpisodesPerHour:N1} per hour) than in `{best.Context}` ({best.EpisodesPerHour:N1} per hour). The hardware configuration, not sync load, tracks the lag.";
        }

        return $"Lag rates were comparable across configurations (`{worst.Context}` {worst.EpisodesPerHour:N1}/h vs `{best.Context}` {best.EpisodesPerHour:N1}/h), so the dock, displays, and Bluetooth do not by themselves explain the difference.";
    }
}
