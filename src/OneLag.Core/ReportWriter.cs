using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

public static class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(DiagnosticReport report)
    {
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public static string ToMarkdown(DiagnosticReport report, Redactor redactor)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# OneLag Diagnostic Report");
        builder.AppendLine();
        builder.AppendLine($"- Started: `{report.StartedAt:O}`");
        builder.AppendLine($"- Finished: `{report.FinishedAt:O}`");
        builder.AppendLine($"- Diagnosis: `{report.Diagnosis}`");
        builder.AppendLine($"- Telemetry: `{report.Telemetry.EvidenceState}`");
        builder.AppendLine($"- System pressure: `{report.SystemPressure.EvidenceState}`");
        builder.AppendLine($"- CPU: `{report.SystemPressure.CpuState}`");
        builder.AppendLine($"- Memory: `{report.SystemPressure.MemoryState}`");
        builder.AppendLine($"- Disk: `{report.SystemPressure.DiskState}`");
        builder.AppendLine($"- Power: `{report.SystemPressure.PowerState}`");
        builder.AppendLine($"- OneDrive client health: `{report.OneDriveClientHealth.EvidenceState}`");
        builder.AppendLine();

        AppendEvidenceQuality(builder, report.EvidenceQuality);
        AppendHypotheses(builder, report.Hypotheses);
        AppendHostContext(builder, report.HostContext, redactor);
        AppendShellResponsiveness(builder, report.ShellResponsiveness);

        builder.AppendLine("## Roots");
        foreach (var root in report.Roots)
        {
            builder.AppendLine($"- `{redactor.PathValue(root.Path)}` ({root.Source}, {root.Confidence}, {root.AccountKind ?? "unknown"})");
        }

        builder.AppendLine();
        builder.AppendLine("## Inventory");
        foreach (var inventory in report.Inventories)
        {
            builder.AppendLine($"### `{redactor.PathValue(inventory.Root)}`");
            builder.AppendLine();
            builder.AppendLine($"- Files: `{inventory.FileCount:N0}`");
            builder.AppendLine($"- Directories: `{inventory.DirectoryCount:N0}`");
            builder.AppendLine($"- Total bytes: `{inventory.TotalBytes:N0}`");
            builder.AppendLine($"- Max depth: `{inventory.MaxDepth:N0}`");
            builder.AppendLine($"- Capped: `{inventory.WasCapped}`");
            builder.AppendLine($"- Inaccessible paths: `{inventory.InaccessiblePaths.Count:N0}`");
            builder.AppendLine($"- High-risk directories: `{inventory.HighRiskDirectories.Count:N0}`");
            builder.AppendLine($"- Sync blockers: `{inventory.SyncBlockers.Count:N0}`");
            builder.AppendLine();

            foreach (var topLevel in inventory.TopLevelItems.Take(12))
            {
                builder.AppendLine($"- Top-level item: `{redactor.PathValue(topLevel.Path)}` files `{topLevel.FileCount:N0}`, directories `{topLevel.DirectoryCount:N0}`, bytes `{topLevel.TotalBytes:N0}`");
            }

            foreach (var group in inventory.SyncBlockers.GroupBy(blocker => blocker.Kind).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Take(12))
            {
                builder.AppendLine($"- Known issue `{group.Key}`: `{group.Count():N0}` item(s)");
            }

            foreach (var risk in inventory.HighRiskDirectories.Take(20))
            {
                builder.AppendLine($"- High-risk directory: `{redactor.PathValue(risk.Path)}` - {risk.Reason}");
            }

            foreach (var blocker in inventory.SyncBlockers.Take(20))
            {
                builder.AppendLine($"- {blocker.Severity} blocker `{blocker.Kind}`: `{redactor.PathValue(blocker.Path)}` - {blocker.Evidence}");
            }

            builder.AppendLine();
        }

        builder.AppendLine("## OneDrive Client Health");
        builder.AppendLine($"- Internal sync database parsed: `{report.OneDriveClientHealth.InternalSyncDatabaseParsed}`");
        builder.AppendLine($"- Reset command candidates: `{report.OneDriveClientHealth.ResetCommands.Count:N0}`");
        foreach (var signal in report.OneDriveClientHealth.Signals)
        {
            builder.AppendLine($"- **{signal.Severity}** `{signal.Kind}`: {signal.Evidence} Safety: {signal.Safety}");
        }

        foreach (var command in report.OneDriveClientHealth.ResetCommands)
        {
            builder.AppendLine($"- Reset candidate `{command.Source}`: `{redactor.PathValue(command.ExecutablePath)} {command.Arguments}`");
        }

        builder.AppendLine();
        builder.AppendLine("## System Performance");
        var performanceSignals = report.SystemPressure.Signals ?? Array.Empty<PerformanceSignal>();
        if (performanceSignals.Count == 0)
        {
            builder.AppendLine("- No structured Windows performance counter signals were available.");
        }
        else
        {
            foreach (var signal in performanceSignals)
            {
                builder.AppendLine($"- `{signal.Kind}`: `{FormatSignalValue(signal)}` `{signal.Unit}` ({signal.EvidenceState})");
            }
        }

        var processSamples = report.SystemPressure.TopProcessSamples ?? Array.Empty<ProcessPressureSample>();
        if (processSamples.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Top sampled processes:");
            foreach (var process in processSamples.Take(10))
            {
                builder.AppendLine($"- `{process.Name}` PID `{process.ProcessId}` CPU `{process.CpuPercent:N1}%` working set `{process.WorkingSetBytes:N0}` bytes");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Recent Windows Events");
        if (report.EventLogs.Count == 0)
        {
            builder.AppendLine("- No recent Windows critical, error, or warning event summaries were available.");
        }
        else
        {
            foreach (var summary in report.EventLogs)
            {
                builder.AppendLine($"- `{summary.LogName}` `{summary.Provider}` event `{summary.EventId}` `{summary.Level}` count `{summary.Count:N0}` newest `{summary.NewestTimestamp?.ToString("O") ?? "unknown"}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Findings");
        foreach (var finding in report.Findings)
        {
            builder.AppendLine($"- **{finding.Severity}**: {finding.Title}. {finding.Evidence} Confidence: `{finding.Confidence}`.");
        }

        builder.AppendLine();
        builder.AppendLine("## Recommendations");
        foreach (var recommendation in report.Recommendations)
        {
            builder.AppendLine($"- **{recommendation.Title}** (`{recommendation.Kind}`): {recommendation.Rationale} Safety: {recommendation.Safety}");
        }

        builder.AppendLine();
        builder.AppendLine("## OneDrive Processes");
        if (report.Telemetry.OneDriveProcesses.Count == 0)
        {
            builder.AppendLine("- No OneDrive process telemetry was available.");
        }
        else
        {
            foreach (var process in report.Telemetry.OneDriveProcesses)
            {
                builder.AppendLine($"- `{process.Name}` PID `{process.ProcessId}` working set `{process.WorkingSetBytes:N0}` bytes CPU time `{process.TotalProcessorTime}` sampled CPU `{FormatOptionalPercent(process.CpuPercent)}`");
            }
        }

        return builder.ToString();
    }

    private static void AppendEvidenceQuality(StringBuilder builder, EvidenceQuality? quality)
    {
        if (quality is null)
        {
            return;
        }

        builder.AppendLine("## Evidence Quality");
        builder.AppendLine();
        builder.AppendLine($"**{quality.Grade} ({quality.Score}/100).** {quality.Summary}");

        if (quality.Gaps.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Gaps in this capture:");
            foreach (var gap in quality.Gaps)
            {
                builder.AppendLine($"- {gap}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendHypotheses(StringBuilder builder, IReadOnlyList<Hypothesis>? hypotheses)
    {
        if (hypotheses is null || hypotheses.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Ranked Causes");
        builder.AppendLine();
        builder.AppendLine("| Cause | Verdict | Score |");
        builder.AppendLine("| --- | --- | --- |");
        foreach (var hypothesis in hypotheses)
        {
            builder.AppendLine($"| {hypothesis.Kind} | {hypothesis.Verdict} | {hypothesis.Score} |");
        }

        builder.AppendLine();

        // OneDrive always gets a written-out section even when it is rejected. It is the hypothesis the user
        // came in believing, so the reasons against it are the most important thing on the page.
        foreach (var hypothesis in hypotheses.Where(candidate =>
            candidate.Verdict is not HypothesisVerdict.NotSupported
            || candidate.Kind == HypothesisKind.OneDriveSync))
        {
            builder.AppendLine($"### {hypothesis.Kind} - {hypothesis.Verdict}");
            builder.AppendLine();
            builder.AppendLine(hypothesis.Summary);
            builder.AppendLine();

            if (hypothesis.Supporting.Count > 0)
            {
                builder.AppendLine("Evidence for:");
                foreach (var item in hypothesis.Supporting)
                {
                    builder.AppendLine($"- {item}");
                }

                builder.AppendLine();
            }

            if (hypothesis.Opposing.Count > 0)
            {
                builder.AppendLine("Evidence against:");
                foreach (var item in hypothesis.Opposing)
                {
                    builder.AppendLine($"- {item}");
                }

                builder.AppendLine();
            }

            builder.AppendLine($"Next step: {hypothesis.NextStep}");
            builder.AppendLine();
        }
    }

    private static void AppendHostContext(StringBuilder builder, HostContext? host, Redactor redactor)
    {
        if (host is null)
        {
            return;
        }

        builder.AppendLine("## Host Context");
        builder.AppendLine();
        builder.AppendLine($"- Dock state: `{host.DockState}`");
        builder.AppendLine($"- Displays: `{host.DisplayCount:N0}` total, `{host.ExternalDisplayCount:N0}` external, `{host.IndirectDisplayCount:N0}` indirect/USB");
        builder.AppendLine($"- Bluetooth radio: present `{FormatBool(host.BluetoothRadioPresent)}`, enabled `{FormatBool(host.BluetoothRadioEnabled)}`, connected devices `{FormatCount(host.ConnectedBluetoothDevices)}`");
        builder.AppendLine($"- Power: `{host.PowerSource}`");
        builder.AppendLine($"- Wired network up: `{FormatBool(host.WiredNetworkUp)}`");
        builder.AppendLine($"- Evidence: `{host.EvidenceState}`");

        foreach (var display in host.Displays)
        {
            var kind = display.IsInternal ? "internal" : display.IsIndirect ? "indirect/USB" : "external";
            builder.AppendLine($"- Display `{redactor.PathValue(display.Name)}`: {kind}, `{display.OutputTechnology}`, `{display.Width}x{display.Height}` at `{display.RefreshHz:N0} Hz`");
        }

        foreach (var driver in host.IndirectDisplayDrivers)
        {
            builder.AppendLine($"- Indirect display software running: `{driver}`");
        }

        builder.AppendLine();
    }

    private static void AppendShellResponsiveness(StringBuilder builder, ShellResponsiveness? shell)
    {
        if (shell is null)
        {
            return;
        }

        builder.AppendLine("## Explorer Shell Responsiveness");
        builder.AppendLine();
        builder.AppendLine($"- Explorer running: `{FormatBool(shell.ExplorerRunning)}`");
        builder.AppendLine($"- Shell window hung: `{FormatBool(shell.ShellWindowHung)}`");
        builder.AppendLine($"- Shell message-pump latency: `{(shell.ShellPumpLatencyMilliseconds.HasValue ? $"{shell.ShellPumpLatencyMilliseconds.Value:N0} ms" : "unknown")}`");
        builder.AppendLine($"- Hung top-level windows: `{shell.HungTopLevelWindows:N0}`");
        builder.AppendLine($"- Evidence: `{shell.EvidenceState}`");
        builder.AppendLine();
    }

    private static string FormatBool(bool? value)
    {
        return value.HasValue ? value.Value.ToString() : "unknown";
    }

    private static string FormatCount(int? value)
    {
        return value.HasValue ? value.Value.ToString("N0") : "unknown";
    }

    private static string FormatSignalValue(PerformanceSignal signal)
    {
        return signal.Value.HasValue ? signal.Value.Value.ToString("N1") : "unknown";
    }

    private static string FormatOptionalPercent(double? value)
    {
        return value.HasValue ? $"{value.Value:N1}%" : "unknown";
    }
}
