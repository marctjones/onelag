using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class WatchServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-watch-service-tests", Guid.NewGuid().ToString("N"));

    public WatchServiceTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void MarkAndReportUseSharedStorage()
    {
        var service = new WatchService();
        var marker = service.Mark(tempRoot, "test", "typing froze");
        var reportPath = Path.Combine(tempRoot, "watch-report.md");

        var fullReportPath = service.WriteReport(tempRoot, reportPath);

        Assert.True(File.Exists(fullReportPath));
        Assert.Equal(marker.Timestamp, Assert.Single(service.ReadMarkers(tempRoot)).Timestamp);
        var report = File.ReadAllText(fullReportPath);
        Assert.Contains("OneLag Watch Report", report);
        Assert.Contains("typing froze", report);
        Assert.Contains("## Episodes", report);
    }

    [Fact]
    public async Task StartDoesNotChargeSamplerCostToTimerDrift()
    {
        // Regression: drift used to be measured against a running schedule, so the samplers' own cost was
        // folded into it and accumulated. With a sampler slower than the drift threshold, every sample after
        // the first read as a lag episode with an ever-growing stall. Drift must measure only the overshoot
        // of the sleep itself.
        var service = new WatchService();
        var platform = new SlowPlatformProbe(TimeSpan.FromMilliseconds(700));
        var options = new WatchStartOptions(
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1),
            tempRoot,
            MaxSamples: 100);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            await service.StartAsync(options, platform, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected: the session is cut short once enough samples exist.
        }

        var samples = service.ReadSamples(tempRoot);
        Assert.True(samples.Count >= 3, $"expected several samples, got {samples.Count}");

        var worstDrift = samples.Max(sample => sample.TimerDriftMilliseconds);
        Assert.True(
            worstDrift < 250,
            $"a {platform.Cost.TotalMilliseconds:N0} ms sampler must not manufacture drift; worst was {worstDrift:N0} ms");

        var episodes = WatchEpisodeDetector.Detect(samples, Array.Empty<WatchMarker>());
        Assert.Empty(episodes);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class SlowPlatformProbe : PortablePlatformProbe
    {
        public SlowPlatformProbe(TimeSpan cost)
        {
            Cost = cost;
        }

        public TimeSpan Cost { get; }

        public override TelemetrySnapshot CaptureTelemetry()
        {
            Thread.Sleep(Cost);
            return base.CaptureTelemetry();
        }
    }
}
