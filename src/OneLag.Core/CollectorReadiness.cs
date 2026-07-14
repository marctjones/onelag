using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace OneLag.Core;

/// <summary>
/// One collector, and what it costs the diagnosis when it is not working.
///
/// The point of this record is the last three fields. Knowing that a collector is "unavailable" is useless to
/// the user standing in front of an 8-hour recording; knowing that without it the SecurityOrSearchScanner
/// hypothesis cannot be tested at all, and that an elevated terminal fixes it, is the whole difference between
/// a wasted day and a working one.
/// </summary>
public sealed record CollectorReadiness(
    string Collector,
    ProbeStatus Status,
    string Reason,
    string Cost,
    string Fix,
    bool BlocksLeakHunt,
    TimeSpan Elapsed)
{
    public bool IsHealthy => Status == ProbeStatus.Live;

    /// <summary>One line the user can act on: what broke, what it costs, what to do.</summary>
    public string Describe() => $"{Collector}: {Reason}{Environment.NewLine}    Cost: {Cost}{Environment.NewLine}    Fix:  {Fix}";
}

/// <summary>
/// The state of every collector an all-day leak hunt depends on, plus the one fact that most determines
/// whether a capture is worth anything at all: whether the process is elevated.
/// </summary>
public sealed record CollectorReadinessReport(
    bool? Elevated,
    IReadOnlyList<CollectorReadiness> Collectors)
{
    public IReadOnlyList<CollectorReadiness> Degraded =>
        Collectors.Where(collector => !collector.IsHealthy).ToArray();

    /// <summary>
    /// The degraded collectors that gut a leak hunt rather than merely thinning it: the filter stack (without
    /// which one whole hypothesis is untestable) and the commit accounting (without which the tool can accuse
    /// a driver of a leak that is really a process it could not open).
    /// </summary>
    public IReadOnlyList<CollectorReadiness> Blocking =>
        Collectors.Where(collector => !collector.IsHealthy && collector.BlocksLeakHunt).ToArray();

    public bool CanRecordLeakHunt => Blocking.Count == 0;

    /// <summary>
    /// Plain English, because the person reading it is about to leave a machine recording for eight hours and
    /// will not be back to check on it.
    /// </summary>
    public string ElevationLine => Elevated switch
    {
        true => "Running elevated. The kernel-level collectors can see the whole machine.",
        false => "NOT RUNNING ELEVATED. The filter-driver stack and the kernel driver trace cannot be read at all, and the process accounting that decides whether a leak is in the kernel or in a program is incomplete.",
        _ => "Elevation is unknown on this platform, so the Windows-only collectors cannot be predicted."
    };
}

/// <summary>
/// Answers the question the README puts before every other: are the collectors actually working?
///
/// This is the single source of truth for collector degradation. The self test, the watch pre-flight, the
/// freeze warning and the GUI banner all consume it, so there is exactly one place that decides what
/// "degraded" means and exactly one set of words for what it costs.
/// </summary>
public static class CollectorReadinessCheck
{
    public const string FilterDriverStackCollector = "filter-driver-stack";
    public const string MemoryAccountingCollector = "memory-accounting";
    public const string DriverTraceCollector = "driver-trace";
    public const string ShellExtensionsCollector = "shell-extensions";
    public const string FileSystemContextCollector = "file-system-context";

    /// <summary>
    /// The share of unreadable processes at which the commit accounting stops being trustworthy enough to
    /// attribute the remainder to the kernel. Mirrors the threshold the Windows memory probe itself uses when
    /// it stamps the capture "partial-process-accounting"; a handful of protected system processes is normal
    /// even under an administrator and must not block a run that nothing can improve.
    /// </summary>
    private const int InaccessibleProcessPartialPercent = 10;

    private const string PartialAccountingEvidence = "partial-process-accounting";

    private const string ElevateFix = "Re-run from an elevated terminal (right-click Windows Terminal or Command Prompt and choose 'Run as administrator').";

    private const string WrongPlatformEvidence = "unavailable-on-this-platform";

    private const string WrongPlatformFix = "Run OneLag on the Windows machine that is lagging. No terminal on this platform can read a Windows kernel.";

    /// <summary>
    /// Whether this process can read the kernel. Guarded by an OS check because WindowsIdentity is meaningless
    /// elsewhere; null therefore means "not knowable here" rather than "no", and callers must not read it as a
    /// refusal — a macOS test run has no elevation to report and must not be blocked for it.
    /// </summary>
    public static bool? IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs the collectors that can be run cheaply and reports what each of them actually returned.
    ///
    /// The kernel driver trace is deliberately NOT run here, and is inferred from the elevation check instead.
    /// This method is on the GUI's startup path (the readiness banner runs a self test on every launch), so
    /// calling CaptureDriverLatency would start a real kernel ETW session every time OneLag opens — which is
    /// precisely the kind of heavy tracing this project promised never to start on its own. Elevation is the
    /// only thing that decides whether the trace can produce anything, so inferring it costs nothing and is
    /// exactly as accurate for the question being asked.
    /// </summary>
    public static CollectorReadinessReport Evaluate(IPlatformProbe platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var filterStack = Timed(() => platform.CaptureFilterDriverStack());
        var memory = Timed(() => platform.CaptureMemoryPressure());
        var shellExtensions = Timed(() => platform.CaptureShellExtensions());
        var fileSystem = Timed(() => platform.CaptureFileSystemContext(Array.Empty<RootCandidate>()));

        return Evaluate(
            filterStack.Value,
            memory.Value,
            shellExtensions.Value,
            fileSystem.Value,
            IsElevated(),
            filterStack.Elapsed,
            memory.Elapsed,
            shellExtensions.Elapsed,
            fileSystem.Elapsed);
    }

    /// <summary>
    /// Judges collectors that have already been captured. `onelag freeze` uses this: it has just taken all of
    /// these readings during the episode, and re-probing them to check on them would cost the user real seconds
    /// while the machine is frozen.
    /// </summary>
    public static CollectorReadinessReport Evaluate(
        FilterDriverStack filterStack,
        MemoryPressureDetail memory,
        ShellExtensionInventory? shellExtensions,
        FileSystemContext? fileSystem,
        bool? elevated,
        TimeSpan filterStackElapsed = default,
        TimeSpan memoryElapsed = default,
        TimeSpan shellExtensionsElapsed = default,
        TimeSpan fileSystemElapsed = default)
    {
        ArgumentNullException.ThrowIfNull(filterStack);
        ArgumentNullException.ThrowIfNull(memory);

        var collectors = new List<CollectorReadiness>
        {
            JudgeFilterStack(filterStack, filterStackElapsed),
            JudgeMemory(memory, memoryElapsed),
            JudgeDriverTrace(elevated)
        };

        if (shellExtensions is not null)
        {
            collectors.Add(JudgeShellExtensions(shellExtensions, shellExtensionsElapsed));
        }

        if (fileSystem is not null)
        {
            collectors.Add(JudgeFileSystem(fileSystem, fileSystemElapsed));
        }

        return new CollectorReadinessReport(elevated, collectors);
    }

    /// <summary>
    /// Blocking. Without the filter stack the SecurityOrSearchScanner hypothesis has nothing to score against —
    /// it is not weakened, it is untestable — and a machine carrying eleven third-party minifilters would come
    /// back from an 8-hour run looking clean.
    /// </summary>
    private static CollectorReadiness JudgeFilterStack(FilterDriverStack stack, TimeSpan elapsed)
    {
        if (stack.Filters.Count > 0)
        {
            return new CollectorReadiness(
                FilterDriverStackCollector,
                ProbeStatus.Live,
                $"{stack.Filters.Count:N0} filter(s) enumerated, {stack.ThirdPartyFileSystemFilterCount:N0} third-party in the file I/O path",
                "SecurityOrSearchScanner can be tested against the real filter stack.",
                "Nothing to fix.",
                BlocksLeakHunt: true,
                elapsed);
        }

        var fix = stack.EvidenceState switch
        {
            "fltmc-requires-elevation" => ElevateFix,
            "fltmc-not-found" => "fltmc.exe was not found on this machine. It ships with Windows; on a stripped image the filter stack cannot be read at all and this hypothesis must be tested by hand.",
            "fltmc-timed-out" => "fltmc did not answer in time, which itself suggests the file I/O path is stalled. Re-run the self test; if it times out again from an elevated terminal, treat that as evidence.",
            WrongPlatformEvidence => WrongPlatformFix,
            _ => ElevateFix
        };

        return new CollectorReadiness(
            FilterDriverStackCollector,
            ProbeStatus.Unavailable,
            $"the minifilter stack could not be read ({stack.EvidenceState})",
            "SecurityOrSearchScanner cannot be tested AT ALL. Every file open traverses every attached minifilter, and OneLag will not be able to see a single one of them, so a machine stalled by its security stack will come back from this run looking clean.",
            fix,
            BlocksLeakHunt: true,
            elapsed);
    }

    /// <summary>
    /// Blocking, and this is the subtle one. Partial process accounting does not merely lose evidence, it
    /// manufactures it: every process the tool could not open is commit that lands in UnaccountedCommitBytes
    /// and reads as a kernel leak. An 8-hour run that ends by accusing a driver of leaking 9 GB that was really
    /// a protected process is worse than no run at all, which is why an unreliable accounting blocks rather
    /// than warns.
    /// </summary>
    private static CollectorReadiness JudgeMemory(MemoryPressureDetail memory, TimeSpan elapsed)
    {
        const string Cost = "UnaccountedCommitBytes is unreliable, so the one question this run exists to answer — is the memory being held by a driver in the kernel or by a program — cannot be answered. A kernel leak may be falsely indicated, or missed entirely.";

        if (memory.CommitTotalBytes is null)
        {
            return new CollectorReadiness(
                MemoryAccountingCollector,
                ProbeStatus.Unavailable,
                $"no commit accounting at all ({memory.EvidenceState})",
                Cost,
                memory.EvidenceState == WrongPlatformEvidence ? WrongPlatformFix : ElevateFix,
                BlocksLeakHunt: true,
                elapsed);
        }

        if (IsPartialAccounting(memory))
        {
            var sampled = memory.ProcessesSampled ?? 0;
            var inaccessible = memory.ProcessesInaccessible ?? 0;
            return new CollectorReadiness(
                MemoryAccountingCollector,
                ProbeStatus.Degraded,
                $"{inaccessible:N0} of {sampled:N0} process(es) could not be opened, so their private bytes are counted as unaccounted commit ({memory.EvidenceState})",
                Cost,
                ElevateFix,
                BlocksLeakHunt: true,
                elapsed);
        }

        var note = memory.ProcessesInaccessible is > 0
            ? $", {memory.ProcessesInaccessible:N0} protected process(es) unreadable (below the {InaccessibleProcessPartialPercent}% floor at which the accounting stops being trustworthy)"
            : string.Empty;

        return new CollectorReadiness(
            MemoryAccountingCollector,
            ProbeStatus.Live,
            $"commit accounted across {memory.ProcessesSampled ?? 0:N0} process(es){note}",
            "A kernel leak can be distinguished from a process leak.",
            "Nothing to fix.",
            BlocksLeakHunt: true,
            elapsed);
    }

    /// <summary>
    /// Advisory, not blocking. Without the trace, DriverInterruptLatency still scores from the DPC and ISR
    /// counters — it can still say a driver is stalling the machine — it just cannot name the driver file. That
    /// thins the conclusion; it does not falsify it, and it is not what an all-day memory hunt is for.
    /// </summary>
    private static CollectorReadiness JudgeDriverTrace(bool? elevated)
    {
        if (elevated == true)
        {
            return new CollectorReadiness(
                DriverTraceCollector,
                ProbeStatus.Live,
                "elevated, so a kernel ETW trace can be started on demand (not started by this check)",
                "DriverInterruptLatency can name the driver file responsible for DPC/ISR time.",
                "Nothing to fix.",
                BlocksLeakHunt: false,
                TimeSpan.Zero);
        }

        return new CollectorReadiness(
            DriverTraceCollector,
            ProbeStatus.Unavailable,
            elevated == false
                ? "the kernel trace would return nothing (requires-administrator)"
                : "the kernel trace is not available on this platform (unavailable-on-this-platform)",
            "DriverInterruptLatency cannot NAME a driver. The DPC and ISR counters can still show that a driver is stalling the machine, but no file will be named, and `--auto-capture-trace` will record empty traces.",
            elevated == false ? ElevateFix : "Run OneLag on the Windows machine that is lagging.",
            BlocksLeakHunt: false,
            TimeSpan.Zero);
    }

    /// <summary>
    /// Advisory. Icon overlay handlers run synchronously on the shell UI thread, so losing this collector loses
    /// ShellExtensionBlocking — a real cost, but one that cannot corrupt any other conclusion, and one that has
    /// nothing to do with a memory leak. It warns; it does not stop a run.
    /// </summary>
    private static CollectorReadiness JudgeShellExtensions(ShellExtensionInventory inventory, TimeSpan elapsed)
    {
        if (inventory.Extensions.Count > 0)
        {
            return new CollectorReadiness(
                ShellExtensionsCollector,
                ProbeStatus.Live,
                $"{inventory.Extensions.Count:N0} shell extension(s), {inventory.ThirdPartyIconOverlayCount:N0} third-party icon overlay handler(s)",
                "ShellExtensionBlocking can be tested.",
                "Nothing to fix.",
                BlocksLeakHunt: false,
                elapsed);
        }

        return new CollectorReadiness(
            ShellExtensionsCollector,
            ProbeStatus.Degraded,
            $"no shell extensions were read ({inventory.EvidenceState})",
            "ShellExtensionBlocking cannot be tested, so an overlay handler blocking Explorer's UI thread will not be named. This does not affect the memory or driver evidence.",
            "Usually a registry read failure. The run is still worth recording without it.",
            BlocksLeakHunt: false,
            elapsed);
    }

    /// <summary>
    /// Advisory, for the same reason as the shell extensions: a missing file-system context loses the dead
    /// mapped drive and the Known Folder Move explanation of a slow open-file dialog, but it cannot make the
    /// memory series lie.
    /// </summary>
    private static CollectorReadiness JudgeFileSystem(FileSystemContext context, TimeSpan elapsed)
    {
        if (context.KnownFolders.Count > 0 || context.MappedDrives.Count > 0)
        {
            return new CollectorReadiness(
                FileSystemContextCollector,
                ProbeStatus.Live,
                $"{context.KnownFolders.Count:N0} known folder(s), {context.MappedDrives.Count:N0} mapped drive(s)",
                "Redirected known folders and dead mapped drives can be seen.",
                "Nothing to fix.",
                BlocksLeakHunt: false,
                elapsed);
        }

        return new CollectorReadiness(
            FileSystemContextCollector,
            ProbeStatus.Degraded,
            $"no known folders or mapped drives were read ({context.EvidenceState})",
            "A dead mapped drive or a Documents folder redirected into OneDrive will not be named as the reason open-file dialogs hang. This does not affect the memory or driver evidence.",
            "The run is still worth recording without it.",
            BlocksLeakHunt: false,
            elapsed);
    }

    private static bool IsPartialAccounting(MemoryPressureDetail memory)
    {
        if (memory.EvidenceState.Contains(PartialAccountingEvidence, StringComparison.Ordinal))
        {
            return true;
        }

        // No process list at all is partial by definition: every byte of commit is unaccounted, which would
        // read as the largest kernel leak ever recorded.
        if (memory.SumOfProcessPrivateBytes is null || memory.ProcessesSampled is null or 0)
        {
            return true;
        }

        var inaccessible = memory.ProcessesInaccessible ?? 0;
        return inaccessible * 100 >= memory.ProcessesSampled.Value * InaccessibleProcessPartialPercent;
    }

    private static (T Value, TimeSpan Elapsed) Timed<T>(Func<T> capture)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = capture();
        stopwatch.Stop();
        return (value, stopwatch.Elapsed);
    }
}
