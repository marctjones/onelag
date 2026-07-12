namespace OneLag.Core;

/// <summary>
/// Grades how much of the evidence a verdict actually rests on.
///
/// A scan that runs while OneDrive is not even running, with no performance counters and no live
/// telemetry, can still emit a full-looking report. Reports like that are worse than no report: they read
/// as authoritative while containing nothing. The grade is surfaced at the top of every report and caps
/// how strong any hypothesis is allowed to get.
/// </summary>
public static class EvidenceQualityAssessor
{
    public static EvidenceQuality Assess(
        TelemetrySnapshot telemetry,
        SystemPressureSnapshot pressure,
        IReadOnlyList<EventLogSummary> eventLogs,
        HostContext? hostContext,
        ShellResponsiveness? shell)
    {
        var gaps = new List<string>();
        var score = 0;

        var oneDriveRunning = telemetry.OneDriveProcesses.Count > 0;
        if (oneDriveRunning)
        {
            score += 25;
        }
        else
        {
            gaps.Add("OneDrive was not running at capture time, so no live OneDrive CPU, memory, or log-churn evidence exists. The OneDrive hypothesis cannot be tested from this capture.");
        }

        var signals = pressure.Signals ?? Array.Empty<PerformanceSignal>();
        var liveSignals = signals.Count(signal => signal.Value.HasValue);
        if (liveSignals >= 6)
        {
            score += 25;
        }
        else if (liveSignals > 0)
        {
            score += 12;
            gaps.Add($"Only {liveSignals} performance counter(s) returned a value; whole-system pressure evidence is partial.");
        }
        else
        {
            gaps.Add("No Windows performance counters returned a value, so CPU, disk, memory, and interrupt pressure are all unmeasured.");
        }

        var hasInterruptSignals = PressureClassifier.Value(signals, "processor-dpc-percent").HasValue
            || PressureClassifier.Value(signals, "processor-dpc-percent-max-core").HasValue;
        if (hasInterruptSignals)
        {
            score += 15;
        }
        else
        {
            gaps.Add("No DPC or interrupt-time counters were available, so driver-latency causes (dock, GPU, Bluetooth, storage controller) cannot be distinguished from software causes.");
        }

        if (hostContext is not null && hostContext.DisplayCount > 0)
        {
            score += 15;
        }
        else
        {
            gaps.Add("No host context (displays, dock state, Bluetooth, power source) was captured, so lag cannot be correlated with docked or undocked operation.");
        }

        if (shell is not null && shell.ShellWindowHung.HasValue)
        {
            score += 10;
        }
        else
        {
            gaps.Add("Explorer shell responsiveness was not measured, so shell-extension blocking was inferred rather than tested.");
        }

        if (eventLogs.Count > 0)
        {
            score += 10;
        }
        else
        {
            gaps.Add("No Windows event summaries were available to corroborate or weaken any hypothesis.");
        }

        var grade = score switch
        {
            >= 70 => EvidenceGrade.Complete,
            >= 35 => EvidenceGrade.Partial,
            _ => EvidenceGrade.Insufficient
        };

        var summary = grade switch
        {
            EvidenceGrade.Complete => "Live telemetry, system pressure, interrupt, host-context, and shell evidence were all collected. Verdicts below rest on measured evidence.",
            EvidenceGrade.Partial => "Some live evidence was collected, but there are gaps. Treat verdicts below as provisional and re-run during an actual lag episode.",
            _ => "This capture contains almost no live evidence. Nothing below should be treated as a diagnosis. Run `onelag watch start` and reproduce the lag, or re-run `onelag scan` while the machine is actually slow."
        };

        return new EvidenceQuality(grade, Math.Clamp(score, 0, 100), gaps, summary);
    }
}
