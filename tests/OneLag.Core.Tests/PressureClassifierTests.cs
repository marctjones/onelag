using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class PressureClassifierTests
{
    [Fact]
    public void ClassifyDetectsInterruptPressureFromASinglePinnedCore()
    {
        // The averaged-across-cores value looks harmless. The per-core maximum is the one that corresponds
        // to a stalled desktop, which is precisely why the _Total instance alone is not enough.
        var pressure = Pressure(
            new PerformanceSignal("processor-total-percent", 15, "percent", "pdh"),
            new PerformanceSignal("processor-dpc-percent", 1.2, "percent", "pdh"),
            new PerformanceSignal("processor-dpc-percent-max-core", 28, "percent", "pdh-per-instance-max"));

        var result = PressureClassifier.Classify(pressure);

        Assert.True(result.HasInterruptPressure);
        Assert.False(result.HasCpuPressure);
        Assert.False(result.HasDiskPressure);
        Assert.Contains(result.Evidence, evidence => evidence.Contains("maxCore=28.0", StringComparison.Ordinal));
    }

    [Fact]
    public void ClassifyDoesNotReportInterruptPressureWhenCountersAreClean()
    {
        var pressure = Pressure(
            new PerformanceSignal("processor-dpc-percent", 0.4, "percent", "pdh"),
            new PerformanceSignal("processor-dpc-percent-max-core", 1.9, "percent", "pdh-per-instance-max"),
            new PerformanceSignal("processor-interrupt-percent", 1.1, "percent", "pdh"));

        Assert.False(PressureClassifier.Classify(pressure).HasInterruptPressure);
    }

    [Fact]
    public void ClassifyDoesNotInventInterruptPressureFromMissingCounters()
    {
        var pressure = Pressure(new PerformanceSignal("processor-dpc-percent", null, "percent", "pdh-counter-unavailable"));

        Assert.False(PressureClassifier.Classify(pressure).HasInterruptPressure);
    }

    [Fact]
    public void EpisodeDetectorBlamesTheDockWhenInterruptPressureCoincidesWithAnExternalDisplay()
    {
        var sample = new WatchSample(
            DateTimeOffset.UtcNow,
            1_200,
            new TelemetrySnapshot(DateTimeOffset.UtcNow, Array.Empty<ProcessSample>(), 0, null, "test"),
            Pressure(new PerformanceSignal("processor-dpc-percent-max-core", 30, "percent", "pdh-per-instance-max")),
            "explorer",
            new HostContext(
                DateTimeOffset.UtcNow,
                2,
                0,
                1,
                Array.Empty<DisplayInfo>(),
                true,
                true,
                1,
                "source=ac;battery=100%",
                true,
                new[] { "DisplayLinkManager" },
                DockStates.DockedLikely,
                "test"));

        var episodes = WatchEpisodeDetector.Detect(new[] { sample }, Array.Empty<WatchMarker>());

        Assert.Equal(EpisodeCategory.DisplayOrDockSuspected, Assert.Single(episodes).Category);
    }

    [Fact]
    public void EpisodeDetectorReportsShellBlockingWhenExplorerIsHung()
    {
        var sample = new WatchSample(
            DateTimeOffset.UtcNow,
            1_200,
            new TelemetrySnapshot(DateTimeOffset.UtcNow, Array.Empty<ProcessSample>(), 0, null, "test"),
            Pressure(),
            "explorer",
            null,
            new ShellResponsiveness(DateTimeOffset.UtcNow, true, true, 3, 2_000, "test"));

        var episodes = WatchEpisodeDetector.Detect(new[] { sample }, Array.Empty<WatchMarker>());

        Assert.Equal(EpisodeCategory.ShellBlocked, Assert.Single(episodes).Category);
    }

    private static SystemPressureSnapshot Pressure(params PerformanceSignal[] signals)
    {
        return new SystemPressureSnapshot(
            DateTimeOffset.UtcNow,
            "normal",
            "normal",
            "normal",
            "ac",
            Array.Empty<string>(),
            "test",
            signals);
    }
}
