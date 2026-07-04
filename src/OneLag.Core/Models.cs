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
    ForegroundAppBlocked,
    OneDrivePossible,
    Unknown
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
    int MaxItems = 500_000);

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
    IReadOnlyList<Recommendation> Recommendations);

public sealed record WatchSample(
    DateTimeOffset Timestamp,
    double TimerDriftMilliseconds,
    TelemetrySnapshot Telemetry,
    SystemPressureSnapshot SystemPressure,
    string? ForegroundProcess);

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
