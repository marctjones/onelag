namespace OneLag.Core;

/// <summary>
/// The growth of one series (a process's private bytes, or the machine's commit) over the session.
/// </summary>
public sealed record MemoryGrowthSeries(
    string Name,
    int? ProcessId,
    long StartBytes,
    long EndBytes,
    double MegabytesPerHour,
    int Samples,
    TimeSpan Span)
{
    public long DeltaBytes => EndBytes - StartBytes;
}

public sealed record MemoryTrend(
    bool HasSufficientData,
    string Summary,
    int MemorySamples,
    TimeSpan Span,
    MemoryGrowthSeries? Commit,
    MemoryGrowthSeries? UnaccountedCommit,
    IReadOnlyList<MemoryGrowthSeries> GrowingProcesses,
    bool KernelLeakSuspected,
    IReadOnlyList<MemoryLeakCandidate> WindowsLeakCandidates);

/// <summary>
/// Finds what is growing.
///
/// A snapshot can say the machine is holding 21.9 GB. It cannot say what is filling it, because size and guilt
/// are different things: a browser sitting at 4 GB and flat is innocent, while a service climbing 200 MB an
/// hour is the leak even though it is a tenth the size. Only a time series separates the two, and that is the
/// entire diagnostic value of this analyzer — everything here ranks by growth rate, never by size.
///
/// The most important number it computes is the growth of *unaccounted* commit: memory the machine has
/// committed that no user-mode process claims. If every process is flat and that figure is the one climbing,
/// the leak is in the kernel — a driver holding pool — and no process list will ever show it, because Task
/// Manager's Details tab enumerates user-mode processes only. That case is called out explicitly rather than
/// left for the reader to infer from an absence.
///
/// Pure and side-effect free: it takes the recorded samples and returns a verdict, so the reasoning can be
/// tested against synthesized machines instead of waiting a day for a real one.
/// </summary>
public static class MemoryTrendAnalyzer
{
    private const double BytesPerMegabyte = 1024 * 1024;

    /// <summary>
    /// A linear fit needs at least three points to be a fit rather than a line drawn through two dots. Two
    /// samples cannot distinguish a trend from a single step, and reporting a rate from them would be a
    /// confident answer built on nothing.
    /// </summary>
    public const int MinimumSamples = 3;

    /// <summary>
    /// Extrapolating an hourly rate from a few minutes of data manufactures enormous numbers from ordinary
    /// allocation noise (a process that happens to allocate 20 MB inside a 2-minute window "grows" at
    /// 600 MB/hour). Ten minutes is the shortest window over which an hourly rate is worth printing.
    /// </summary>
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Below this, a process is doing its job, not leaking. Working sets and heaps breathe by tens of megabytes
    /// as caches fill and GCs run; only sustained growth above that noise floor is evidence of anything.
    /// </summary>
    public const double ProcessGrowthNoiseFloorMegabytesPerHour = 50;

    /// <summary>
    /// Unaccounted commit climbing this fast is a leak the user cannot close his way out of: at 100 MB/hour an
    /// overnight machine loses a gigabyte to something that owns no window and appears in no process list.
    /// </summary>
    public const double KernelLeakMegabytesPerHour = 100;

    /// <summary>
    /// The kernel verdict is only honest if the kernel is where the growth actually is. Requiring the
    /// unaccounted growth to be at least twice the total growth of every process together stops a process leak
    /// from being blamed on a driver merely because the accounting is noisy.
    /// </summary>
    public const double KernelLeakDominanceRatio = 2.0;

    public static MemoryTrend Analyze(IReadOnlyList<WatchSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        // Memory is sampled on a slower cadence than the loop ticks, so most samples carry no memory at all. A
        // null here means "not sampled", never "nothing to report", and must not be read as a zero.
        var memories = samples
            .Where(sample => sample.Memory is not null)
            .Select(sample => sample.Memory!)
            .OrderBy(memory => memory.Timestamp)
            .ToArray();

        var span = memories.Length >= 2 ? memories[^1].Timestamp - memories[0].Timestamp : TimeSpan.Zero;

        if (memories.Length < MinimumSamples || span < MinimumSpan)
        {
            return new MemoryTrend(
                false,
                $"Not enough memory samples to fit a trend: {memories.Length} sample(s) over {span.TotalMinutes:N0} min. " +
                $"At least {MinimumSamples} samples spanning {MinimumSpan.TotalMinutes:N0} min are required before a growth rate means anything. Record for longer.",
                memories.Length,
                span,
                null,
                null,
                Array.Empty<MemoryGrowthSeries>(),
                false,
                CollectLeakCandidates(memories));
        }

        var commit = Fit("committed memory", null, memories, memory => memory.CommitTotalBytes);
        var unaccounted = Fit("unaccounted commit", null, memories, memory => memory.UnaccountedCommitBytes);
        var processes = FitProcesses(memories);

        var growing = processes
            .Where(series => series.MegabytesPerHour >= ProcessGrowthNoiseFloorMegabytesPerHour)
            .OrderByDescending(series => series.MegabytesPerHour)
            .ToArray();

        var processGrowth = growing.Sum(series => series.MegabytesPerHour);
        var kernelLeak = unaccounted is not null
            && unaccounted.MegabytesPerHour >= KernelLeakMegabytesPerHour
            && unaccounted.MegabytesPerHour >= processGrowth * KernelLeakDominanceRatio;

        return new MemoryTrend(
            true,
            Summarize(commit, unaccounted, growing, kernelLeak),
            memories.Length,
            span,
            commit,
            unaccounted,
            growing,
            kernelLeak,
            CollectLeakCandidates(memories));
    }

    private static string Summarize(
        MemoryGrowthSeries? commit,
        MemoryGrowthSeries? unaccounted,
        IReadOnlyList<MemoryGrowthSeries> growing,
        bool kernelLeak)
    {
        if (kernelLeak)
        {
            return $"Committed memory that belongs to no user-mode process grew at {unaccounted!.MegabytesPerHour:N0} MB/hour " +
                $"while no process grew fast enough to explain it. This is a kernel or driver leak: closing applications cannot return this memory, and it will not appear anywhere in Task Manager.";
        }

        if (growing.Count > 0)
        {
            var worst = growing[0];
            return $"`{worst.Name}` grew at {worst.MegabytesPerHour:N0} MB/hour, the fastest-growing process in the session. " +
                "Growth, not size, identifies a leak: a large process that stays flat is not the problem.";
        }

        if (commit is not null && commit.MegabytesPerHour >= KernelLeakMegabytesPerHour)
        {
            return $"Committed memory grew at {commit.MegabytesPerHour:N0} MB/hour, but no single process and no unaccounted-commit trend dominates it. The growth is spread across the machine.";
        }

        return "No process and no kernel allocation grew fast enough over this session to be called a leak.";
    }

    /// <summary>
    /// Series are keyed on name and PID together because Windows reuses PIDs freely: a PID that belonged to a
    /// short-lived process and is later handed to a different one would otherwise splice two unrelated
    /// footprints into a single "trend" and invent growth that never happened. When a PID is seen under more
    /// than one name, both series are dropped entirely — a fabricated leak is far worse than a missed one, and
    /// the same process will still be caught by its successor's own series if it really is leaking.
    ///
    /// A limitation worth stating: the recorder retains only the top processes by size per sample, so a small
    /// process that is climbing does not enter the series until it grows large enough to be retained, and its
    /// measured start is therefore later and higher than its true one. The rate over the captured window is
    /// still sound, and the unaccounted-commit path below is immune to this entirely — which matters, because
    /// a kernel leak is the case this cap could otherwise hide.
    /// </summary>
    private static IReadOnlyList<MemoryGrowthSeries> FitProcesses(IReadOnlyList<MemoryPressureDetail> memories)
    {
        var namesByPid = new Dictionary<int, HashSet<string>>();
        var points = new Dictionary<(string Name, int Pid), List<(DateTimeOffset Timestamp, long Bytes)>>();

        foreach (var memory in memories)
        {
            foreach (var process in memory.TopCommitProcesses)
            {
                if (!namesByPid.TryGetValue(process.ProcessId, out var names))
                {
                    names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    namesByPid[process.ProcessId] = names;
                }

                names.Add(process.Name);

                var key = (process.Name, process.ProcessId);
                if (!points.TryGetValue(key, out var series))
                {
                    series = new List<(DateTimeOffset, long)>();
                    points[key] = series;
                }

                series.Add((memory.Timestamp, process.PrivateBytes));
            }
        }

        var results = new List<MemoryGrowthSeries>();
        foreach (var ((name, pid), series) in points)
        {
            if (namesByPid[pid].Count > 1)
            {
                continue;
            }

            var ordered = series.OrderBy(point => point.Timestamp).ToArray();
            if (ordered.Length < MinimumSamples)
            {
                continue;
            }

            var span = ordered[^1].Timestamp - ordered[0].Timestamp;
            if (span < MinimumSpan)
            {
                continue;
            }

            results.Add(new MemoryGrowthSeries(
                name,
                pid,
                ordered[0].Bytes,
                ordered[^1].Bytes,
                Slope(ordered.Select(point => (point.Timestamp, (long?)point.Bytes)).ToArray()),
                ordered.Length,
                span));
        }

        return results;
    }

    private static MemoryGrowthSeries? Fit(
        string name,
        int? processId,
        IReadOnlyList<MemoryPressureDetail> memories,
        Func<MemoryPressureDetail, long?> selector)
    {
        var points = memories
            .Select(memory => (memory.Timestamp, Value: selector(memory)))
            .Where(point => point.Value.HasValue)
            .ToArray();

        if (points.Length < MinimumSamples)
        {
            return null;
        }

        var span = points[^1].Timestamp - points[0].Timestamp;
        if (span < MinimumSpan)
        {
            return null;
        }

        return new MemoryGrowthSeries(
            name,
            processId,
            points[0].Value!.Value,
            points[^1].Value!.Value,
            Slope(points),
            points.Length,
            span);
    }

    /// <summary>
    /// Least squares rather than first-versus-last.
    ///
    /// First-versus-last is hostage to its two endpoints: one sample taken during a transient spike — a build,
    /// a browser opening a hundred tabs — sets the whole rate, and the intervening hours of evidence are
    /// discarded. A regression uses every point, so a genuine steady climb survives the noise and a single
    /// spike does not become a leak.
    /// </summary>
    private static double Slope(IReadOnlyList<(DateTimeOffset Timestamp, long? Bytes)> points)
    {
        var origin = points[0].Timestamp;
        var n = points.Count;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;

        foreach (var (timestamp, bytes) in points)
        {
            var x = (timestamp - origin).TotalHours;
            var y = bytes!.Value / BytesPerMegabyte;
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        var denominator = (n * sumXx) - (sumX * sumX);
        if (Math.Abs(denominator) < double.Epsilon)
        {
            return 0;
        }

        return ((n * sumXy) - (sumX * sumY)) / denominator;
    }

    private static IReadOnlyList<MemoryLeakCandidate> CollectLeakCandidates(IReadOnlyList<MemoryPressureDetail> memories)
    {
        return memories
            .SelectMany(memory => memory.LeakCandidates)
            .GroupBy(candidate => (candidate.ProcessName, candidate.ProcessId))
            .Select(group => group.OrderByDescending(candidate => candidate.ObservedAt).First())
            .OrderByDescending(candidate => candidate.ObservedAt)
            .ToArray();
    }
}
