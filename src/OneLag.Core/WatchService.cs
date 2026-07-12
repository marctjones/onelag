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

            var sample = new WatchSample(
                DateTimeOffset.UtcNow,
                drift,
                platform.CaptureTelemetry(),
                platform.CaptureSystemPressure(),
                platform.GetForegroundProcessName(),
                hostContext,
                platform.CaptureShellResponsiveness());

            AppendJsonLine(samplesPath, sample);
            sampleCount++;

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
