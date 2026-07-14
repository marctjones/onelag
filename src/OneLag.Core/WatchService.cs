using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

public sealed record WatchStartOptions(
    TimeSpan Duration,
    TimeSpan Interval,
    string OutputDirectory,
    int MaxSamples = 14_400)
{
    public TimeSpan HostContextInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Memory accounting walks the whole process table, which is far more expensive than the counter reads the
    /// loop does every tick. This tool is diagnosing a machine that is already starved, so a watcher that costs
    /// real CPU corrupts the very measurement it is taking. Thirty seconds is fast enough to see a leak that
    /// takes hours (the thing we are looking for) and slow enough to be invisible against the workload.
    /// </summary>
    public TimeSpan MemoryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The number of processes retained per memory sample. Bounded because an 8-hour recording of a full
    /// process table would be gigabytes of JSONL on a machine that is short of resources; ten is enough to
    /// carry every plausible leaker, and the unaccounted-commit figure — which is what names a kernel leak — is
    /// computed from every process before the list is truncated, so the cap cannot hide it.
    /// </summary>
    public int MaxCommitProcessesPerSample { get; init; } = 10;

    /// <summary>
    /// On by default, and that is the point of the feature: the user cannot tag a freeze while frozen, because
    /// the freeze is precisely what stops him acting. The watcher detects it and captures it unattended.
    /// </summary>
    public bool AutoCapture { get; init; } = true;

    /// <summary>
    /// Off by default. A kernel ETW trace holds a session open for ten seconds and costs more than everything
    /// else the loop does put together; firing that unattended, inside a sampling loop, on a machine that is
    /// already stalling would make the watcher part of the problem. Opt in when you are specifically hunting a
    /// driver and can accept the cost.
    /// </summary>
    public bool AutoCaptureDriverTrace { get; init; }

    public TimeSpan AutoCaptureTraceDuration { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan AutoCaptureCooldown { get; init; } = TimeSpan.FromMinutes(2);

    public int MaxAutoCaptures { get; init; } = 20;
}

public sealed record WatchState(
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string Directory,
    int Samples);

public sealed record WatchRunSummary(
    string Directory,
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    int Samples,
    string State);

public sealed class WatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions LineJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string GetDefaultDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".onelag");
        }

        return Path.Combine(localAppData, "OneLag", "watch");
    }

    public async Task<WatchRunSummary> StartAsync(WatchStartOptions options, IPlatformProbe platform, CancellationToken cancellationToken)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(platform);

        var directory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(directory);
        File.Delete(GetStopRequestPath(directory));

        var statePath = GetStatePath(directory);
        var samplesPath = GetSamplesPath(directory);
        var startedAt = DateTimeOffset.UtcNow;
        var sampleCount = 0;

        // Host context (display topology, Bluetooth radio, dock state) changes on the scale of minutes and
        // is more expensive to enumerate than a counter read, so it is refreshed on its own cadence and
        // attached to each sample rather than re-queried every tick.
        HostContext? hostContext = null;
        var hostContextAge = Stopwatch.StartNew();

        // Memory is the second reason this loop exists. A single snapshot cannot tell a leak from a large but
        // stable process; only a series can. It is sampled on its own slower cadence for the reason given on
        // MemoryInterval, and attached to whichever tick happened to carry it.
        MemoryPressureDetail? memory = null;
        Stopwatch? memoryAge = null;

        var detector = new FreezeDetector(new FreezeDetectorOptions
        {
            DeepCaptureCooldown = options.AutoCaptureCooldown,
            MaxDeepCaptures = options.MaxAutoCaptures
        });
        var freezeCapture = new FreezeCaptureService(platform);

        WriteState(statePath, new WatchState("running", startedAt, DateTimeOffset.UtcNow, directory, sampleCount));

        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt < options.Duration)
        {
            if (File.Exists(GetStopRequestPath(directory)))
            {
                break;
            }

            // Timer drift is the overshoot of this one sleep, not the loop's cumulative lateness. The
            // samplers themselves cost real time (PDH and process sampling each hold a window open), and
            // measuring against a running schedule would fold that cost into drift and accumulate it, so
            // every sample after the first would read as a lag episode with an ever-growing stall.
            var tick = Stopwatch.StartNew();
            await Task.Delay(options.Interval, cancellationToken);
            var drift = tick.Elapsed.TotalMilliseconds - options.Interval.TotalMilliseconds;

            if (hostContext is null || hostContextAge.Elapsed >= options.HostContextInterval)
            {
                hostContext = platform.CaptureHostContext();
                hostContextAge.Restart();
            }

            // Sampled memory is attached only to the tick that took it. Every other sample carries a null, which
            // readers must treat as "not measured here" rather than as an absence of pressure.
            MemoryPressureDetail? sampledMemory = null;
            if (memoryAge is null || memoryAge.Elapsed >= options.MemoryInterval)
            {
                memory = Trim(platform.CaptureMemoryPressure(), options.MaxCommitProcessesPerSample);
                memoryAge = Stopwatch.StartNew();
                sampledMemory = memory;
            }

            var sample = new WatchSample(
                DateTimeOffset.UtcNow,
                drift,
                platform.CaptureTelemetry(),
                platform.CaptureSystemPressure(),
                platform.GetForegroundProcessName(),
                hostContext,
                platform.CaptureShellResponsiveness(),
                sampledMemory);

            AppendJsonLine(samplesPath, sample);
            sampleCount++;

            if (options.AutoCapture)
            {
                // The detector is fed the most recent memory reading even on ticks that did not sample it: a
                // freeze must be judged against what the machine is holding now, not against a null.
                AutoCapture(
                    detector.Evaluate(sample with { Memory = sampledMemory ?? memory }),
                    directory,
                    freezeCapture,
                    options,
                    cancellationToken);
            }

            if (sampleCount > options.MaxSamples)
            {
                TrimJsonLines(samplesPath, options.MaxSamples);
                sampleCount = options.MaxSamples;
            }

            WriteState(statePath, new WatchState("running", startedAt, DateTimeOffset.UtcNow, directory, sampleCount));
        }

        var stoppedAt = DateTimeOffset.UtcNow;
        var state = new WatchState("stopped", startedAt, stoppedAt, directory, sampleCount);
        WriteState(statePath, state);
        return new WatchRunSummary(directory, startedAt, stoppedAt, sampleCount, state.State);
    }

    /// <summary>
    /// Writes the marker and, unless debounced, the deep capture for one auto-detected freeze.
    ///
    /// The marker is written for every detection, even when the capture itself is suppressed, because the
    /// report is rebuilt from this directory long after the run exited: a detection that leaves no trace is a
    /// detection that never happened as far as the user can tell.
    ///
    /// Deep captures are separate files, not samples, so the ring-buffer trim that bounds the time series can
    /// never discard the one artifact the whole session existed to produce. Failures here are swallowed on
    /// purpose — a capture that throws (an unreadable process, a denied handle) must not end an all-day
    /// recording that is still collecting the memory series.
    /// </summary>
    private static void AutoCapture(
        FreezeDetection detection,
        string directory,
        FreezeCaptureService freezeCapture,
        WatchStartOptions options,
        CancellationToken cancellationToken)
    {
        if (!detection.Triggered)
        {
            return;
        }

        try
        {
            AppendJsonLine(
                GetMarkersPath(directory),
                new WatchMarker(DateTimeOffset.UtcNow, FreezeDetector.AutoMarkerSource, $"{detection.Trigger}: {detection.Note}"));

            if (!detection.ShouldCapture)
            {
                return;
            }

            var capture = freezeCapture.Capture(
                new FreezeCaptureOptions(
                    options.AutoCaptureTraceDuration,
                    $"Auto-detected freeze ({detection.Trigger}). {detection.Note}",
                    SkipDriverTrace: !options.AutoCaptureDriverTrace),
                cancellationToken);

            var path = Path.Combine(directory, $"freeze-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(path, FreezeReportWriter.ToJson(capture));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best effort: losing one capture is survivable, losing the session is not.
        }
    }

    /// <summary>
    /// Bounds what each memory sample costs on disk. The commit accounting itself (including the unaccounted
    /// figure that names a kernel leak) is computed by the probe over every process before this runs, so
    /// truncating the retained list cannot change any conclusion about where the memory went.
    /// </summary>
    private static MemoryPressureDetail Trim(MemoryPressureDetail memory, int maxProcesses)
    {
        if (memory.TopCommitProcesses.Count <= maxProcesses)
        {
            return memory;
        }

        return memory with
        {
            TopCommitProcesses = memory.TopCommitProcesses
                .OrderByDescending(process => process.PrivateBytes)
                .Take(maxProcesses)
                .ToArray()
        };
    }

    public void RequestStop(string outputDirectory)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(GetStopRequestPath(directory), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    public WatchState? ReadState(string outputDirectory)
    {
        var path = GetStatePath(Path.GetFullPath(outputDirectory));
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WatchState>(File.ReadAllText(path), JsonOptions);
    }

    public WatchMarker Mark(string outputDirectory, string source, string? note)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var marker = new WatchMarker(DateTimeOffset.UtcNow, source, note);
        AppendJsonLine(GetMarkersPath(directory), marker);
        return marker;
    }

    public string WriteReport(string outputDirectory, string reportPath)
    {
        var directory = Path.GetFullPath(outputDirectory);
        var samples = ReadSamples(directory);
        var markers = ReadMarkers(directory);
        var report = BuildReport(samples, markers);
        var fullReportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath) ?? Environment.CurrentDirectory);
        File.WriteAllText(fullReportPath, report);
        return fullReportPath;
    }

    public IReadOnlyList<WatchSample> ReadSamples(string outputDirectory)
    {
        return ReadJsonLines<WatchSample>(GetSamplesPath(Path.GetFullPath(outputDirectory))).ToArray();
    }

    public IReadOnlyList<WatchMarker> ReadMarkers(string outputDirectory)
    {
        return ReadJsonLines<WatchMarker>(GetMarkersPath(Path.GetFullPath(outputDirectory))).ToArray();
    }

    public string BuildReport(IReadOnlyList<WatchSample> samples, IReadOnlyList<WatchMarker> markers)
    {
        var lines = new List<string>
        {
            "# OneLag Watch Report",
            string.Empty,
            $"- Samples: `{samples.Count:N0}`",
            $"- Markers: `{markers.Count:N0}`"
        };

        if (samples.Count > 0)
        {
            lines.Add($"- First sample: `{samples[0].Timestamp:O}`");
            lines.Add($"- Last sample: `{samples[^1].Timestamp:O}`");
            lines.Add($"- Max timer drift: `{samples.Max(sample => sample.TimerDriftMilliseconds):N1} ms`");
        }

        var episodes = WatchEpisodeDetector.Detect(samples, markers);
        var correlation = WatchContextCorrelation.Correlate(samples, episodes, InferSampleInterval(samples));

        lines.Add(string.Empty);
        lines.Add("## Configuration Correlation");
        if (correlation.Buckets.Count == 0)
        {
            lines.Add("- No host context (displays, dock, Bluetooth) was captured, so lag cannot be correlated with the machine's configuration.");
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("| Configuration | Samples | Observed | Episodes | Episodes/hour | Max drift | Max DPC |");
            lines.Add("| --- | --- | --- | --- | --- | --- | --- |");
            foreach (var bucket in correlation.Buckets)
            {
                var dpc = bucket.MaxDpcPercent.HasValue ? $"{bucket.MaxDpcPercent.Value:N1}%" : "unknown";
                lines.Add($"| {bucket.Context} | {bucket.Samples:N0} | {bucket.Observed:hh\\:mm\\:ss} | {bucket.Episodes:N0} | {bucket.EpisodesPerHour:N1} | {bucket.MaxTimerDriftMilliseconds:N0} ms | {dpc} |");
            }

            if (!string.IsNullOrWhiteSpace(correlation.Conclusion))
            {
                lines.Add(string.Empty);
                lines.Add(correlation.Conclusion);
            }
        }

        AppendMemoryTrend(lines, samples);
        AppendAutoDetectedFreezes(lines, samples, markers);

        lines.Add(string.Empty);
        lines.Add("## Markers");
        foreach (var marker in markers)
        {
            lines.Add($"- `{marker.Timestamp:O}` from `{marker.Source}`{(string.IsNullOrWhiteSpace(marker.Note) ? string.Empty : $": {marker.Note}")}");
        }

        lines.Add(string.Empty);
        lines.Add("## Episodes");
        if (episodes.Count == 0)
        {
            lines.Add("- No lag episodes crossed the timer-drift threshold and no manual markers were recorded.");
        }
        else
        {
            foreach (var episode in episodes)
            {
                lines.Add($"- `{episode.StartedAt:O}` to `{episode.FinishedAt:O}` `{episode.Category}` confidence `{episode.Confidence}`: {episode.Evidence}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("## Largest Timer Delays");
        foreach (var sample in samples.OrderByDescending(sample => sample.TimerDriftMilliseconds).Take(10))
        {
            var signals = sample.SystemPressure.Signals ?? Array.Empty<PerformanceSignal>();
            var dpc = PressureClassifier.Value(signals, "processor-dpc-percent-max-core")
                ?? PressureClassifier.Value(signals, "processor-dpc-percent");
            var shell = sample.ShellResponsiveness?.ShellWindowHung == true ? "shell-hung" : "shell-ok";
            lines.Add($"- `{sample.Timestamp:O}` drift `{sample.TimerDriftMilliseconds:N1} ms`, foreground `{sample.ForegroundProcess ?? "unknown"}`, DPC `{(dpc.HasValue ? $"{dpc.Value:N1}%" : "unknown")}`, `{shell}`, config `{WatchContextCorrelation.DescribeContext(sample)}`");
        }

        var hungSamples = samples.Count(sample => sample.ShellResponsiveness?.ShellWindowHung == true);
        if (hungSamples > 0)
        {
            lines.Add(string.Empty);
            lines.Add("## Explorer Shell");
            lines.Add($"- The Explorer shell was hung in `{hungSamples:N0}` of `{samples.Count:N0}` samples. This is the shell-blocking failure mode measured directly.");
        }

        lines.Add(string.Empty);
        lines.Add("## Interpretation");
        lines.Add("Timer drift is a user-mode responsiveness canary: it proves the machine stalled but not what stalled it. DPC and interrupt time narrow the stall to kernel driver work, and the configuration correlation above narrows it to a hardware configuration. WPR/WPA with the DPC/ISR profile is still required to name the specific driver file.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// The section that needs no action from the user at all.
    ///
    /// A freeze has to be caught in the act, but a leak announces itself over hours whether anyone is watching
    /// or not — and it is the harder thing to see, because the process holding the most memory is almost never
    /// the one taking it. This section reports growth rates, and where the growth belongs to no process at all
    /// it says so in the plainest terms available, because that case (a driver holding kernel pool) is
    /// invisible in every tool the user already has.
    /// </summary>
    private static void AppendMemoryTrend(List<string> lines, IReadOnlyList<WatchSample> samples)
    {
        const double MB = 1024 * 1024;
        const double GB = 1024 * MB;

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        lines.Add(string.Empty);
        lines.Add("## Memory Trend");
        lines.Add(string.Empty);

        if (!trend.HasSufficientData)
        {
            lines.Add(trend.Summary);
            return;
        }

        lines.Add($"Measured over `{trend.MemorySamples:N0}` memory samples spanning `{trend.Span:hh\\:mm\\:ss}`. Rates are least-squares fits over every sample, not first-versus-last, so a single spike cannot become a trend.");
        lines.Add(string.Empty);

        if (trend.Commit is { } commit)
        {
            lines.Add($"- Committed memory: `{commit.StartBytes / GB:N1} GB` to `{commit.EndBytes / GB:N1} GB` (`{commit.DeltaBytes / MB:N0} MB`), growing at `{commit.MegabytesPerHour:N0} MB/hour`");
        }

        if (trend.UnaccountedCommit is { } unaccounted)
        {
            lines.Add($"- Commit belonging to no user-mode process: `{unaccounted.StartBytes / GB:N1} GB` to `{unaccounted.EndBytes / GB:N1} GB`, growing at `{unaccounted.MegabytesPerHour:N0} MB/hour`");
        }

        lines.Add(string.Empty);

        if (trend.KernelLeakSuspected)
        {
            lines.Add("### This is a kernel or driver leak, not an application leak");
            lines.Add(string.Empty);
            lines.Add(trend.Summary);
            lines.Add(string.Empty);
            lines.Add("Closing applications will not return this memory, and rebooting is the only thing that will. Task Manager's Details tab lists user-mode processes only, so memory a driver holds in kernel pool appears nowhere in it while still consuming commit — which is why this can only be seen by measuring what every process accounts for and subtracting it from what the machine has committed.");
            lines.Add(string.Empty);
            lines.Add("Next step: run `poolmon` or Sysinternals RAMMap (Use Counts tab) to name the pool tag, then map the tag to the driver holding it. On a machine carrying third-party kernel drivers, that is where to look first.");
            lines.Add(string.Empty);
        }

        lines.Add("### Fastest-growing processes");
        lines.Add(string.Empty);
        lines.Add("Ranked by growth rate, not by size. A large process that stays flat is not a leak; a small one that climbs is.");
        lines.Add(string.Empty);

        if (trend.GrowingProcesses.Count == 0)
        {
            lines.Add($"- No process grew faster than `{MemoryTrendAnalyzer.ProcessGrowthNoiseFloorMegabytesPerHour:N0} MB/hour`, the floor below which ordinary heap and cache behaviour is indistinguishable from a leak.");
        }
        else
        {
            lines.Add("| Process | PID | Growth | Start | End | Samples |");
            lines.Add("| --- | --- | --- | --- | --- | --- |");
            foreach (var process in trend.GrowingProcesses.Take(10))
            {
                lines.Add($"| `{process.Name}` | {process.ProcessId} | {process.MegabytesPerHour:N0} MB/hour | {process.StartBytes / MB:N0} MB | {process.EndBytes / MB:N0} MB | {process.Samples:N0} |");
            }
        }

        if (trend.WindowsLeakCandidates.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("### Leak candidates named by Windows");
            lines.Add(string.Empty);
            lines.Add("Windows' own leak detectors, not a OneLag heuristic.");
            foreach (var candidate in trend.WindowsLeakCandidates)
            {
                lines.Add($"- `{candidate.ProcessName}` flagged by `{candidate.Source}` at `{candidate.ObservedAt:O}`");
            }
        }
    }

    /// <summary>
    /// Correlates each freeze the watcher detected on its own against what the machine's memory was doing at
    /// that moment. Markers land on the tick that tripped the detector, which usually carries no memory reading
    /// of its own, so the nearest memory-bearing sample is used.
    /// </summary>
    private static void AppendAutoDetectedFreezes(List<string> lines, IReadOnlyList<WatchSample> samples, IReadOnlyList<WatchMarker> markers)
    {
        const double MB = 1024 * 1024;

        var auto = markers.Where(marker => marker.Source == FreezeDetector.AutoMarkerSource).ToArray();

        lines.Add(string.Empty);
        lines.Add("## Auto-Detected Freezes");
        lines.Add(string.Empty);

        if (auto.Length == 0)
        {
            lines.Add("- The watcher detected no freeze on its own during this session.");
            return;
        }

        var memorySamples = samples.Where(sample => sample.Memory is not null).ToArray();
        lines.Add($"`{auto.Length:N0}` freeze(s) were detected and recorded without the user having to act.");
        lines.Add(string.Empty);

        foreach (var marker in auto)
        {
            var nearest = memorySamples
                .OrderBy(sample => Math.Abs((sample.Timestamp - marker.Timestamp).Ticks))
                .FirstOrDefault();

            var memory = nearest?.Memory;
            var state = memory?.CommitTotalBytes is null
                ? "memory not sampled"
                : $"committed {memory.CommitTotalBytes.Value / MB:N0} MB, headroom {(memory.CommitHeadroomBytes.HasValue ? $"{memory.CommitHeadroomBytes.Value / MB:N0} MB" : "unknown")}, unaccounted {(memory.UnaccountedCommitBytes.HasValue ? $"{memory.UnaccountedCommitBytes.Value / MB:N0} MB" : "unknown")}";

            lines.Add($"- `{marker.Timestamp:O}`: {marker.Note} (memory at that time: {state})");
        }

        // A cap that hides what it dropped reads as "this only happened twenty times", which would be false.
        var dropped = auto.Count(marker => marker.Note?.Contains(FreezeDetector.CapSkipToken, StringComparison.Ordinal) == true);
        if (dropped > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"**`{dropped:N0}` detected freeze(s) did not get a deep capture: the per-run cap on deep captures was reached.** The freezes above still happened and are all recorded; only the expensive capture was skipped. The machine froze more often than the number of `freeze-*.json` files in this directory.");
        }
    }

    /// <summary>
    /// Recovers the recording cadence from the samples themselves so a report can be rebuilt from a
    /// directory without knowing what interval the session was started with.
    /// </summary>
    private static TimeSpan InferSampleInterval(IReadOnlyList<WatchSample> samples)
    {
        if (samples.Count < 2)
        {
            return TimeSpan.FromSeconds(2);
        }

        var deltas = samples
            .OrderBy(sample => sample.Timestamp)
            .Zip(samples.OrderBy(sample => sample.Timestamp).Skip(1), (first, second) => (second.Timestamp - first.Timestamp).TotalSeconds)
            .Where(delta => delta > 0)
            .OrderBy(delta => delta)
            .ToArray();

        if (deltas.Length == 0)
        {
            return TimeSpan.FromSeconds(2);
        }

        return TimeSpan.FromSeconds(deltas[deltas.Length / 2]);
    }

    private static void Validate(WatchStartOptions options)
    {
        if (options.Duration <= TimeSpan.Zero || options.Duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentException("Watch duration must be greater than zero and no more than 24h.", nameof(options));
        }

        if (options.Interval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("Watch interval must be at least 1s.", nameof(options));
        }

        if (options.MaxSamples <= 0)
        {
            throw new ArgumentException("Watch max samples must be greater than zero.", nameof(options));
        }

        if (options.MemoryInterval <= TimeSpan.Zero)
        {
            throw new ArgumentException("Watch memory interval must be greater than zero.", nameof(options));
        }

        if (options.MaxCommitProcessesPerSample <= 0)
        {
            throw new ArgumentException("Watch max commit processes per sample must be greater than zero.", nameof(options));
        }

        if (options.AutoCaptureCooldown < TimeSpan.Zero)
        {
            throw new ArgumentException("Watch auto-capture cooldown must not be negative.", nameof(options));
        }

        if (options.MaxAutoCaptures <= 0)
        {
            throw new ArgumentException("Watch max auto-captures must be greater than zero.", nameof(options));
        }
    }

    private static string GetStatePath(string directory) => Path.Combine(directory, "state.json");

    private static string GetSamplesPath(string directory) => Path.Combine(directory, "samples.ndjson");

    private static string GetMarkersPath(string directory) => Path.Combine(directory, "markers.ndjson");

    private static string GetStopRequestPath(string directory) => Path.Combine(directory, "stop.request");

    private static void AppendJsonLine<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        File.AppendAllText(path, JsonSerializer.Serialize(value, LineJsonOptions) + Environment.NewLine);
    }

    private static IEnumerable<T> ReadJsonLines<T>(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = JsonSerializer.Deserialize<T>(line, LineJsonOptions);
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static void TrimJsonLines(string path, int maxLines)
    {
        var lines = File.ReadLines(path).TakeLast(maxLines).ToArray();
        File.WriteAllLines(path, lines);
    }

    private static void WriteState(string path, WatchState state)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
    }
}
