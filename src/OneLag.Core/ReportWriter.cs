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
        builder.AppendLine();

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
                builder.AppendLine($"- `{process.Name}` PID `{process.ProcessId}` working set `{process.WorkingSetBytes:N0}` bytes CPU time `{process.TotalProcessorTime}`");
            }
        }

        return builder.ToString();
    }
}
