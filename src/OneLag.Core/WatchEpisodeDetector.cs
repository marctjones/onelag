namespace OneLag.Core;

public sealed record WatchEpisodeDetectionOptions(
    double TimerDriftThresholdMilliseconds = 500,
    TimeSpan? MergeWindow = null);

public static class WatchEpisodeDetector
{
    public static IReadOnlyList<WatchEpisode> Detect(
        IReadOnlyList<WatchSample> samples,
        IReadOnlyList<WatchMarker> markers,
        WatchEpisodeDetectionOptions? options = null)
    {
        options ??= new WatchEpisodeDetectionOptions();
        var mergeWindow = options.MergeWindow ?? TimeSpan.FromSeconds(30);
        var candidates = new List<WatchEpisode>();

        foreach (var sample in samples.OrderBy(sample => sample.Timestamp))
        {
            if (sample.TimerDriftMilliseconds >= options.TimerDriftThresholdMilliseconds)
            {
                candidates.Add(CreateSampleEpisode(sample));
            }
        }

        foreach (var marker in markers.OrderBy(marker => marker.Timestamp))
        {
            var nearest = FindNearestSample(samples, marker.Timestamp, mergeWindow);
            candidates.Add(new WatchEpisode(
                marker.Timestamp,
                marker.Timestamp,
                nearest is null ? EpisodeCategory.Unknown : Categorize(nearest),
                string.IsNullOrWhiteSpace(marker.Note)
                    ? "Manual lag marker was recorded."
                    : $"Manual lag marker was recorded: {marker.Note}",
                "user-observed"));
        }

        return Merge(candidates.OrderBy(candidate => candidate.StartedAt).ToArray(), mergeWindow);
    }

    private static WatchEpisode CreateSampleEpisode(WatchSample sample)
    {
        var category = Categorize(sample);
        var evidence = $"Timer drift {sample.TimerDriftMilliseconds:N1} ms";
        if (!string.IsNullOrWhiteSpace(sample.ForegroundProcess))
        {
            evidence += $", foreground {sample.ForegroundProcess}";
        }

        if (sample.Telemetry.OneDriveProcesses.Count > 0)
        {
            var onedriveCpu = sample.Telemetry.OneDriveProcesses
                .Where(process => process.CpuPercent.HasValue)
                .Sum(process => process.CpuPercent.GetValueOrDefault());
            evidence += onedriveCpu > 0
                ? $", OneDrive CPU {onedriveCpu:N1}%"
                : ", OneDrive process present";
        }

        var pressure = PressureClassifier.Classify(sample.SystemPressure);
        if (pressure.Evidence.Count > 0)
        {
            evidence += $", pressure {string.Join("; ", pressure.Evidence)}";
        }

        return new WatchEpisode(sample.Timestamp, sample.Timestamp, category, evidence, "medium");
    }

    private static EpisodeCategory Categorize(WatchSample sample)
    {
        var onedriveCpu = sample.Telemetry.OneDriveProcesses
            .Where(process => process.CpuPercent.HasValue)
            .Sum(process => process.CpuPercent.GetValueOrDefault());
        if (sample.Telemetry.OneDriveLogFilesChangedLastMinute >= 5 || onedriveCpu >= 15)
        {
            return EpisodeCategory.OneDrivePossible;
        }

        var pressure = PressureClassifier.Classify(sample.SystemPressure);
        if (pressure.HasDiskPressure)
        {
            return EpisodeCategory.StoragePressure;
        }

        if (pressure.HasMemoryPressure)
        {
            return EpisodeCategory.MemoryPaging;
        }

        if (pressure.HasCpuPressure)
        {
            return EpisodeCategory.CpuStarvation;
        }

        if (!string.IsNullOrWhiteSpace(sample.ForegroundProcess))
        {
            return EpisodeCategory.ForegroundAppBlocked;
        }

        return EpisodeCategory.Unknown;
    }

    private static WatchSample? FindNearestSample(IReadOnlyList<WatchSample> samples, DateTimeOffset timestamp, TimeSpan window)
    {
        return samples
            .Select(sample => new { Sample = sample, Delta = Duration(sample.Timestamp - timestamp) })
            .Where(candidate => candidate.Delta <= window)
            .OrderBy(candidate => candidate.Delta)
            .Select(candidate => candidate.Sample)
            .FirstOrDefault();
    }

    private static IReadOnlyList<WatchEpisode> Merge(IReadOnlyList<WatchEpisode> candidates, TimeSpan mergeWindow)
    {
        var merged = new List<WatchEpisode>();
        foreach (var candidate in candidates)
        {
            if (merged.Count == 0)
            {
                merged.Add(candidate);
                continue;
            }

            var previous = merged[^1];
            if (candidate.StartedAt - previous.FinishedAt <= mergeWindow)
            {
                merged[^1] = new WatchEpisode(
                    previous.StartedAt,
                    Max(previous.FinishedAt, candidate.FinishedAt),
                    MergeCategory(previous.Category, candidate.Category),
                    $"{previous.Evidence}; {candidate.Evidence}",
                    MergeConfidence(previous.Confidence, candidate.Confidence));
            }
            else
            {
                merged.Add(candidate);
            }
        }

        return merged;
    }

    private static EpisodeCategory MergeCategory(EpisodeCategory first, EpisodeCategory second)
    {
        if (first == second)
        {
            return first;
        }

        if (first == EpisodeCategory.OneDrivePossible || second == EpisodeCategory.OneDrivePossible)
        {
            return EpisodeCategory.OneDrivePossible;
        }

        if (first == EpisodeCategory.StoragePressure || second == EpisodeCategory.StoragePressure)
        {
            return EpisodeCategory.StoragePressure;
        }

        if (first == EpisodeCategory.MemoryPaging || second == EpisodeCategory.MemoryPaging)
        {
            return EpisodeCategory.MemoryPaging;
        }

        if (first == EpisodeCategory.CpuStarvation || second == EpisodeCategory.CpuStarvation)
        {
            return EpisodeCategory.CpuStarvation;
        }

        if (first == EpisodeCategory.ForegroundAppBlocked || second == EpisodeCategory.ForegroundAppBlocked)
        {
            return EpisodeCategory.ForegroundAppBlocked;
        }

        return EpisodeCategory.Unknown;
    }

    private static string MergeConfidence(string first, string second)
    {
        if (first == second)
        {
            return first;
        }

        return first == "user-observed" || second == "user-observed" ? "user-observed+sampled" : "medium";
    }

    private static TimeSpan Duration(TimeSpan value)
    {
        return value < TimeSpan.Zero ? -value : value;
    }

    private static DateTimeOffset Max(DateTimeOffset first, DateTimeOffset second)
    {
        return first >= second ? first : second;
    }
}
