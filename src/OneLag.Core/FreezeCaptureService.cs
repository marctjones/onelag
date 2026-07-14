namespace OneLag.Core;

public sealed record FreezeCaptureOptions(
    TimeSpan DriverTraceDuration,
    string? Note,
    bool SkipDriverTrace = false);

/// <summary>
/// Captures the machine while it is actually lagging.
///
/// Every capture this project has taken was recorded at a calm moment, and every one produced an
/// authoritative-looking report that could not test the hypotheses that mattered. `scan` walks the OneDrive
/// tree, which takes minutes and measures the wrong thing; `watch` has to be started before the episode; a
/// DPC trace on its own names a driver but says nothing about memory. None of them can be fired *during* a
/// freeze, which is the only moment the evidence exists.
///
/// This is that command. It collects the contemporaneous state — memory and commit accounting, the
/// filter-driver stack, the shell's message pump, the file namespace a dialog would land in, and an
/// optional kernel trace — in one shot, with no directory walk, so it returns while the symptom is still
/// happening. It is deliberately ordered cheapest-first: the volatile signals are sampled before the kernel
/// trace, because the trace takes seconds and the freeze may not last.
/// </summary>
public sealed class FreezeCaptureService
{
    private readonly IPlatformProbe platform;
    private readonly HypothesisEngine hypotheses = new();

    public FreezeCaptureService(IPlatformProbe platform)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public FreezeCapture Capture(FreezeCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var startedAt = DateTimeOffset.UtcNow;
        var roots = platform.DiscoverOneDriveRoots();

        // Volatile first. Memory, the shell pump, and the process table describe the freeze only while it is
        // happening; the filter stack and the file namespace do not change under us. If the user's machine
        // recovers mid-capture we want the perishable evidence already in hand.
        var pressure = platform.CaptureSystemPressure();
        var memory = platform.CaptureMemoryPressure();
        var shell = platform.CaptureShellResponsiveness();
        var telemetry = platform.CaptureTelemetry();
        var host = platform.CaptureHostContext();
        var filterStack = platform.CaptureFilterDriverStack();
        var fileSystem = platform.CaptureFileSystemContext(roots);
        var shellExtensions = platform.CaptureShellExtensions();

        var driverLatency = options.SkipDriverTrace
            ? DriverLatencyAttribution.Unavailable("skipped-by-request")
            : platform.CaptureDriverLatency(options.DriverTraceDuration, cancellationToken);

        var clientHealth = platform.CaptureOneDriveClientHealth(roots, telemetry);

        // No inventory. A freeze capture must not walk a directory tree — that would take minutes and measure
        // folder shape, which describes exposure rather than the episode in front of us. Every hypothesis that
        // needs inventory will correctly report itself as untested rather than scoring on absent evidence.
        var inventories = Array.Empty<InventorySummary>();
        var eventLogs = platform.ReadRecentEventSummaries(startedAt - TimeSpan.FromHours(24));

        var quality = EvidenceQualityAssessor.Assess(telemetry, pressure, eventLogs, host, shell);

        var inputs = new HypothesisInputs(
            inventories,
            telemetry,
            pressure,
            clientHealth,
            eventLogs,
            host,
            shell,
            quality,
            MaxTimerDriftMilliseconds: null,
            DriverLatency: driverLatency,
            Memory: memory,
            FilterStack: filterStack,
            ShellExtensions: shellExtensions,
            FileSystem: fileSystem);

        var ranked = hypotheses.Rank(inputs);

        return new FreezeCapture(
            startedAt,
            DateTimeOffset.UtcNow,
            options.Note,
            pressure,
            memory,
            filterStack,
            fileSystem,
            shell,
            host,
            telemetry,
            driverLatency,
            ranked,
            quality,
            BuildFindings(memory, filterStack, fileSystem, shell));
    }

    /// <summary>
    /// Findings state the things a reader must not miss even if they skip the ranked table — specifically the
    /// ones that change what the user should do next, rather than merely which hypothesis wins.
    /// </summary>
    private static IReadOnlyList<Finding> BuildFindings(
        MemoryPressureDetail memory,
        FilterDriverStack filterStack,
        FileSystemContext fileSystem,
        ShellResponsiveness shell)
    {
        const long GB = 1024L * 1024 * 1024;
        var findings = new List<Finding>();

        if (memory.UnaccountedCommitBytes is { } unaccounted && unaccounted >= 4 * GB)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "Committed memory is not accounted for by any process",
                $"{unaccounted / (double)GB:N1} GB of committed memory belongs to no user-mode process. Closing applications will not return it. This is the signature of a kernel or driver leak, which Task Manager cannot show because its Details tab lists only user-mode processes.",
                memory.ProcessesInaccessible is > 0 ? "medium" : "high"));
        }

        foreach (var leak in memory.LeakCandidates.Take(3))
        {
            findings.Add(new Finding(
                Severity.Warning,
                $"Windows flagged {leak.ProcessName} as a probable memory leak",
                $"{leak.Source} named `{leak.ProcessName}` at {leak.ObservedAt:O}. This is Windows' own leak detector, not a OneLag heuristic. Confirm by watching that process's private bytes over hours; a leak reappears on a schedule.",
                "high"));
        }

        if (filterStack.ThirdPartyFileSystemFilterCount >= 6)
        {
            var vendors = filterStack.SecurityVendors.Count > 0
                ? $" ({string.Join(", ", filterStack.SecurityVendors)})"
                : string.Empty;
            findings.Add(new Finding(
                Severity.Warning,
                "A large third-party file-system filter stack is in the file I/O path",
                $"{filterStack.ThirdPartyFileSystemFilterCount:N0} third-party minifilters{vendors} are attached. Every file open traverses all of them synchronously, and an open-file dialog performs one open per file just to draw its icon — which is why Explorer and file dialogs can be the only surface that visibly suffers.",
                "medium"));
        }

        var cloudFolders = fileSystem.KnownFolders.Where(folder => folder.RedirectedIntoCloudRoot).ToArray();
        if (cloudFolders.Length > 0)
        {
            findings.Add(new Finding(
                Severity.Warning,
                "Shell known folders are redirected into a cloud-synced root",
                $"{string.Join(", ", cloudFolders.Select(folder => folder.KnownFolder))} {(cloudFolders.Length == 1 ? "is" : "are")} redirected into OneDrive. Every native open-file dialog defaults to Documents, so each dialog enumerates a cloud-backed folder through the Cloud Files filter and the whole security filter stack. {fileSystem.DehydratedPlaceholderCount:N0} dehydrated placeholder(s) were found; a dialog only has to touch one to block on a network round-trip.",
                "medium"));
        }

        foreach (var drive in fileSystem.MappedDrives.Where(drive => drive.Reachable is false or null))
        {
            findings.Add(new Finding(
                Severity.Warning,
                $"Mapped drive {drive.Letter} did not respond",
                $"`{drive.Letter}` -> `{drive.RemotePath}` reported status `{drive.Status}` and did not answer within the probe's timeout. A dead mapped drive blocks open-file dialogs and Explorer for tens of seconds while the redirector times out, and it is one of the most frequently missed causes of exactly this symptom.",
                "medium"));
        }

        if (shell.ShellWindowHung == true)
        {
            findings.Add(new Finding(
                Severity.HighRisk,
                "Explorer was not pumping messages at capture time",
                $"Windows reported the shell window as hung, with {shell.HungTopLevelWindows:N0} hung top-level window(s). This was measured during the episode, not inferred from folder shape.",
                "high"));
        }

        return findings;
    }
}
