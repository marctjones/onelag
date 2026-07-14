using System.Text.Json.Serialization;

namespace OneLag.Core;

public enum DifferentialDiagnosis
{
    OneDriveLikely,
    OneDrivePossible,
    OneDriveNotProven,
    NonOneDrivePressureSuspected,
    Unknown
}

public enum Severity
{
    Info,
    Warning,
    HighRisk,
    Emergency
}

public enum RecommendationKind
{
    Observe,
    PauseSync,
    MoveOutOfOneDrive,
    FreeUpSpace,
    SelectiveSync,
    ResetOneDrive,
    SupportAndRecoveryAssistant,
    EscalateToEventViewer,
    EscalateToProcmonOrWpr,
    ReviewUnsupportedConfiguration
}

public enum EpisodeCategory
{
    StoragePressure,
    CpuStarvation,
    MemoryPaging,
    DriverOrDpcSuspected,
    DisplayOrDockSuspected,
    InputOrBluetoothSuspected,
    ShellBlocked,
    ForegroundAppBlocked,
    OneDrivePossible,
    Unknown
}

/// <summary>
/// Candidate causes of desktop lag. OneDrive is one hypothesis among several, not the default.
/// </summary>
public enum HypothesisKind
{
    OneDriveSync,
    StorageSaturation,
    CpuContention,
    MemoryPaging,
    DriverInterruptLatency,
    DisplayOrDockPipeline,
    BluetoothOrInputRadio,
    ShellExtensionBlocking,
    SecurityOrSearchScanner,
    ThermalOrPowerThrottling
}

public enum HypothesisVerdict
{
    RuledOut,
    NotSupported,
    Possible,
    Likely,
    StronglySupported,
    Unknown
}

/// <summary>
/// How much of the evidence a verdict depends on was actually collectable. A verdict built on an
/// insufficient snapshot must not be presented with the same weight as one built on live evidence.
/// </summary>
public enum EvidenceGrade
{
    Insufficient,
    Partial,
    Complete
}

public sealed record RootCandidate(
    string Path,
    string Source,
    string Confidence,
    string? AccountKind);

public sealed record ScanOptions(
    IReadOnlyList<string> Roots,
    string OutputPath,
    string Format,
    bool FullPaths,
    int MaxItems = 500_000,
    TimeSpan? DriverTraceDuration = null);

public sealed record DirectoryRisk(
    string Path,
    string Name,
    string Reason,
    long ItemCountEstimate);

public sealed record SyncBlocker(
    string Path,
    string Kind,
    string Evidence,
    Severity Severity);

public sealed record InventorySummary(
    string Root,
    long FileCount,
    long DirectoryCount,
    long TotalBytes,
    int MaxDepth,
    bool WasCapped,
    IReadOnlyList<string> InaccessiblePaths,
    IReadOnlyList<TopLevelInventory> TopLevelItems,
    IReadOnlyList<DirectoryRisk> HighRiskDirectories,
    IReadOnlyList<SyncBlocker> SyncBlockers)
{
    [JsonIgnore]
    public long TotalItems => FileCount + DirectoryCount;
}

public sealed record TopLevelInventory(
    string Path,
    string Name,
    long FileCount,
    long DirectoryCount,
    long TotalBytes)
{
    [JsonIgnore]
    public long TotalItems => FileCount + DirectoryCount;
}

public sealed record ProcessSample(
    string Name,
    int ProcessId,
    long WorkingSetBytes,
    TimeSpan TotalProcessorTime,
    string? Path,
    double? CpuPercent = null);

public sealed record PerformanceSignal(
    string Kind,
    double? Value,
    string Unit,
    string EvidenceState);

public sealed record ProcessPressureSample(
    string Name,
    int ProcessId,
    double CpuPercent,
    long WorkingSetBytes,
    string? Path);

public sealed record TelemetrySnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<ProcessSample> OneDriveProcesses,
    int OneDriveLogFilesChangedLastMinute,
    string? OneDriveVersion,
    string EvidenceState);

public sealed record SystemPressureSnapshot(
    DateTimeOffset Timestamp,
    string CpuState,
    string MemoryState,
    string DiskState,
    string PowerState,
    IReadOnlyList<string> TopProcesses,
    string EvidenceState,
    IReadOnlyList<PerformanceSignal>? Signals = null,
    IReadOnlyList<ProcessPressureSample>? TopProcessSamples = null);

public sealed record DisplayInfo(
    string Name,
    string OutputTechnology,
    bool IsInternal,
    bool IsIndirect,
    int Width,
    int Height,
    double RefreshHz);

/// <summary>
/// The machine's physical environment at sample time: what is plugged in, what radios are live, and
/// whether the laptop is docked. Lag that only appears in one of these configurations is a hardware or
/// driver problem, not a sync problem, and no amount of OneDrive inventory will show that.
/// </summary>
public sealed record HostContext(
    DateTimeOffset Timestamp,
    int DisplayCount,
    int ExternalDisplayCount,
    int IndirectDisplayCount,
    IReadOnlyList<DisplayInfo> Displays,
    bool? BluetoothRadioPresent,
    bool? BluetoothRadioEnabled,
    int? ConnectedBluetoothDevices,
    string PowerSource,
    bool? WiredNetworkUp,
    IReadOnlyList<string> IndirectDisplayDrivers,
    string DockState,
    string EvidenceState)
{
    public static HostContext Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        0,
        0,
        0,
        Array.Empty<DisplayInfo>(),
        null,
        null,
        null,
        "unknown",
        null,
        Array.Empty<string>(),
        DockStates.Unknown,
        evidenceState);
}

public static class DockStates
{
    public const string DockedLikely = "docked-likely";
    public const string UndockedLikely = "undocked-likely";
    public const string Unknown = "unknown";
}

/// <summary>
/// Direct measurement of whether the Explorer shell is blocked. The source guide's core failure mode is a
/// stalled sync-status query blocking Explorer, which OneLag previously inferred but never tested.
/// </summary>
public sealed record ShellResponsiveness(
    DateTimeOffset Timestamp,
    bool? ExplorerRunning,
    bool? ShellWindowHung,
    int HungTopLevelWindows,
    double? ShellPumpLatencyMilliseconds,
    string EvidenceState)
{
    public static ShellResponsiveness Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        null,
        null,
        0,
        null,
        evidenceState);
}

public sealed record DriverLatencySample(
    string Driver,
    string Kind,
    double TotalMilliseconds,
    double MaxMilliseconds,
    long Count);

/// <summary>
/// Attribution of kernel DPC and ISR time to specific driver images.
///
/// The DPC counters can say that a driver is stalling the machine. Only a kernel trace can say which one.
/// This is the difference between "escalate to WPR" and an answer.
/// </summary>
public sealed record DriverLatencyAttribution(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<DriverLatencySample> Drivers,
    string EvidenceState)
{
    public static DriverLatencyAttribution Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        TimeSpan.Zero,
        Array.Empty<DriverLatencySample>(),
        evidenceState);
}

/// <summary>
/// One file-system filter driver (minifilter) attached to a volume. Altitude determines load order.
/// </summary>
public sealed record FilterDriverInfo(
    string Name,
    double? Altitude,
    int Instances,
    string? Vendor,
    bool IsFileSystemFilter,
    bool IsMicrosoft);

/// <summary>
/// The file-system filter stack. Every file open — and an open-file dialog performs one per file just to
/// draw an icon — traverses every attached minifilter synchronously. A machine carrying a large third-party
/// security stack pays that cost on every directory listing, which is why Explorer and file dialogs are
/// often the only surface that visibly suffers.
///
/// This is the collector the SecurityOrSearchScanner hypothesis never had: it previously looked only at
/// Defender and Search CPU, so a machine running eleven third-party kernel filters scored zero.
/// </summary>
public sealed record FilterDriverStack(
    DateTimeOffset Timestamp,
    IReadOnlyList<FilterDriverInfo> Filters,
    int FileSystemFilterCount,
    int ThirdPartyFileSystemFilterCount,
    IReadOnlyList<string> SecurityVendors,
    bool? DefenderFilterRunning,
    bool? CloudFilesFilterRunning,
    string EvidenceState)
{
    public static FilterDriverStack Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        Array.Empty<FilterDriverInfo>(),
        0,
        0,
        Array.Empty<string>(),
        null,
        null,
        evidenceState);
}

/// <summary>
/// A process Windows itself flagged as a probable memory leak, via RADAR (Windows Error Reporting bucket
/// RADAR_PRE_LEAK_64) or the Resource-Exhaustion-Resolver. When Windows names the leaking process, that is
/// far stronger evidence than any heuristic OneLag could compute, and it was previously not collected at all.
/// </summary>
public sealed record MemoryLeakCandidate(
    string ProcessName,
    int? ProcessId,
    DateTimeOffset ObservedAt,
    TimeSpan? ProcessUptime,
    string Source);

public sealed record ProcessCommitSample(
    string Name,
    int ProcessId,
    long PrivateBytes,
    long WorkingSetBytes);

/// <summary>
/// Memory pressure in the terms that actually predict a freeze.
///
/// The previous scan reported only a commit percentage and an absolute available-MB figure, and scored both
/// against fixed thresholds — so 1.5 GB of commit headroom on a 16 GB machine scored zero. What matters is
/// headroom, the rate it is being consumed, how long the machine has been up, and which process is holding it.
/// </summary>
public sealed record MemoryPressureDetail(
    DateTimeOffset Timestamp,
    long? CommitTotalBytes,
    long? CommitLimitBytes,
    long? PhysicalTotalBytes,
    long? PhysicalAvailableBytes,
    TimeSpan? SystemUptime,
    IReadOnlyList<ProcessCommitSample> TopCommitProcesses,
    IReadOnlyList<MemoryLeakCandidate> LeakCandidates,
    string EvidenceState,
    long? KernelPagedPoolBytes = null,
    long? KernelNonPagedPoolBytes = null,
    long? SumOfProcessPrivateBytes = null,
    int? ProcessesSampled = null,
    int? ProcessesInaccessible = null)
{
    [JsonIgnore]
    public long? CommitHeadroomBytes => CommitLimitBytes.HasValue && CommitTotalBytes.HasValue
        ? CommitLimitBytes.Value - CommitTotalBytes.Value
        : null;

    [JsonIgnore]
    public double? CommitPercent => CommitLimitBytes is > 0 && CommitTotalBytes.HasValue
        ? (double)CommitTotalBytes.Value / CommitLimitBytes.Value * 100
        : null;

    [JsonIgnore]
    public double? PhysicalAvailablePercent => PhysicalTotalBytes is > 0 && PhysicalAvailableBytes.HasValue
        ? (double)PhysicalAvailableBytes.Value / PhysicalTotalBytes.Value * 100
        : null;

    /// <summary>
    /// Commit that no user-mode process accounts for.
    ///
    /// This is the number that decides where a leak lives, and OneLag could not previously see it at all.
    /// Task Manager's Details tab lists only user-mode processes, so a driver leaking kernel pool shows up
    /// nowhere in that list while still consuming commit. If the machine is holding tens of gigabytes and the
    /// sum of every process's private bytes is a small fraction of it, the memory is being held in the kernel
    /// by a driver, not by a program — and no amount of closing applications will return it.
    ///
    /// Deliberately not clamped to zero: a negative value means the accounting itself is unreliable (processes
    /// were sampled at a different instant, or access was denied for the largest consumers), and a wrong number
    /// that admits it is wrong is worth more than a plausible one that hides it. Read alongside
    /// <see cref="ProcessesInaccessible"/> before trusting it.
    /// </summary>
    [JsonIgnore]
    public long? UnaccountedCommitBytes => CommitTotalBytes.HasValue && SumOfProcessPrivateBytes.HasValue
        ? CommitTotalBytes.Value - SumOfProcessPrivateBytes.Value
        : null;

    [JsonIgnore]
    public long? KernelPoolBytes => KernelPagedPoolBytes.HasValue || KernelNonPagedPoolBytes.HasValue
        ? (KernelPagedPoolBytes ?? 0) + (KernelNonPagedPoolBytes ?? 0)
        : null;

    public static MemoryPressureDetail Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        null,
        null,
        Array.Empty<ProcessCommitSample>(),
        Array.Empty<MemoryLeakCandidate>(),
        evidenceState);
}

public sealed record ShellExtensionInfo(
    string Clsid,
    string Name,
    string Kind,
    string? Publisher,
    bool IsMicrosoft);

public static class ShellExtensionKinds
{
    public const string IconOverlay = "icon-overlay";
    public const string ContextMenu = "context-menu";
    public const string PropertySheet = "property-sheet";
}

/// <summary>
/// Registered Explorer shell extensions. Icon overlay handlers run synchronously on the Explorer UI thread
/// and Windows honours only the first ~15 by sort order, so a crowded overlay list both blocks the shell and
/// silently drops handlers. This is a classic cause of slow Explorer and slow open-file dialogs, and OneLag
/// could not see it because the log bundle contains no registry export.
/// </summary>
public sealed record ShellExtensionInventory(
    DateTimeOffset Timestamp,
    IReadOnlyList<ShellExtensionInfo> Extensions,
    int IconOverlayCount,
    int ThirdPartyIconOverlayCount,
    string EvidenceState)
{
    public const int IconOverlayLimit = 15;

    public static ShellExtensionInventory Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        Array.Empty<ShellExtensionInfo>(),
        0,
        0,
        evidenceState);
}

public sealed record KnownFolderRedirect(
    string KnownFolder,
    string Path,
    bool RedirectedIntoCloudRoot);

public sealed record MappedDrive(
    string Letter,
    string RemotePath,
    string Status,
    bool? Reachable);

/// <summary>
/// The shape of the file namespace an open-file dialog actually lands in.
///
/// Every native open-file dialog defaults to Documents. If Documents has been redirected into a cloud-synced
/// root (OneDrive Known Folder Move), then every dialog enumerates a cloud-backed folder through the Cloud
/// Files filter plus the whole security filter stack, and a single dehydrated placeholder can block it on a
/// network round-trip. A dead mapped drive does the same thing. Neither was previously collected.
/// </summary>
public sealed record FileSystemContext(
    DateTimeOffset Timestamp,
    IReadOnlyList<KnownFolderRedirect> KnownFolders,
    IReadOnlyList<MappedDrive> MappedDrives,
    int DehydratedPlaceholderCount,
    string EvidenceState)
{
    public static FileSystemContext Unavailable(string evidenceState) => new(
        DateTimeOffset.UtcNow,
        Array.Empty<KnownFolderRedirect>(),
        Array.Empty<MappedDrive>(),
        0,
        evidenceState);
}

/// <summary>
/// A single composite capture taken while the machine is actually lagging.
///
/// Every capture this project has ever taken was recorded while the machine was fine, and every one of them
/// produced an authoritative-looking report that could not test the hypotheses that mattered. This record is
/// the answer to that: one command, fired the instant the freeze is felt, that grabs the driver trace, the
/// memory state, the filter stack and the shell state together, so the evidence is contemporaneous with the
/// symptom rather than with a calm moment hours away from it.
/// </summary>
public sealed record FreezeCapture(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? Note,
    SystemPressureSnapshot SystemPressure,
    MemoryPressureDetail Memory,
    FilterDriverStack FilterStack,
    FileSystemContext FileSystem,
    ShellResponsiveness Shell,
    HostContext HostContext,
    TelemetrySnapshot Telemetry,
    DriverLatencyAttribution DriverLatency,
    IReadOnlyList<Hypothesis> Hypotheses,
    EvidenceQuality EvidenceQuality,
    IReadOnlyList<Finding> Findings);

public sealed record Hypothesis(
    HypothesisKind Kind,
    HypothesisVerdict Verdict,
    int Score,
    string Summary,
    IReadOnlyList<string> Supporting,
    IReadOnlyList<string> Opposing,
    string NextStep);

public sealed record EvidenceQuality(
    EvidenceGrade Grade,
    int Score,
    IReadOnlyList<string> Gaps,
    string Summary);

public sealed record EventLogSummary(
    string LogName,
    string Provider,
    int EventId,
    string Level,
    int Count,
    DateTimeOffset? NewestTimestamp);

public sealed record ClientHealthSignal(
    Severity Severity,
    string Kind,
    string Evidence,
    string Safety);

public sealed record OneDriveResetCommand(
    string ExecutablePath,
    string Arguments,
    string Source);

public sealed record OneDriveClientHealthSnapshot(
    DateTimeOffset Timestamp,
    bool InternalSyncDatabaseParsed,
    string EvidenceState,
    IReadOnlyList<ClientHealthSignal> Signals,
    IReadOnlyList<OneDriveResetCommand> ResetCommands);

public sealed record Finding(
    Severity Severity,
    string Title,
    string Evidence,
    string Confidence);

public sealed record Recommendation(
    RecommendationKind Kind,
    string Title,
    string Rationale,
    string Safety);

public sealed record DiagnosticReport(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<RootCandidate> Roots,
    IReadOnlyList<InventorySummary> Inventories,
    TelemetrySnapshot Telemetry,
    SystemPressureSnapshot SystemPressure,
    OneDriveClientHealthSnapshot OneDriveClientHealth,
    IReadOnlyList<EventLogSummary> EventLogs,
    DifferentialDiagnosis Diagnosis,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Recommendation> Recommendations,
    HostContext? HostContext = null,
    ShellResponsiveness? ShellResponsiveness = null,
    IReadOnlyList<Hypothesis>? Hypotheses = null,
    EvidenceQuality? EvidenceQuality = null,
    DriverLatencyAttribution? DriverLatency = null);

/// <summary>
/// One tick of the watch recorder.
///
/// <see cref="Memory"/> is null on most samples by design. Memory accounting walks the process table, which
/// costs far more than a counter read, so it is sampled on its own slower cadence and attached only to the
/// ticks that carried it. Anything reading this series must treat a null as "not sampled here", never as
/// "no memory pressure".
/// </summary>
public sealed record WatchSample(
    DateTimeOffset Timestamp,
    double TimerDriftMilliseconds,
    TelemetrySnapshot Telemetry,
    SystemPressureSnapshot SystemPressure,
    string? ForegroundProcess,
    HostContext? HostContext = null,
    ShellResponsiveness? ShellResponsiveness = null,
    MemoryPressureDetail? Memory = null);

public sealed record WatchMarker(
    DateTimeOffset Timestamp,
    string Source,
    string? Note);

public sealed record WatchEpisode(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    EpisodeCategory Category,
    string Evidence,
    string Confidence);
