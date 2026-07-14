using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class WatchServiceTests : IDisposable
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

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

    [Fact]
    public async Task MemoryIsSampledOnItsOwnCadenceRatherThanEveryTick()
    {
        // The watcher is measuring a machine that is already starved. Walking the process table every second
        // would make the tool part of the problem, so memory rides a slower cadence and most samples carry none.
        var service = new WatchService();
        var platform = new FakePlatformProbe { Memory = Snapshots.HeavyUnaccountedCommit() };
        var options = new WatchStartOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), tempRoot, MaxSamples: 100)
        {
            MemoryInterval = TimeSpan.FromSeconds(3),
            AutoCapture = false
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        await RunUntilCancelled(service, options, platform, cancellation.Token);

        var samples = service.ReadSamples(tempRoot);
        Assert.True(samples.Count >= 4, $"expected several samples, got {samples.Count}");

        var withMemory = samples.Count(sample => sample.Memory is not null);
        Assert.InRange(withMemory, 1, samples.Count - 1);
        Assert.All(
            samples.Where(sample => sample.Memory is not null),
            sample => Assert.Equal(Snapshots.HeavyUnaccountedCommit().CommitTotalBytes, sample.Memory!.CommitTotalBytes));
    }

    [Fact]
    public async Task MemorySampleRetainsOnlyTheTopProcessesByCommit()
    {
        // An 8-hour recording of a full process table would be gigabytes of JSONL on a machine that is short of
        // everything. The unaccounted-commit figure is computed by the probe before this cap, so nothing that
        // names a kernel leak is lost by truncating the list.
        var service = new WatchService();
        var platform = new FakePlatformProbe
        {
            Memory = Snapshots.HeavyUnaccountedCommit() with
            {
                TopCommitProcesses = Enumerable.Range(0, 50)
                    .Select(index => new ProcessCommitSample($"process-{index}", 1000 + index, index * 1_000_000L, index * 1_000_000L))
                    .ToArray()
            }
        };

        var options = new WatchStartOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), tempRoot, MaxSamples: 100)
        {
            MemoryInterval = TimeSpan.FromSeconds(1),
            MaxCommitProcessesPerSample = 10,
            AutoCapture = false
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await RunUntilCancelled(service, options, platform, cancellation.Token);

        var memory = Assert.IsType<MemoryPressureDetail>(service.ReadSamples(tempRoot).First(sample => sample.Memory is not null).Memory);
        Assert.Equal(10, memory.TopCommitProcesses.Count);
        Assert.Equal("process-49", memory.TopCommitProcesses[0].Name);
        Assert.Equal(6L * 1024 * 1024 * 1024, memory.SumOfProcessPrivateBytes);
    }

    [Fact]
    public async Task AutoCaptureRecordsAFreezeWithoutTheUserActing()
    {
        // The point of the feature. The machine's Explorer shell is hung, the user cannot click anything, and
        // the watcher marks and captures the episode on its own.
        var service = new WatchService();
        var platform = new FakePlatformProbe
        {
            Shell = Snapshots.HungShell(),
            Memory = Snapshots.HeavyUnaccountedCommit(),
            FilterStack = Snapshots.CrowdedFilterStack()
        };

        var options = new WatchStartOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), tempRoot, MaxSamples: 100)
        {
            MemoryInterval = TimeSpan.FromSeconds(1)
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await RunUntilCancelled(service, options, platform, cancellation.Token);

        var marker = Assert.Single(service.ReadMarkers(tempRoot));
        Assert.Equal(FreezeDetector.AutoMarkerSource, marker.Source);
        Assert.Contains("shell-hung", marker.Note);

        var captures = Directory.GetFiles(tempRoot, "freeze-*.json");
        Assert.Single(captures);
        Assert.Contains("Auto-detected freeze", File.ReadAllText(captures[0]));

        // The default must not fire a kernel trace: a 10s ETW session inside a sampling loop, unattended, on a
        // machine that is already stalling would make the watcher part of the failure.
        Assert.Equal(0, platform.DriverTraceCalls);

        var report = service.BuildReport(service.ReadSamples(tempRoot), service.ReadMarkers(tempRoot));
        Assert.Contains("## Auto-Detected Freezes", report);
        Assert.Contains("without the user having to act", report);
    }

    [Fact]
    public async Task NoAutoCaptureDisablesDetectionEntirely()
    {
        var service = new WatchService();
        var platform = new FakePlatformProbe { Shell = Snapshots.HungShell() };
        var options = new WatchStartOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), tempRoot, MaxSamples: 100)
        {
            AutoCapture = false
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await RunUntilCancelled(service, options, platform, cancellation.Token);

        Assert.Empty(service.ReadMarkers(tempRoot));
        Assert.Empty(Directory.GetFiles(tempRoot, "freeze-*.json"));
    }

    [Fact]
    public void ReportSaysLoudlyWhenTheLeakIsInTheKernel()
    {
        // The report has to say this in the plainest words available. A user reading "unaccounted commit is
        // growing" learns nothing; a user reading "closing applications cannot return this memory" knows to
        // stop hunting through Task Manager, which is the only place he can look and the one place it is
        // guaranteed not to be.
        var samples = TrendingSamples(index => (
            CommitBytes: (8 * GB) + (long)(index * 0.5 * GB),
            SumOfPrivateBytes: 5 * GB,
            Processes: new[] { new ProcessCommitSample("chrome", 1010, 4 * GB, 4 * GB) }));

        var report = new WatchService().BuildReport(samples, Array.Empty<WatchMarker>());

        Assert.Contains("kernel or driver leak", report);
        Assert.Contains("Closing applications will not return this memory", report);
        Assert.Contains("poolmon", report);
        Assert.Contains("1,024 MB/hour", report);
    }

    [Fact]
    public void ReportRanksTheGrowingProcessAndDoesNotBlameTheKernel()
    {
        var samples = TrendingSamples(index => (
            CommitBytes: (8 * GB) + (long)(index * 0.5 * 200 * MB),
            SumOfPrivateBytes: (5 * GB) + (long)(index * 0.5 * 200 * MB),
            Processes: new[]
            {
                new ProcessCommitSample("chrome", 1010, 4 * GB, 4 * GB),
                new ProcessCommitSample("leaky", 2020, (500 * MB) + (long)(index * 0.5 * 200 * MB), 0)
            }));

        var report = new WatchService().BuildReport(samples, Array.Empty<WatchMarker>());

        Assert.Contains("Fastest-growing processes", report);
        Assert.Contains("| `leaky` | 2020 | 200 MB/hour |", report);
        Assert.DoesNotContain("kernel or driver leak", report);
    }

    [Fact]
    public void ReportSaysWhenDeepCapturesWereDroppedByTheCap()
    {
        // A cap that hides what it dropped reads as "this only happened N times", which would be false.
        var markers = new[]
        {
            new WatchMarker(Now, FreezeDetector.AutoMarkerSource, $"timer-drift-severe: stalled. {FreezeDetector.CapSkipToken}"),
            new WatchMarker(Now.AddMinutes(1), FreezeDetector.AutoMarkerSource, "timer-drift-severe: stalled.")
        };

        var report = new WatchService().BuildReport(Array.Empty<WatchSample>(), markers);

        Assert.Contains("did not get a deep capture", report);
        Assert.Contains("The machine froze more often than the number of `freeze-*.json` files", report);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    /// <summary>
    /// A memory series long enough to fit a trend against: the analyzer refuses to extrapolate an hourly rate
    /// from a few minutes of data, so a report test has to synthesize hours.
    /// </summary>
    private static IReadOnlyList<WatchSample> TrendingSamples(
        Func<int, (long CommitBytes, long SumOfPrivateBytes, IReadOnlyList<ProcessCommitSample> Processes)> memory)
    {
        return Enumerable.Range(0, 9)
            .Select(index =>
            {
                var timestamp = Now.AddMinutes(index * 30);
                var (commit, sumOfPrivate, processes) = memory(index);
                return new WatchSample(
                    timestamp,
                    5,
                    Snapshots.QuietTelemetry(),
                    Snapshots.QuietPressure(),
                    "explorer",
                    Snapshots.UndockedHost(),
                    Snapshots.ResponsiveShell(),
                    new MemoryPressureDetail(
                        timestamp,
                        CommitTotalBytes: commit,
                        CommitLimitBytes: 64 * GB,
                        PhysicalTotalBytes: 32 * GB,
                        PhysicalAvailableBytes: 8 * GB,
                        SystemUptime: TimeSpan.FromDays(9),
                        TopCommitProcesses: processes,
                        LeakCandidates: Array.Empty<MemoryLeakCandidate>(),
                        EvidenceState: "windows-toolhelp-process-and-performance-counters",
                        SumOfProcessPrivateBytes: sumOfPrivate,
                        ProcessesSampled: 240,
                        ProcessesInaccessible: 0));
            })
            .ToArray();
    }

    private static async Task RunUntilCancelled(
        WatchService service,
        WatchStartOptions options,
        IPlatformProbe platform,
        CancellationToken cancellationToken)
    {
        try
        {
            await service.StartAsync(options, platform, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Expected: the session is cut short once enough samples exist.
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
