using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class WatchContextCorrelationTests
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-07-10T09:00:00Z");

    [Fact]
    public void CorrelateAttributesLagToTheConfigurationItActuallyTracks()
    {
        // The real-world shape this models: lag every few minutes while docked with an external display,
        // and none at all on the internal panel. The item count and sync load are identical in both.
        var samples = new List<WatchSample>();
        samples.AddRange(Samples(count: 30, start: Origin, docked: true, driftMilliseconds: 900));
        samples.AddRange(Samples(count: 30, start: Origin.AddMinutes(10), docked: false, driftMilliseconds: 5));

        var episodes = WatchEpisodeDetector.Detect(samples, Array.Empty<WatchMarker>());
        var result = WatchContextCorrelation.Correlate(samples, episodes, TimeSpan.FromSeconds(2));

        var docked = result.Buckets.Single(bucket => bucket.Context.Contains("external-display", StringComparison.Ordinal));
        var undocked = result.Buckets.Single(bucket => bucket.Context.Contains("internal-display-only", StringComparison.Ordinal));

        Assert.True(docked.Episodes > 0);
        Assert.Equal(0, undocked.Episodes);
        Assert.NotNull(result.Conclusion);
        Assert.Contains("Every lag episode happened in", result.Conclusion, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrelateRefusesToConcludeFromASingleConfiguration()
    {
        var samples = Samples(count: 20, start: Origin, docked: true, driftMilliseconds: 900).ToArray();
        var episodes = WatchEpisodeDetector.Detect(samples, Array.Empty<WatchMarker>());

        var result = WatchContextCorrelation.Correlate(samples, episodes, TimeSpan.FromSeconds(2));

        Assert.Single(result.Buckets);
        Assert.Contains("only observed in one configuration", result.Conclusion!, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrelateReturnsNothingWhenNoHostContextWasCaptured()
    {
        var samples = new[]
        {
            new WatchSample(Origin, 1, EmptyTelemetry(), EmptyPressure(), "explorer")
        };

        var result = WatchContextCorrelation.Correlate(samples, Array.Empty<WatchEpisode>(), TimeSpan.FromSeconds(2));

        Assert.Empty(result.Buckets);
        Assert.Null(result.Conclusion);
    }

    private static IEnumerable<WatchSample> Samples(int count, DateTimeOffset start, bool docked, double driftMilliseconds)
    {
        for (var index = 0; index < count; index++)
        {
            yield return new WatchSample(
                start.AddSeconds(index * 2),
                driftMilliseconds,
                EmptyTelemetry(),
                EmptyPressure(),
                "explorer",
                Host(docked));
        }
    }

    private static HostContext Host(bool docked)
    {
        return new HostContext(
            Origin,
            docked ? 2 : 1,
            docked ? 1 : 0,
            0,
            Array.Empty<DisplayInfo>(),
            false,
            false,
            0,
            docked ? "source=ac;battery=100%" : "source=battery;battery=70%",
            docked,
            Array.Empty<string>(),
            docked ? DockStates.DockedLikely : DockStates.UndockedLikely,
            "test");
    }

    private static TelemetrySnapshot EmptyTelemetry()
    {
        return new TelemetrySnapshot(Origin, Array.Empty<ProcessSample>(), 0, null, "test");
    }

    private static SystemPressureSnapshot EmptyPressure()
    {
        return new SystemPressureSnapshot(Origin, "normal", "normal", "normal", "ac", Array.Empty<string>(), "test");
    }
}
