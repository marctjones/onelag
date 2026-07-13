using System.Diagnostics;

namespace OneLag.Core;

public enum ProbeStatus
{
    Live,
    Degraded,
    Unavailable
}

public sealed record ProbeResult(
    string Probe,
    ProbeStatus Status,
    string Detail,
    string EvidenceState,
    TimeSpan Elapsed);

public sealed record SelfTestReport(
    IReadOnlyList<ProbeResult> Probes,
    EvidenceQuality Quality)
{
    public bool ReadyToDiagnose => Quality.Grade != EvidenceGrade.Insufficient
        && Probes.Count(probe => probe.Status == ProbeStatus.Live) >= 3;
}

/// <summary>
/// Runs every probe once and reports which ones actually measured something.
///
/// A watch session is only worth recording if the collectors work. The reports this project was originally
/// built to produce looked authoritative while containing nothing, because every probe had quietly degraded
/// and the report said so only in a footnote. This makes that the headline, in ten seconds, before anyone
/// spends two days recording evidence that turns out to be empty.
/// </summary>
public sealed class SelfTestService
{
    private readonly IPlatformProbe platform;

    public SelfTestService(IPlatformProbe platform)
    {
        this.platform = platform;
    }

    public SelfTestReport Run()
    {
        var probes = new List<ProbeResult>();

        var roots = Timed("onedrive-roots", () => platform.DiscoverOneDriveRoots(), discovered => discovered.Count > 0
            ? (ProbeStatus.Live, $"{discovered.Count:N0} OneDrive root(s) found", "discovered")
            : (ProbeStatus.Degraded, "no OneDrive roots found", "none"));
        probes.Add(roots.Result);

        var telemetry = Timed("onedrive-telemetry", () => platform.CaptureTelemetry(), snapshot => snapshot.OneDriveProcesses.Count > 0
            ? (ProbeStatus.Live, $"OneDrive running, {snapshot.OneDriveLogFilesChangedLastMinute:N0} log file(s) changed in the last minute", snapshot.EvidenceState)
            : (ProbeStatus.Degraded, "OneDrive is not running, so it cannot be tested as a cause right now", snapshot.EvidenceState));
        probes.Add(telemetry.Result);

        var pressure = Timed("performance-counters", () => platform.CaptureSystemPressure(), snapshot =>
        {
            var signals = snapshot.Signals ?? Array.Empty<PerformanceSignal>();
            var live = signals.Count(signal => signal.Value.HasValue);
            return live >= 6
                ? (ProbeStatus.Live, $"{live:N0} of {signals.Count:N0} counters returned a value", snapshot.EvidenceState)
                : live > 0
                    ? (ProbeStatus.Degraded, $"only {live:N0} of {signals.Count:N0} counters returned a value", snapshot.EvidenceState)
                    : (ProbeStatus.Unavailable, "no performance counters returned a value", snapshot.EvidenceState);
        });
        probes.Add(pressure.Result);

        // The DPC counters are called out separately because they are the ones that decide whether a driver
        // problem is even visible, and they are the ones most likely to be silently absent.
        var signals = pressure.Value.Signals ?? Array.Empty<PerformanceSignal>();
        var dpc = PressureClassifier.Value(signals, "processor-dpc-percent");
        var dpcPerCore = PressureClassifier.Value(signals, "processor-dpc-percent-max-core");
        probes.Add(new ProbeResult(
            "dpc-interrupt-counters",
            dpc.HasValue && dpcPerCore.HasValue ? ProbeStatus.Live : dpc.HasValue ? ProbeStatus.Degraded : ProbeStatus.Unavailable,
            dpc.HasValue && dpcPerCore.HasValue
                ? $"DPC {dpc.Value:N1}% all-core, {dpcPerCore.Value:N1}% worst core"
                : dpc.HasValue
                    ? $"DPC {dpc.Value:N1}% all-core, but the per-core maximum is missing, so a driver pinning one core would be averaged away"
                    : "no DPC or interrupt counters, so driver latency cannot be distinguished from software causes",
            signals.FirstOrDefault(signal => signal.Kind == "processor-dpc-percent-max-core")?.EvidenceState ?? "absent",
            TimeSpan.Zero));

        var host = Timed("host-context", () => platform.CaptureHostContext(), context => context.DisplayCount > 0
            ? (ProbeStatus.Live,
                $"{context.DockState}; {context.DisplayCount:N0} display(s), {context.ExternalDisplayCount:N0} external, {context.IndirectDisplayCount:N0} indirect/USB; bluetooth {(context.BluetoothRadioEnabled == true ? "on" : "off")}, {(context.ConnectedBluetoothDevices.HasValue ? $"{context.ConnectedBluetoothDevices.Value:N0} device(s) connected" : "device count unknown")}",
                context.EvidenceState)
            : (ProbeStatus.Unavailable, "no displays enumerated, so lag cannot be correlated with the dock", context.EvidenceState));
        probes.Add(host.Result);

        var shell = Timed("explorer-shell", () => platform.CaptureShellResponsiveness(), responsiveness => responsiveness.ShellWindowHung.HasValue
            ? (ProbeStatus.Live,
                responsiveness.ShellWindowHung.Value
                    ? $"Explorer is HUNG (pump latency {responsiveness.ShellPumpLatencyMilliseconds:N0} ms)"
                    : $"Explorer is responsive (pump latency {responsiveness.ShellPumpLatencyMilliseconds:N0} ms)",
                responsiveness.EvidenceState)
            : (ProbeStatus.Unavailable, "the shell could not be probed", responsiveness.EvidenceState));
        probes.Add(shell.Result);

        var events = Timed("event-log", () => platform.ReadRecentEventSummaries(DateTimeOffset.UtcNow.AddDays(-1)), summaries => summaries.Count > 0
            ? (ProbeStatus.Live, $"{summaries.Count:N0} recent event summar(ies) read", "read")
            : (ProbeStatus.Degraded, "no recent events, which can be normal on a healthy machine", "empty"));
        probes.Add(events.Result);

        var quality = EvidenceQualityAssessor.Assess(
            telemetry.Value,
            pressure.Value,
            events.Value,
            host.Value,
            shell.Value);

        return new SelfTestReport(probes, quality);
    }

    private static (ProbeResult Result, T Value) Timed<T>(
        string name,
        Func<T> capture,
        Func<T, (ProbeStatus Status, string Detail, string EvidenceState)> describe)
    {
        var stopwatch = Stopwatch.StartNew();
        var value = capture();
        stopwatch.Stop();

        var (status, detail, evidenceState) = describe(value);
        return (new ProbeResult(name, status, detail, evidenceState, stopwatch.Elapsed), value);
    }
}
