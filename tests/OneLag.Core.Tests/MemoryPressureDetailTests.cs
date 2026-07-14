using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// The accounting identity that decides where a leak lives.
///
/// These numbers are the ones actually measured on the machine under investigation: 21.9 GB committed with a
/// single Chrome tab and Webex open, where the sum of every user-mode process's private bytes came to roughly
/// 6 GB. An idle Windows 11 box with two applications should sit at 4-8 GB, so the ~15.9 GB that no process
/// accounts for is the whole finding — and because Task Manager's Details tab lists only user-mode processes,
/// it is memory the user cannot see anywhere and cannot reclaim by closing applications.
/// </summary>
public sealed class MemoryPressureDetailTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private static MemoryPressureDetail Detail(
        long? commitTotalBytes = null,
        long? sumOfProcessPrivateBytes = null,
        long? kernelPagedPoolBytes = null,
        long? kernelNonPagedPoolBytes = null,
        int? processesSampled = null,
        int? processesInaccessible = null)
    {
        return new MemoryPressureDetail(
            DateTimeOffset.UnixEpoch,
            commitTotalBytes,
            null,
            null,
            null,
            null,
            Array.Empty<ProcessCommitSample>(),
            Array.Empty<MemoryLeakCandidate>(),
            "test",
            kernelPagedPoolBytes,
            kernelNonPagedPoolBytes,
            sumOfProcessPrivateBytes,
            processesSampled,
            processesInaccessible);
    }

    /// <summary>
    /// The measurement that motivated all of this: two applications open, 21.9 GB committed, and only about
    /// 6 GB of it belonging to any process. Roughly 15.9 GB is held by something that is not a program.
    /// </summary>
    [Fact]
    public void UnaccountedCommitIsCommitTotalMinusTheSumOfEveryProcess()
    {
        var commitTotal = (long)(21.9 * Gigabyte);
        var processSum = 6 * Gigabyte;

        var detail = Detail(commitTotalBytes: commitTotal, sumOfProcessPrivateBytes: processSum);

        Assert.Equal(commitTotal - processSum, detail.UnaccountedCommitBytes);

        var unaccountedGb = detail.UnaccountedCommitBytes!.Value / (double)Gigabyte;
        Assert.Equal(15.9, unaccountedGb, precision: 1);
    }

    /// <summary>
    /// A negative figure is returned exactly as computed. It means the accounting is unreliable — processes
    /// sampled at slightly different instants, or the largest consumers denied to an unelevated scan — and
    /// clamping it to zero would erase the only evidence that the number should not be trusted.
    /// </summary>
    [Fact]
    public void NegativeUnaccountedCommitIsNotClampedBecauseItSignalsBrokenAccounting()
    {
        var detail = Detail(commitTotalBytes: 4 * Gigabyte, sumOfProcessPrivateBytes: 6 * Gigabyte);

        Assert.Equal(-2 * Gigabyte, detail.UnaccountedCommitBytes);
        Assert.True(detail.UnaccountedCommitBytes < 0);
    }

    [Fact]
    public void UnaccountedCommitIsNullWhenTheCommitTotalIsUnknown()
    {
        Assert.Null(Detail(sumOfProcessPrivateBytes: 6 * Gigabyte).UnaccountedCommitBytes);
    }

    [Fact]
    public void UnaccountedCommitIsNullWhenTheProcessSumIsUnknown()
    {
        Assert.Null(Detail(commitTotalBytes: 21 * Gigabyte).UnaccountedCommitBytes);
    }

    [Fact]
    public void KernelPoolSumsPagedAndNonPagedPools()
    {
        var detail = Detail(kernelPagedPoolBytes: 3 * Gigabyte, kernelNonPagedPoolBytes: 2 * Gigabyte);

        Assert.Equal(5 * Gigabyte, detail.KernelPoolBytes);
    }

    /// <summary>
    /// One pool read and the other not is still worth reporting: a driver leaking non-paged pool is a real
    /// diagnosis even if the paged figure went missing.
    /// </summary>
    [Fact]
    public void KernelPoolTreatsAMissingHalfAsZeroRatherThanDiscardingTheOtherHalf()
    {
        Assert.Equal(2 * Gigabyte, Detail(kernelNonPagedPoolBytes: 2 * Gigabyte).KernelPoolBytes);
        Assert.Equal(3 * Gigabyte, Detail(kernelPagedPoolBytes: 3 * Gigabyte).KernelPoolBytes);
    }

    [Fact]
    public void KernelPoolIsNullWhenNeitherPoolWasRead()
    {
        Assert.Null(Detail().KernelPoolBytes);
    }

    /// <summary>
    /// The unaccounted figure is only as good as the process walk behind it, so the counts that qualify it
    /// have to survive on the record for the report to be able to hedge.
    /// </summary>
    [Fact]
    public void ProcessAccountingCountsAreCarriedSoTheFigureCanBeQualified()
    {
        var detail = Detail(
            commitTotalBytes: (long)(21.9 * Gigabyte),
            sumOfProcessPrivateBytes: 6 * Gigabyte,
            processesSampled: 240,
            processesInaccessible: 12);

        Assert.Equal(240, detail.ProcessesSampled);
        Assert.Equal(12, detail.ProcessesInaccessible);
    }

    [Fact]
    public void UnavailableDetailClaimsNoAccountingAtAll()
    {
        var detail = MemoryPressureDetail.Unavailable("unavailable-on-this-platform");

        Assert.Null(detail.UnaccountedCommitBytes);
        Assert.Null(detail.KernelPoolBytes);
        Assert.Null(detail.SumOfProcessPrivateBytes);
        Assert.Null(detail.ProcessesSampled);
        Assert.Null(detail.ProcessesInaccessible);
    }
}
