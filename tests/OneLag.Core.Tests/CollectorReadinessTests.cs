using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// The tests that stand between the user and a wasted day.
///
/// A watch session recorded with degraded collectors produces an authoritative-looking report containing
/// nothing. These assert the two behaviours that prevent that: the self test has to say WHICH collector broke,
/// WHY, and WHAT IT COSTS; and the watch pre-flight has to refuse to start rather than record it anyway.
/// </summary>
public sealed class CollectorReadinessTests
{
    /// <summary>
    /// A fully instrumented machine: every new collector returns real data.
    /// </summary>
    private static FakePlatformProbe HealthyProbe() => new()
    {
        Memory = Snapshots.HeavyUnaccountedCommit(),
        FilterStack = Snapshots.CrowdedFilterStack(),
        ShellExtensions = new ShellExtensionInventory(
            Snapshots.Now,
            new[] { new ShellExtensionInfo("{clsid}", "OneDrive overlay", ShellExtensionKinds.IconOverlay, "Microsoft", true) },
            IconOverlayCount: 1,
            ThirdPartyIconOverlayCount: 0,
            "windows-shell-extension-registry"),
        FileSystem = new FileSystemContext(
            Snapshots.Now,
            new[] { new KnownFolderRedirect("Documents", @"C:\Users\marc\OneDrive\Documents", true) },
            Array.Empty<MappedDrive>(),
            DehydratedPlaceholderCount: 3,
            "windows-known-folders-and-net-use")
    };

    /// <summary>
    /// The unelevated machine, which is the case that matters: fltmc refused, the memory probe could not open
    /// most of the process table, and the kernel trace would have returned nothing.
    /// </summary>
    private static FakePlatformProbe UnelevatedProbe() => new()
    {
        FilterStack = FilterDriverStack.Unavailable("fltmc-requires-elevation"),
        Memory = MemoryPressureDetail.Unavailable("windows-memory-pressure-unavailable"),
        ShellExtensions = ShellExtensionInventory.Unavailable("windows-shell-extension-registry-read-failed"),
        FileSystem = FileSystemContext.Unavailable("windows-file-system-context-failed")
    };

    [Fact]
    public void SelfTestNamesEachDegradedCollectorItsReasonAndItsCost()
    {
        var report = new SelfTestService(UnelevatedProbe()).Run();

        var filterStack = Assert.Single(
            report.Readiness.Collectors,
            collector => collector.Collector == CollectorReadinessCheck.FilterDriverStackCollector);
        Assert.Equal(ProbeStatus.Unavailable, filterStack.Status);
        Assert.Contains("fltmc-requires-elevation", filterStack.Reason, StringComparison.Ordinal);
        Assert.Contains("SecurityOrSearchScanner cannot be tested AT ALL", filterStack.Cost, StringComparison.Ordinal);
        Assert.Contains("elevated terminal", filterStack.Fix, StringComparison.OrdinalIgnoreCase);

        var memory = Assert.Single(
            report.Readiness.Collectors,
            collector => collector.Collector == CollectorReadinessCheck.MemoryAccountingCollector);
        Assert.Equal(ProbeStatus.Unavailable, memory.Status);
        Assert.Contains("UnaccountedCommitBytes is unreliable", memory.Cost, StringComparison.Ordinal);
        Assert.Contains("falsely indicated, or missed", memory.Cost, StringComparison.Ordinal);

        var trace = Assert.Single(
            report.Readiness.Collectors,
            collector => collector.Collector == CollectorReadinessCheck.DriverTraceCollector);
        Assert.NotEqual(ProbeStatus.Live, trace.Status);
        Assert.Contains("DriverInterruptLatency cannot NAME a driver", trace.Cost, StringComparison.Ordinal);

        var shellExtensions = Assert.Single(
            report.Readiness.Collectors,
            collector => collector.Collector == CollectorReadinessCheck.ShellExtensionsCollector);
        Assert.Equal(ProbeStatus.Degraded, shellExtensions.Status);
        Assert.Contains("ShellExtensionBlocking cannot be tested", shellExtensions.Cost, StringComparison.Ordinal);

        var fileSystem = Assert.Single(
            report.Readiness.Collectors,
            collector => collector.Collector == CollectorReadinessCheck.FileSystemContextCollector);
        Assert.Equal(ProbeStatus.Degraded, fileSystem.Status);

        Assert.False(report.Readiness.CanRecordLeakHunt);
        Assert.False(report.ReadyToDiagnose);
    }

    [Fact]
    public void SelfTestReportsHealthyCollectorsWhenTheyReturnRealData()
    {
        var report = new SelfTestService(HealthyProbe()).Run();

        Assert.DoesNotContain(
            report.Readiness.Collectors,
            collector => collector.Collector != CollectorReadinessCheck.DriverTraceCollector && !collector.IsHealthy);

        var filterStack = Assert.Single(
            report.Readiness.Collectors,
            collector => collector.Collector == CollectorReadinessCheck.FilterDriverStackCollector);
        Assert.Equal(ProbeStatus.Live, filterStack.Status);
        Assert.Contains("third-party", filterStack.Reason, StringComparison.Ordinal);

        // The driver trace is the only collector that cannot be healthy on a test host: it is inferred from
        // elevation, and elevation is unknowable off Windows. It is advisory, so it never blocks a run.
        Assert.True(report.Readiness.CanRecordLeakHunt);
    }

    [Fact]
    public void SelfTestNeverStartsAKernelTrace()
    {
        // The self test runs on every GUI launch. Probing the trace, even for a second, would mean a kernel ETW
        // session every time OneLag opens; elevation tells us the same thing for free.
        var platform = HealthyProbe();

        _ = new SelfTestService(platform).Run();

        Assert.Equal(0, platform.DriverTraceCalls);
    }

    [Fact]
    public void PreflightBlocksWhenTheFilterStackIsUnavailableAndTheFlagIsAbsent()
    {
        var readiness = CollectorReadinessCheck.Evaluate(
            new FakePlatformProbe
            {
                FilterStack = FilterDriverStack.Unavailable("fltmc-requires-elevation"),
                Memory = Snapshots.HeavyUnaccountedCommit(),
                ShellExtensions = HealthyProbe().ShellExtensions,
                FileSystem = HealthyProbe().FileSystem
            });

        var decision = WatchPreflight.Evaluate(readiness, acknowledged: false);

        Assert.False(decision.CanProceed);
        Assert.Contains("REFUSING TO START", decision.Message, StringComparison.Ordinal);
        Assert.Contains("fltmc-requires-elevation", decision.Message, StringComparison.Ordinal);
        Assert.Contains("SecurityOrSearchScanner", decision.Message, StringComparison.Ordinal);
        Assert.Contains(WatchPreflight.AcknowledgementFlag, decision.Message, StringComparison.Ordinal);
        Assert.Equal(
            CollectorReadinessCheck.FilterDriverStackCollector,
            Assert.Single(decision.Blocking).Collector);
    }

    [Fact]
    public void PreflightBlocksWhenTheMemoryAccountingIsPartial()
    {
        // The dangerous case: the probe returns numbers, but a tenth of the process table was unreadable, so
        // their private bytes land in UnaccountedCommitBytes and read as a kernel leak that is not there.
        var readiness = CollectorReadinessCheck.Evaluate(
            new FakePlatformProbe
            {
                FilterStack = Snapshots.CrowdedFilterStack(),
                Memory = Snapshots.HeavyUnaccountedCommit() with
                {
                    EvidenceState = "windows-performance-info-partial-process-accounting",
                    ProcessesSampled = 240,
                    ProcessesInaccessible = 61
                }
            });

        var decision = WatchPreflight.Evaluate(readiness, acknowledged: false);

        Assert.False(decision.CanProceed);
        Assert.Equal(
            CollectorReadinessCheck.MemoryAccountingCollector,
            Assert.Single(decision.Blocking).Collector);
        Assert.Contains("kernel leak may be falsely indicated", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightProceedsWhenTheAcknowledgementFlagIsPresent()
    {
        var readiness = CollectorReadinessCheck.Evaluate(UnelevatedProbe());

        var decision = WatchPreflight.Evaluate(readiness, acknowledged: true);

        Assert.True(decision.CanProceed);
        Assert.NotEmpty(decision.Blocking);
        Assert.Contains("Proceeding anyway", decision.Message, StringComparison.Ordinal);
        Assert.Contains("Do not read its silence as a clean bill of health", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightProceedsWhenTheCollectorsAreHealthy()
    {
        var readiness = CollectorReadinessCheck.Evaluate(HealthyProbe());

        var decision = WatchPreflight.Evaluate(readiness, acknowledged: false);

        Assert.True(decision.CanProceed);
        Assert.Empty(decision.Blocking);
    }

    [Fact]
    public void PreflightWarnsButDoesNotBlockOnADegradedShellExtensionProbe()
    {
        // Losing the overlay handlers costs one hypothesis. It cannot corrupt the memory series, which is what
        // an all-day leak hunt is for, so it warns and the run goes ahead.
        var platform = HealthyProbe();
        platform.ShellExtensions = ShellExtensionInventory.Unavailable("windows-shell-extension-registry-read-failed");

        var decision = WatchPreflight.Evaluate(CollectorReadinessCheck.Evaluate(platform), acknowledged: false);

        Assert.True(decision.CanProceed);
        Assert.Empty(decision.Blocking);
        Assert.Contains("[PARTIAL]", decision.Message, StringComparison.Ordinal);
        Assert.Contains(CollectorReadinessCheck.ShellExtensionsCollector, decision.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("REFUSING TO START", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FreezeReadinessIsJudgedFromTheReadingsTheCaptureAlreadyTook()
    {
        // `onelag freeze` warns and continues where `watch start` refuses, and it must not pay to re-probe a
        // frozen machine to find that out. The same judgement runs over the readings the capture already holds.
        var capture = new FreezeCaptureService(UnelevatedProbe()).Capture(
            new FreezeCaptureOptions(TimeSpan.FromSeconds(1), "typing froze", SkipDriverTrace: true));

        var readiness = CollectorReadinessCheck.Evaluate(
            capture.FilterStack,
            capture.Memory,
            shellExtensions: null,
            capture.FileSystem,
            elevated: false);

        Assert.False(readiness.CanRecordLeakHunt);
        Assert.Contains(readiness.Degraded, collector => collector.Collector == CollectorReadinessCheck.FilterDriverStackCollector);
        Assert.Contains("NOT RUNNING ELEVATED", readiness.ElevationLine, StringComparison.Ordinal);
    }
}
