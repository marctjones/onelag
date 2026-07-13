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

public sealed record WatchSample(
    DateTimeOffset Timestamp,
    double TimerDriftMilliseconds,
    TelemetrySnapshot Telemetry,
    SystemPressureSnapshot SystemPressure,
    string? ForegroundProcess,
    HostContext? HostContext = null,
    ShellResponsiveness? ShellResponsiveness = null);

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
