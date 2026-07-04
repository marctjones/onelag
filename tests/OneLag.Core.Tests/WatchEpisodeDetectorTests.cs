using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class WatchEpisodeDetectorTests
{
    [Fact]
    public void DetectClassifiesOneDriveCpuEpisode()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var sample = new WatchSample(
            timestamp,
            1_200,
            new TelemetrySnapshot(
                timestamp,
                new[] { new ProcessSample("OneDrive", 42, 100, TimeSpan.FromSeconds(2), null, 25) },
                0,
                null,
                "test"),
            EmptyPressure(timestamp),
            "WINWORD");

        var episodes = WatchEpisodeDetector.Detect(new[] { sample }, Array.Empty<WatchMarker>());

        var episode = Assert.Single(episodes);
        Assert.Equal(EpisodeCategory.OneDrivePossible, episode.Category);
        Assert.Contains("OneDrive CPU", episode.Evidence);
    }

    [Fact]
    public void DetectClassifiesMemoryPressureEpisode()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var sample = new WatchSample(
            timestamp,
            900,
            EmptyTelemetry(timestamp),
            new SystemPressureSnapshot(
                timestamp,
                "processor=10%;queue=0",
                "available=512MB;commit=95%",
                "queue=0;active=0%",
                "source=ac",
                Array.Empty<string>(),
                "test",
                new[]
                {
                    new PerformanceSignal("memory-available-mb", 512, "megabytes", "test"),
                    new PerformanceSignal("memory-commit-percent", 95, "percent", "test")
                },
                Array.Empty<ProcessPressureSample>()),
            null);

        var episodes = WatchEpisodeDetector.Detect(new[] { sample }, Array.Empty<WatchMarker>());

        var episode = Assert.Single(episodes);
        Assert.Equal(EpisodeCategory.MemoryPaging, episode.Category);
    }

    [Fact]
    public void DetectIncludesManualMarkers()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var marker = new WatchMarker(timestamp, "cli", "typing froze");

        var episodes = WatchEpisodeDetector.Detect(Array.Empty<WatchSample>(), new[] { marker });

        var episode = Assert.Single(episodes);
        Assert.Equal(EpisodeCategory.Unknown, episode.Category);
        Assert.Equal("user-observed", episode.Confidence);
        Assert.Contains("typing froze", episode.Evidence);
    }

    private static TelemetrySnapshot EmptyTelemetry(DateTimeOffset timestamp)
    {
        return new TelemetrySnapshot(timestamp, Array.Empty<ProcessSample>(), 0, null, "test");
    }

    private static SystemPressureSnapshot EmptyPressure(DateTimeOffset timestamp)
    {
        return new SystemPressureSnapshot(timestamp, "unknown", "unknown", "unknown", "unknown", Array.Empty<string>(), "test");
    }
}
