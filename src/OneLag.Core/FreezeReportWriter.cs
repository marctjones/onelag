using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

/// <summary>
/// Renders a freeze capture.
///
/// The report leads with what was measured *during* the episode, because that is the whole point of the
/// command and the thing every previous capture lacked. Where a signal could not be collected it says so
/// explicitly rather than omitting it — an absent collector and a negative finding look identical on a page,
/// and confusing the two is how this tool previously produced authoritative-looking reports about nothing.
/// </summary>
public static class FreezeReportWriter
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(FreezeCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return JsonSerializer.Serialize(capture, JsonOptions);
    }

    public static string ToMarkdown(FreezeCapture capture, Redactor redactor)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(redactor);

        var builder = new StringBuilder();
        builder.AppendLine("# OneLag Freeze Capture");
        builder.AppendLine();
        builder.AppendLine("This capture was taken while the machine was reported to be lagging. Unlike a scan, it measures");
        builder.AppendLine("the episode rather than the folder shape, and it does not walk any directory tree.");
        builder.AppendLine();
        builder.AppendLine($"- Started: `{capture.StartedAt:O}`");
        builder.AppendLine($"- Finished: `{capture.FinishedAt:O}`");
        if (!string.IsNullOrWhiteSpace(capture.Note))
        {
            builder.AppendLine($"- Note: {capture.Note}");
        }

        builder.AppendLine();

        AppendEvidenceQuality(builder, capture.EvidenceQuality);
        AppendMemory(builder, capture.Memory);
        AppendRankedCauses(builder, capture.Hypotheses);
        AppendFilterStack(builder, capture.FilterStack);
        AppendFileSystem(builder, capture.FileSystem, redactor);
        AppendShell(builder, capture.Shell);
        ReportWriter.AppendDriverLatency(builder, capture.DriverLatency);
        AppendFindings(builder, capture.Findings);

        return builder.ToString();
    }

    private static void AppendEvidenceQuality(StringBuilder builder, EvidenceQuality quality)
    {
        builder.AppendLine("## Evidence Quality");
        builder.AppendLine();
        builder.AppendLine($"**{quality.Grade} ({quality.Score}/100).** {quality.Summary}");
        builder.AppendLine();

        if (quality.Gaps.Count > 0)
        {
            builder.AppendLine("Gaps in this capture:");
            foreach (var gap in quality.Gaps)
            {
                builder.AppendLine($"- {gap}");
            }

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Memory leads the report. It is the section that answers the question a frozen user actually has —
    /// "why did my clicks queue up and then replay all at once" — and the unaccounted-commit figure is the one
    /// number here that Task Manager cannot show you at all.
    /// </summary>
    private static void AppendMemory(StringBuilder builder, MemoryPressureDetail memory)
    {
        builder.AppendLine("## Memory");
        builder.AppendLine();

        if (memory.CommitTotalBytes is null)
        {
            builder.AppendLine($"Memory could not be measured: `{memory.EvidenceState}`.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"- Committed: `{Bytes(memory.CommitTotalBytes)}` of `{Bytes(memory.CommitLimitBytes)}` ({Percent(memory.CommitPercent)})");
        builder.AppendLine($"- Commit headroom: `{Bytes(memory.CommitHeadroomBytes)}`");
        builder.AppendLine($"- Physical available: `{Bytes(memory.PhysicalAvailableBytes)}` of `{Bytes(memory.PhysicalTotalBytes)}` ({Percent(memory.PhysicalAvailablePercent)})");
        builder.AppendLine($"- Kernel pool: paged `{Bytes(memory.KernelPagedPoolBytes)}`, non-paged `{Bytes(memory.KernelNonPagedPoolBytes)}`");

        if (memory.SystemUptime is { } uptime)
        {
            builder.AppendLine($"- System uptime: `{uptime.TotalDays:N1} days`");
        }

        builder.AppendLine($"- Evidence: `{memory.EvidenceState}`");
        builder.AppendLine();

        if (memory.UnaccountedCommitBytes is { } unaccounted && unaccounted > 0)
        {
            builder.AppendLine($"### Unaccounted commit: {Bytes(unaccounted)}");
            builder.AppendLine();
            builder.AppendLine($"The sum of every readable process's private bytes is `{Bytes(memory.SumOfProcessPrivateBytes)}`, against `{Bytes(memory.CommitTotalBytes)}` committed.");
            builder.AppendLine();

            if (memory.ProcessesInaccessible is > 0)
            {
                builder.AppendLine($"**Read this with caution.** {memory.ProcessesInaccessible:N0} of {memory.ProcessesSampled:N0} processes could not be read, so some of this may belong to a process rather than to the kernel. Re-run from an elevated terminal for a reliable figure.");
            }
            else
            {
                builder.AppendLine("**No user-mode process holds this memory, so closing applications will not return it.** That points at the kernel — a driver holding pool — rather than at an application. Task Manager cannot show you this: its Details tab lists only user-mode processes, so driver-held memory appears nowhere while still consuming commit.");
                builder.AppendLine();
                builder.AppendLine("Next step: run `poolmon` or Sysinternals RAMMap (Use Counts tab) to name the pool tag and the driver holding it.");
            }

            builder.AppendLine();
        }

        if (memory.LeakCandidates.Count > 0)
        {
            builder.AppendLine("### Leak candidates named by Windows");
            builder.AppendLine();
            builder.AppendLine("These are Windows' own leak detectors, not OneLag heuristics.");
            builder.AppendLine();
            foreach (var leak in memory.LeakCandidates)
            {
                var leakUptime = leak.ProcessUptime is null ? string.Empty : $", up {leak.ProcessUptime.Value.TotalDays:N1} days";
                builder.AppendLine($"- `{leak.ProcessName}`{leakUptime} — flagged by `{leak.Source}` at `{leak.ObservedAt:O}`");
            }

            builder.AppendLine();
        }

        if (memory.TopCommitProcesses.Count > 0)
        {
            builder.AppendLine("### Top processes by commit (private bytes)");
            builder.AppendLine();
            builder.AppendLine("| Process | PID | Private bytes | Working set |");
            builder.AppendLine("| --- | --- | --- | --- |");
            foreach (var process in memory.TopCommitProcesses)
            {
                builder.AppendLine($"| `{process.Name}` | {process.ProcessId} | {Bytes(process.PrivateBytes)} | {Bytes(process.WorkingSetBytes)} |");
            }

            builder.AppendLine();
        }
    }

    private static void AppendRankedCauses(StringBuilder builder, IReadOnlyList<Hypothesis> hypotheses)
    {
        if (hypotheses.Count == 0)
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

        foreach (var hypothesis in hypotheses.Where(h => h.Score > 0 || h.Supporting.Count > 0))
        {
            builder.AppendLine($"### {hypothesis.Kind} - {hypothesis.Verdict}");
            builder.AppendLine();
            builder.AppendLine(hypothesis.Summary);
            builder.AppendLine();

            if (hypothesis.Supporting.Count > 0)
            {
                builder.AppendLine("Evidence for:");
                foreach (var evidence in hypothesis.Supporting)
                {
                    builder.AppendLine($"- {evidence}");
                }

                builder.AppendLine();
            }

            if (hypothesis.Opposing.Count > 0)
            {
                builder.AppendLine("Evidence against:");
                foreach (var evidence in hypothesis.Opposing)
                {
                    builder.AppendLine($"- {evidence}");
                }

                builder.AppendLine();
            }

            builder.AppendLine($"Next step: {hypothesis.NextStep}");
            builder.AppendLine();
        }
    }

    private static void AppendFilterStack(StringBuilder builder, FilterDriverStack stack)
    {
        builder.AppendLine("## File-System Filter Stack");
        builder.AppendLine();

        if (stack.Filters.Count == 0)
        {
            builder.AppendLine($"The filter stack could not be enumerated: `{stack.EvidenceState}`.");
            builder.AppendLine();
            builder.AppendLine("This is not the same as finding no filters. `fltmc` needs an elevated terminal — re-run from an administrator prompt to test this hypothesis at all.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine($"- Filters attached: `{stack.FileSystemFilterCount}` total, `{stack.ThirdPartyFileSystemFilterCount}` third-party");
        builder.AppendLine($"- Security vendors in the file I/O path: `{(stack.SecurityVendors.Count > 0 ? string.Join(", ", stack.SecurityVendors) : "none identified")}`");
        builder.AppendLine($"- Defender real-time filter running: `{Bool(stack.DefenderFilterRunning)}`");
        builder.AppendLine($"- OneDrive Cloud Files filter running: `{Bool(stack.CloudFilesFilterRunning)}`");
        builder.AppendLine($"- Evidence: `{stack.EvidenceState}`");
        builder.AppendLine();
        builder.AppendLine("Every file open traverses every attached filter synchronously. An open-file dialog performs one");
        builder.AppendLine("open per file just to draw its icon, so this cost lands on Explorer and file dialogs first.");
        builder.AppendLine();

        builder.AppendLine("| Filter | Altitude | Instances | Vendor |");
        builder.AppendLine("| --- | --- | --- | --- |");
        foreach (var filter in stack.Filters.OrderBy(f => f.Altitude ?? double.MaxValue))
        {
            var vendor = filter.IsMicrosoft ? "Microsoft" : filter.Vendor ?? "third-party";
            builder.AppendLine($"| `{filter.Name}` | {filter.Altitude?.ToString("N0") ?? "unknown"} | {filter.Instances} | {vendor} |");
        }

        builder.AppendLine();
    }

    private static void AppendFileSystem(StringBuilder builder, FileSystemContext fileSystem, Redactor redactor)
    {
        builder.AppendLine("## File Namespace");
        builder.AppendLine();

        if (fileSystem.KnownFolders.Count == 0 && fileSystem.MappedDrives.Count == 0)
        {
            builder.AppendLine($"Not measured: `{fileSystem.EvidenceState}`.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("Every native open-file dialog defaults to Documents. Where Documents actually lives decides how");
        builder.AppendLine("expensive every dialog is.");
        builder.AppendLine();

        foreach (var folder in fileSystem.KnownFolders)
        {
            var flag = folder.RedirectedIntoCloudRoot ? " **(redirected into a cloud-synced root)**" : string.Empty;
            builder.AppendLine($"- `{folder.KnownFolder}`: `{redactor.PathValue(folder.Path)}`{flag}");
        }

        builder.AppendLine($"- Dehydrated placeholders found: `{fileSystem.DehydratedPlaceholderCount:N0}` (each one blocks on a network round-trip when opened)");

        foreach (var drive in fileSystem.MappedDrives)
        {
            var reachable = drive.Reachable switch
            {
                true => "reachable",
                false => "**NOT REACHABLE**",
                null => "**did not answer within the timeout**"
            };
            builder.AppendLine($"- Mapped drive `{drive.Letter}` -> `{redactor.PathValue(drive.RemotePath)}`: `{drive.Status}`, {reachable}");
        }

        builder.AppendLine($"- Evidence: `{fileSystem.EvidenceState}`");
        builder.AppendLine();
    }

    private static void AppendShell(StringBuilder builder, ShellResponsiveness shell)
    {
        builder.AppendLine("## Explorer Shell");
        builder.AppendLine();
        builder.AppendLine($"- Explorer running: `{Bool(shell.ExplorerRunning)}`");
        builder.AppendLine($"- Shell window hung: `{Bool(shell.ShellWindowHung)}`");
        builder.AppendLine($"- Shell message-pump latency: `{(shell.ShellPumpLatencyMilliseconds is { } ms ? $"{ms:N0} ms" : "unknown")}`");
        builder.AppendLine($"- Hung top-level windows: `{shell.HungTopLevelWindows}`");
        builder.AppendLine($"- Evidence: `{shell.EvidenceState}`");
        builder.AppendLine();
    }

    private static void AppendFindings(StringBuilder builder, IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Findings");
        builder.AppendLine();
        foreach (var finding in findings)
        {
            builder.AppendLine($"- **{finding.Severity}**: {finding.Title}. {finding.Evidence} Confidence: `{finding.Confidence}`.");
        }

        builder.AppendLine();
    }

    private static string Bytes(long? bytes)
    {
        if (bytes is null)
        {
            return "unknown";
        }

        var value = bytes.Value;
        return Math.Abs(value) >= GB
            ? $"{value / (double)GB:N1} GB"
            : $"{value / (double)MB:N0} MB";
    }

    private static string Percent(double? value) => value is null ? "unknown" : $"{value.Value:N1}%";

    private static string Bool(bool? value) => value switch
    {
        true => "yes",
        false => "no",
        null => "unknown"
    };
}
