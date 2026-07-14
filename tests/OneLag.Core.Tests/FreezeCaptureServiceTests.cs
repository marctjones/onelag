using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// FreezeCaptureService is what runs the instant the machine locks up, so it has to survive the case every
/// previous capture in this project never tested: every new probe reporting itself unavailable. A capture that
/// throws mid-freeze is worse than one that returns a degraded report, because the user loses the one window
/// where the evidence existed at all.
/// </summary>
public sealed class FreezeCaptureServiceTests
{
    private static FreezeCaptureOptions Options(bool skipDriverTrace = false) =>
        new(TimeSpan.FromSeconds(1), "test note", skipDriverTrace);

    [Fact]
    public void DoesNotThrowWhenEveryNewProbeIsUnavailable()
    {
        var probe = new FakePlatformProbe();
        var service = new FreezeCaptureService(probe);

        var capture = service.Capture(Options());

        Assert.NotNull(capture);
        Assert.Equal(MemoryPressureDetail.Unavailable("not-requested").EvidenceState, capture.Memory.EvidenceState);
        Assert.Equal(FilterDriverStack.Unavailable("not-requested").EvidenceState, capture.FilterStack.EvidenceState);
        Assert.Equal(FileSystemContext.Unavailable("not-requested").EvidenceState, capture.FileSystem.EvidenceState);
    }

    [Fact]
    public void ProducesHypothesesRankedByScoreDescending()
    {
        var probe = new FakePlatformProbe
        {
            Memory = Snapshots.HeavyUnaccountedCommit(),
            FilterStack = Snapshots.CrowdedFilterStack(),
        };
        var service = new FreezeCaptureService(probe);

        var capture = service.Capture(Options());

        Assert.NotEmpty(capture.Hypotheses);
        var scores = capture.Hypotheses.Select(h => h.Score).ToArray();
        Assert.Equal(scores.OrderByDescending(score => score).ToArray(), scores);
    }

    [Fact]
    public void SkipDriverTraceIsHonoredAndNeverCallsTheProbe()
    {
        var probe = new FakePlatformProbe { DriverLatency = Snapshots.DisplayLinkStorm() };
        var service = new FreezeCaptureService(probe);

        var capture = service.Capture(Options(skipDriverTrace: true));

        Assert.Equal(0, probe.DriverTraceCalls);
        Assert.Equal("skipped-by-request", capture.DriverLatency.EvidenceState);
    }

    [Fact]
    public void CapturesTheDriverTraceWhenNotSkipped()
    {
        var probe = new FakePlatformProbe { DriverLatency = Snapshots.DisplayLinkStorm() };
        var service = new FreezeCaptureService(probe);

        var capture = service.Capture(Options(skipDriverTrace: false));

        Assert.Equal(1, probe.DriverTraceCalls);
        Assert.Same(probe.DriverLatency, capture.DriverLatency);
    }

    /// <summary>
    /// A freeze capture must never walk a directory tree -- that is the entire reason this command exists
    /// separately from `scan`. There is no inventory input to HypothesisEngine, so any hypothesis that leans on
    /// folder shape must report itself untested rather than scoring on absent evidence, and nothing here should
    /// ever call a filesystem-walking API.
    /// </summary>
    [Fact]
    public void DoesNotDiscoverInventoryEvenWhenRootsExist()
    {
        var probe = new FakePlatformProbe
        {
            Roots = new[] { new RootCandidate(@"C:\Users\test\OneDrive", "env:OneDrive", "high", "personal") }
        };
        var service = new FreezeCaptureService(probe);

        var capture = service.Capture(Options());

        Assert.NotNull(capture);
    }

    /// <summary>
    /// The note is free text the user types while frozen ("clicks queued closing chrome windows"); it must
    /// survive unmodified into the capture so the report can show it.
    /// </summary>
    [Fact]
    public void CarriesTheNoteThrough()
    {
        var probe = new FakePlatformProbe();
        var service = new FreezeCaptureService(probe);

        var capture = service.Capture(new FreezeCaptureOptions(TimeSpan.FromSeconds(1), "clicks queued closing chrome windows"));

        Assert.Equal("clicks queued closing chrome windows", capture.Note);
    }
}
