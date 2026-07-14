using System.Diagnostics;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Memory pressure measured the way a leak actually presents.
///
/// The previous sampler reported an absolute available-megabyte figure and a commit percentage, and scored
/// them against fixed thresholds. A machine sitting at 96% commit with 1.5 GB of headroom after eight days of
/// uptime therefore scored zero, while Windows' own leak detector had already named the offending process. So
/// this captures the four things that together make a leak legible: commit headroom against the limit, how
/// long the machine has been up, which processes hold the commit, and whether Windows has already accused
/// one of them.
/// </summary>
internal static class WindowsMemoryPressureProbe
{
    private const int TopProcessCount = 15;

    /// <summary>
    /// The share of unreadable processes at which the commit accounting stops being trustworthy enough to
    /// attribute the remainder to the kernel.
    /// </summary>
    private const int InaccessibleProcessPartialPercent = 10;

    private const int MaxLeakEventsPerChannel = 50;
    private const int EventQueryTimeoutMilliseconds = 5_000;

    /// <summary>
    /// A leak is diagnosed over days, not minutes. RADAR may have fired hours before the user got round to
    /// running a scan, and a window that only covered the scan itself would miss the accusation entirely.
    /// </summary>
    private static readonly TimeSpan LeakEventLookback = TimeSpan.FromDays(7);

    /// <summary>
    /// WER reports land in Application and the resource-exhaustion events in System, but channel placement
    /// varies by build, so both channels are asked for all three ids and the parser decides what is a leak.
    /// A query against a channel that has no such events simply returns nothing.
    /// </summary>
    private static readonly string[] LeakEventChannels = { "Application", "System" };

    public static MemoryPressureDetail Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return MemoryPressureDetail.Unavailable("unavailable-on-this-platform");
        }

        var now = DateTimeOffset.UtcNow;
        var memory = ReadSystemMemory();
        var accounting = ReadProcessCommitAccounting();
        var leakCandidates = ReadLeakCandidates(now, out var eventsRead);

        if (memory is null && !accounting.Read && !eventsRead)
        {
            return MemoryPressureDetail.Unavailable("windows-memory-pressure-unavailable");
        }

        return new MemoryPressureDetail(
            now,
            memory?.CommitTotalBytes,
            memory?.CommitLimitBytes,
            memory?.PhysicalTotalBytes,
            memory?.PhysicalAvailableBytes,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            accounting.TopProcesses,
            leakCandidates,
            DescribeEvidence(memory is not null, accounting, eventsRead),
            memory?.KernelPagedPoolBytes,
            memory?.KernelNonPagedPoolBytes,
            accounting.Read ? accounting.SumOfPrivateBytes : null,
            accounting.Read ? accounting.Sampled : null,
            accounting.Read ? accounting.Inaccessible : null);
    }

    /// <summary>
    /// The evidence state has to say which of the three reads survived, because a leak candidate with no
    /// commit figure and a commit figure with no leak candidate support very different conclusions, and a
    /// single "windows" label would let a partial capture read as a complete one.
    ///
    /// Partial process accounting gets its own state because it is the one failure that can produce a
    /// confident-looking lie: every process denied to an unelevated scan is commit that lands in
    /// UnaccountedCommitBytes and reads as a kernel leak. The report has to be able to say "some of this
    /// unaccounted memory may just be processes I could not open" rather than accusing a driver.
    /// </summary>
    private static string DescribeEvidence(bool memoryRead, ProcessCommitAccounting accounting, bool eventsRead)
    {
        if (memoryRead && accounting.IsPartial)
        {
            return "windows-performance-info-partial-process-accounting";
        }

        return (memoryRead, accounting.Read, eventsRead) switch
        {
            (true, true, true) => "windows-performance-info-and-radar-events",
            (true, true, false) => "windows-performance-info-process-list-radar-events-unavailable",
            (true, false, true) => "windows-performance-info-and-radar-events-process-list-unavailable",
            (true, false, false) => "windows-performance-info-only",
            (false, true, true) => "windows-process-list-and-radar-events-performance-info-unavailable",
            (false, true, false) => "windows-process-list-only",
            (false, false, true) => "windows-radar-events-only",
            _ => "windows-memory-pressure-unavailable"
        };
    }

    private static SystemMemory? ReadSystemMemory()
    {
        try
        {
            if (!WindowsPerformanceSampler.GetPerformanceInfo(
                    out var info,
                    Marshal.SizeOf<WindowsPerformanceSampler.PerformanceInformation>()))
            {
                return null;
            }

            // GetPerformanceInfo reports every memory figure in pages, not bytes.
            var pageSize = (long)info.PageSize.ToUInt64();
            if (pageSize <= 0)
            {
                return null;
            }

            // Kernel pool is where a leaking driver hides: it consumes commit but belongs to no process, so it
            // appears nowhere in Task Manager's Details tab. On a machine holding 21.9 GB with two apps open,
            // this is the figure that decides whether the memory is a program's or a driver's.
            return new SystemMemory(
                ToBytes(info.CommitTotal, pageSize),
                ToBytes(info.CommitLimit, pageSize),
                ToBytes(info.PhysicalTotal, pageSize),
                ToBytes(info.PhysicalAvailable, pageSize),
                ToBytes(info.KernelPaged, pageSize),
                ToBytes(info.KernelNonpaged, pageSize));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private static long ToBytes(UIntPtr pages, long pageSize)
    {
        return (long)pages.ToUInt64() * pageSize;
    }

    /// <summary>
    /// Ranked by private bytes rather than working set. A leaked allocation that has been paged out still
    /// holds its commit charge but has left the working set, so a working-set ranking understates exactly the
    /// process that is consuming the headroom — which is why the previous sampler could not see the leak.
    ///
    /// The sum is taken over EVERY process that can be read, not over the fifteen that get reported. The top
    /// list is for display; the sum is an accounting identity, and summing only the top fifteen would leave
    /// the tail of small processes looking like unaccounted kernel memory and falsely accuse a driver.
    /// </summary>
    private static ProcessCommitAccounting ReadProcessCommitAccounting()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return ProcessCommitAccounting.Unavailable;
        }

        var samples = new List<ProcessCommitSample>(processes.Length);
        var sumOfPrivateBytes = 0L;
        var inaccessible = 0;

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var sample = new ProcessCommitSample(
                        process.ProcessName,
                        process.Id,
                        process.PrivateMemorySize64,
                        process.WorkingSet64);

                    samples.Add(sample);
                    sumOfPrivateBytes += sample.PrivateBytes;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or NotSupportedException)
                {
                    // Protected and already-exited processes deny the read. Skipping one process must never
                    // cost the others — but it must be counted, because every process that could not be read
                    // is commit that will show up as unaccounted and be mistaken for a kernel leak.
                    inaccessible++;
                }
            }
        }

        if (samples.Count == 0)
        {
            return ProcessCommitAccounting.Unavailable;
        }

        var topProcesses = samples
            .OrderByDescending(sample => sample.PrivateBytes)
            .Take(TopProcessCount)
            .ToArray();

        return new ProcessCommitAccounting(true, topProcesses, sumOfPrivateBytes, samples.Count, inaccessible);
    }

    private static IReadOnlyList<MemoryLeakCandidate> ReadLeakCandidates(DateTimeOffset now, out bool eventsRead)
    {
        eventsRead = false;
        var milliseconds = (long)LeakEventLookback.TotalMilliseconds;

        // RADAR leak reports are logged at Information level, so the level filter the other event reads use
        // would discard every one of them.
        var query = "*[System[(EventID=1001 or EventID=1014 or EventID=2004) "
            + $"and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";

        var candidates = new List<MemoryLeakCandidate>();
        foreach (var channel in LeakEventChannels)
        {
            var xml = TryReadEventXml(channel, query);
            if (xml is null)
            {
                continue;
            }

            eventsRead = true;
            candidates.AddRange(RadarLeakEventParser.Parse(xml, now));
        }

        return candidates
            .OrderByDescending(candidate => candidate.ObservedAt)
            .ThenBy(candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? TryReadEventXml(string channel, string query)
    {
        try
        {
            using var process = Process.Start(CreateWevtutilStartInfo(channel, query));
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(EventQueryTimeoutMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                _ = error.GetAwaiter().GetResult();
                return null;
            }

            return output.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProcessStartInfo CreateWevtutilStartInfo(string channel, string query)
    {
        var startInfo = new ProcessStartInfo("wevtutil")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("qe");
        startInfo.ArgumentList.Add(channel);
        startInfo.ArgumentList.Add($"/q:{query}");
        startInfo.ArgumentList.Add("/f:xml");
        startInfo.ArgumentList.Add($"/c:{MaxLeakEventsPerChannel}");
        startInfo.ArgumentList.Add("/rd:true");
        return startInfo;
    }

    private sealed record SystemMemory(
        long CommitTotalBytes,
        long CommitLimitBytes,
        long PhysicalTotalBytes,
        long PhysicalAvailableBytes,
        long KernelPagedPoolBytes,
        long KernelNonPagedPoolBytes);

    /// <summary>
    /// The result of walking the process table: what to show, what it all adds up to, and how much of it was
    /// missed. The last of those is what keeps the unaccounted figure honest.
    /// </summary>
    private sealed record ProcessCommitAccounting(
        bool Read,
        IReadOnlyList<ProcessCommitSample> TopProcesses,
        long SumOfPrivateBytes,
        int Sampled,
        int Inaccessible)
    {
        public static readonly ProcessCommitAccounting Unavailable =
            new(false, Array.Empty<ProcessCommitSample>(), 0, 0, 0);

        /// <summary>
        /// An unelevated scan is routinely denied a handful of protected system processes, which is tolerable.
        /// Being denied a large share of them is not: the commit those processes hold silently inflates
        /// UnaccountedCommitBytes, and the tool would blame a driver for memory a program is holding.
        /// </summary>
        public bool IsPartial => Read
            && Inaccessible > 0
            && Inaccessible * 100 >= (Sampled + Inaccessible) * InaccessibleProcessPartialPercent;
    }
}
