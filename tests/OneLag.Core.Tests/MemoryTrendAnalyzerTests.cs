using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// The analyzer's whole job is to separate the thing that is growing from the thing that is merely large.
/// These tests synthesize machines whose truth is known — a climber among flat processes, a big innocent
/// process, and a kernel leak with no guilty process at all — and assert that the analyzer names the right one.
/// The kernel case is the one that matters most: it is the likeliest real answer for the machine that motivated
/// this feature, and the one no process list can ever show.
/// </summary>
public sealed class MemoryTrendAnalyzerTests
{
    private const long MB = 1024L * 1024;
    private const long GB = 1024L * MB;
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    [Fact]
    public void RanksTheGrowingProcessFirstAndIgnoresTheFlatOnes()
    {
        // leaky.exe climbs 200 MB/hour; everything else is flat. Commit climbs with it, so the growth is fully
        // accounted for by a process and the kernel must not be blamed.
        var samples = Series(
            count: 9,
            minutesApart: 30,
            memory: index => Memory(
                index,
                commitBytes: (8 * GB) + (long)(index * 0.5 * 100 * MB),
                sumOfPrivateBytes: (5 * GB) + (long)(index * 0.5 * 100 * MB),
                processes: new[]
                {
                    Process("chrome", 1010, 4 * GB),
                    Process("leaky", 2020, (500 * MB) + (long)(index * 0.5 * 200 * MB)),
                    Process("explorer", 3030, 300 * MB)
                }));

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        Assert.True(trend.HasSufficientData);
        var worst = trend.GrowingProcesses[0];
        Assert.Equal("leaky", worst.Name);
        Assert.Equal(200, worst.MegabytesPerHour, 1);
        Assert.False(trend.KernelLeakSuspected);
        Assert.Contains("leaky", trend.Summary);
    }

    [Fact]
    public void ALargeButFlatProcessIsNotFlagged()
    {
        var samples = Series(
            count: 9,
            minutesApart: 30,
            memory: index => Memory(
                index,
                commitBytes: 8 * GB,
                sumOfPrivateBytes: 5 * GB,
                processes: new[]
                {
                    // Four gigabytes and utterly innocent: size is not guilt.
                    Process("chrome", 1010, 4 * GB),
                    Process("explorer", 3030, 300 * MB)
                }));

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        Assert.True(trend.HasSufficientData);
        Assert.Empty(trend.GrowingProcesses);
        Assert.False(trend.KernelLeakSuspected);
        Assert.DoesNotContain("chrome", trend.Summary);
    }

    [Fact]
    public void FlatProcessesWithClimbingUnaccountedCommitAreReportedAsAKernelLeak()
    {
        // The user's most likely real case: every process is flat, the sum of private bytes never moves, and yet
        // the machine commits another gigabyte every hour. Nothing in Task Manager can show this.
        var samples = Series(
            count: 9,
            minutesApart: 30,
            memory: index => Memory(
                index,
                commitBytes: (8 * GB) + (long)(index * 0.5 * GB),
                sumOfPrivateBytes: 5 * GB,
                processes: new[]
                {
                    Process("chrome", 1010, 4 * GB),
                    Process("explorer", 3030, 300 * MB)
                }));

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        Assert.True(trend.HasSufficientData);
        Assert.True(trend.KernelLeakSuspected);
        Assert.Empty(trend.GrowingProcesses);
        Assert.Equal(1024, trend.UnaccountedCommit!.MegabytesPerHour, 1);
        Assert.Equal(1024, trend.Commit!.MegabytesPerHour, 1);
        Assert.Contains("kernel or driver leak", trend.Summary);
    }

    [Fact]
    public void TooFewSamplesReportInsufficientDataRatherThanABogusTrend()
    {
        var samples = Series(
            count: 2,
            minutesApart: 30,
            memory: index => Memory(index, commitBytes: (8 * GB) + (index * GB), sumOfPrivateBytes: 5 * GB, processes: Array.Empty<ProcessCommitSample>()));

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        Assert.False(trend.HasSufficientData);
        Assert.Null(trend.Commit);
        Assert.False(trend.KernelLeakSuspected);
        Assert.Contains("Not enough memory samples", trend.Summary);
    }

    [Fact]
    public void SamplesWithoutMemoryAreNotCountedAsZero()
    {
        // Memory rides a slower cadence than the loop, so most samples carry no memory at all. A null must mean
        // "not sampled", never "nothing there".
        var withMemory = Series(
            count: 4,
            minutesApart: 30,
            memory: index => Memory(index, commitBytes: 8 * GB, sumOfPrivateBytes: 5 * GB, processes: Array.Empty<ProcessCommitSample>()));

        var mixed = withMemory
            .SelectMany(sample => new[] { sample with { Memory = null }, sample })
            .ToArray();

        var trend = MemoryTrendAnalyzer.Analyze(mixed);

        Assert.True(trend.HasSufficientData);
        Assert.Equal(4, trend.MemorySamples);
        Assert.Equal(0, trend.Commit!.MegabytesPerHour, 1);
    }

    [Fact]
    public void PidReuseDoesNotManufactureATrend()
    {
        // PID 2020 belongs to a small process, exits, and is handed to a large one. Splicing the two would
        // invent a violent leak that never happened, so both series are dropped instead.
        var samples = Series(
            count: 9,
            minutesApart: 30,
            memory: index => Memory(
                index,
                commitBytes: 8 * GB,
                sumOfPrivateBytes: 5 * GB,
                processes: index < 4
                    ? new[] { Process("small", 2020, 100 * MB), Process("explorer", 3030, 300 * MB) }
                    : new[] { Process("huge", 2020, 6 * GB), Process("explorer", 3030, 300 * MB) }));

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        Assert.True(trend.HasSufficientData);
        Assert.DoesNotContain(trend.GrowingProcesses, series => series.ProcessId == 2020);
        Assert.Empty(trend.GrowingProcesses);
    }

    [Fact]
    public void WindowsNamedLeakCandidatesSurviveIntoTheTrend()
    {
        var candidate = new MemoryLeakCandidate("StartMenuExperienceHost", 4242, Start, TimeSpan.FromDays(9), "windows-radar-pre-leak-64");
        var samples = Series(
            count: 4,
            minutesApart: 30,
            memory: index => Memory(index, commitBytes: 8 * GB, sumOfPrivateBytes: 5 * GB, processes: Array.Empty<ProcessCommitSample>()) with
            {
                LeakCandidates = new[] { candidate }
            });

        var trend = MemoryTrendAnalyzer.Analyze(samples);

        Assert.Equal("StartMenuExperienceHost", Assert.Single(trend.WindowsLeakCandidates).ProcessName);
    }

    private static IReadOnlyList<WatchSample> Series(int count, int minutesApart, Func<int, MemoryPressureDetail> memory)
    {
        return Enumerable.Range(0, count)
            .Select(index => new WatchSample(
                Start.AddMinutes(index * minutesApart),
                5,
                Snapshots.QuietTelemetry(),
                Snapshots.QuietPressure(),
                "explorer",
                Snapshots.UndockedHost(),
                Snapshots.ResponsiveShell(),
                memory(index)))
            .ToArray();
    }

    private static MemoryPressureDetail Memory(
        int index,
        long commitBytes,
        long sumOfPrivateBytes,
        IReadOnlyList<ProcessCommitSample> processes)
    {
        return new MemoryPressureDetail(
            Start.AddMinutes(index * 30),
            CommitTotalBytes: commitBytes,
            CommitLimitBytes: 64 * GB,
            PhysicalTotalBytes: 32 * GB,
            PhysicalAvailableBytes: 8 * GB,
            SystemUptime: TimeSpan.FromDays(9),
            TopCommitProcesses: processes,
            LeakCandidates: Array.Empty<MemoryLeakCandidate>(),
            EvidenceState: "windows-toolhelp-process-and-performance-counters",
            SumOfProcessPrivateBytes: sumOfPrivateBytes,
            ProcessesSampled: 240,
            ProcessesInaccessible: 0);
    }

    private static ProcessCommitSample Process(string name, int pid, long privateBytes) =>
        new(name, pid, privateBytes, privateBytes);
}
