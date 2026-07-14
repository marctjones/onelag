using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// MemoryReclaimService.Plan is pure -- it never touches a process -- so these tests run the same on the
/// macOS CI runner as on the Windows machine that motivated it: StartMenuExperienceHost.exe measured at 2.7 GB
/// against an expected footprint of roughly 100 MB.
/// </summary>
public sealed class MemoryReclaimServiceTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private const long Megabyte = 1024L * 1024;

    private static MemoryPressureDetail MemoryWith(params ProcessCommitSample[] processes)
    {
        return new MemoryPressureDetail(
            DateTimeOffset.UnixEpoch,
            CommitTotalBytes: 20 * Gigabyte,
            CommitLimitBytes: 32 * Gigabyte,
            PhysicalTotalBytes: 32 * Gigabyte,
            PhysicalAvailableBytes: 4 * Gigabyte,
            SystemUptime: TimeSpan.FromDays(5),
            TopCommitProcesses: processes,
            LeakCandidates: Array.Empty<MemoryLeakCandidate>(),
            EvidenceState: "test");
    }

    [Fact]
    public void StartMenuExperienceHostAt2Point7GbIsACandidateWithTheRightReclaimFigure()
    {
        var bloated = (long)(2.7 * Gigabyte);
        var memory = MemoryWith(new ProcessCommitSample("StartMenuExperienceHost", 4242, bloated, bloated));

        var plan = new MemoryReclaimService().Plan(memory);

        var candidate = Assert.Single(plan);
        Assert.Equal("StartMenuExperienceHost", candidate.ProcessName);
        Assert.Equal(4242, candidate.ProcessId);
        Assert.Equal(bloated, candidate.PrivateBytes);
        Assert.True(candidate.AutoRestarts);
    }

    [Fact]
    public void StartMenuExperienceHostAt90MbIsNotACandidate()
    {
        var memory = MemoryWith(new ProcessCommitSample("StartMenuExperienceHost", 4242, 90 * Megabyte, 90 * Megabyte));

        var plan = new MemoryReclaimService().Plan(memory);

        Assert.Empty(plan);
    }

    /// <summary>
    /// The 500 MB line itself: just under is not worth a shell restart, just at or over is.
    /// </summary>
    [Fact]
    public void ThresholdIsFiveHundredMegabytes()
    {
        var justUnder = MemoryWith(new ProcessCommitSample("explorer", 100, 500 * Megabyte - 1, 500 * Megabyte - 1));
        var atThreshold = MemoryWith(new ProcessCommitSample("explorer", 100, 500 * Megabyte, 500 * Megabyte));

        Assert.Empty(new MemoryReclaimService().Plan(justUnder));
        Assert.Single(new MemoryReclaimService().Plan(atThreshold));
    }

    [Fact]
    public void ExplorerAboveThresholdIsACandidate()
    {
        var bloated = 900 * Megabyte;
        var memory = MemoryWith(new ProcessCommitSample("explorer", 1500, bloated, bloated));

        var plan = new MemoryReclaimService().Plan(memory);

        var candidate = Assert.Single(plan);
        Assert.Equal("explorer", candidate.ProcessName);
        Assert.Equal(bloated, candidate.PrivateBytes);
    }

    /// <summary>
    /// The allowlist is hard-coded in the service. Nothing about MemoryPressureDetail -- not even a process
    /// named after something dangerous -- can add a new entry to it; only the two known-safe shell processes
    /// are ever candidates, regardless of what else appears in the top-commit list.
    /// </summary>
    [Fact]
    public void TheAllowlistCannotBeExtendedByInput()
    {
        var memory = MemoryWith(
            new ProcessCommitSample("chrome", 1, 4 * Gigabyte, 4 * Gigabyte),
            new ProcessCommitSample("OneDrive", 2, 2 * Gigabyte, 2 * Gigabyte),
            new ProcessCommitSample("svchost", 3, 3 * Gigabyte, 3 * Gigabyte),
            new ProcessCommitSample("System", 4, 5 * Gigabyte, 5 * Gigabyte));

        var plan = new MemoryReclaimService().Plan(memory);

        Assert.Empty(plan);
    }

    [Fact]
    public void PlanOnUnavailableMemoryReturnsEmptyWithoutThrowing()
    {
        var memory = MemoryPressureDetail.Unavailable("unavailable-on-this-platform");

        var plan = new MemoryReclaimService().Plan(memory);

        Assert.Empty(plan);
    }

    [Fact]
    public void PlanThrowsOnNullMemoryRatherThanProducingAMisleadingEmptyPlan()
    {
        Assert.Throws<ArgumentNullException>(() => new MemoryReclaimService().Plan(null!));
    }

    /// <summary>
    /// Execute refuses anything not on its own hard-coded allowlist, even if a caller somehow constructs a
    /// MemoryReclaimCandidate for a process that was never on it. This is the load-bearing safety property:
    /// OneLag must never become a general process killer.
    /// </summary>
    [Fact]
    public void ExecuteRefusesACandidateNotOnTheAllowlist()
    {
        var forged = new MemoryReclaimCandidate("winlogon", 5, 1 * Gigabyte, "forged", "forged", true);

        var results = new MemoryReclaimService().Execute(new[] { forged });

        var result = Assert.Single(results);
        Assert.False(result.Killed);
        Assert.Contains("allowlist", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
