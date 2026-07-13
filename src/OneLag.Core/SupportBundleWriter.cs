using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

public sealed record SupportBundleOptions(
    string OutputDirectory,
    IReadOnlyList<string> ReportPaths,
    string? WatchDirectory = null,
    bool IncludeTracePlan = false,
    bool CreateZip = false,
    bool Overwrite = false,
    string? UserNote = null,
    string? OneDriveStatus = null);

public sealed record SupportBundleResult(
    string OutputDirectory,
    string? ZipPath,
    IReadOnlyList<string> Files);

public sealed record SupportBundleManifest(
    DateTimeOffset CreatedAt,
    string BundleFormat,
    string OneLagVersion,
    string Purpose,
    string PrivacyModel,
    IReadOnlyList<SupportBundleReportEntry> Reports,
    IReadOnlyList<SupportBundleFileEntry> Files);

public sealed record SupportBundleReportEntry(
    string SourcePath,
    string BundlePath,
    string SummaryPath,
    LocalReportKind Kind,
    string Title,
    IReadOnlyList<string> KeyFacts);

public sealed record SupportBundleFileEntry(
    string Path,
    long Bytes,
    string Sha256);

public sealed class SupportBundleWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IReportViewService reportViewService;
    private readonly Func<string, string>? versionProvider;

    public SupportBundleWriter(IReportViewService? reportViewService = null, Func<string, string>? versionProvider = null)
    {
        this.reportViewService = reportViewService ?? new LocalReportViewService();
        this.versionProvider = versionProvider;
    }

    public SupportBundleResult Write(SupportBundleOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException("Output directory is required.", nameof(options));
        }

        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        PrepareOutputDirectory(outputDirectory, options.Overwrite);

        var reportsDirectory = Path.Combine(outputDirectory, "reports");
        Directory.CreateDirectory(reportsDirectory);

        var reportEntries = new List<SupportBundleReportEntry>();
        var reportIndex = 1;

        foreach (var reportPath in options.ReportPaths)
        {
            CopyReport(reportPath, reportsDirectory, reportIndex++, reportEntries);
        }

        if (!string.IsNullOrWhiteSpace(options.WatchDirectory))
        {
            var generatedReport = Path.Combine(reportsDirectory, "watch-generated-report.md");
            new WatchService().WriteReport(options.WatchDirectory, generatedReport);
            CopyReport(generatedReport, reportsDirectory, reportIndex, reportEntries, sourceAlreadyInBundle: true);
        }

        if (reportEntries.Count == 0)
        {
            throw new ArgumentException("At least one --report path or --watch-output directory is required.");
        }

        if (options.IncludeTracePlan)
        {
            EscalationPlanWriter.WriteTracePlan(Path.Combine(outputDirectory, "trace-plan"));
        }

        WriteText(Path.Combine(outputDirectory, "README.md"), BuildReadme());
        WriteText(Path.Combine(outputDirectory, "ANALYZE_WITH_CODEX_OR_CLAUDE.md"), BuildAnalysisPrompt(reportEntries));
        WriteText(Path.Combine(outputDirectory, "privacy-checklist.md"), BuildPrivacyChecklist());
        WriteText(Path.Combine(outputDirectory, "user-notes.md"), BuildUserNotes(options.UserNote, options.OneDriveStatus));
        WriteText(Path.Combine(outputDirectory, "environment.md"), BuildEnvironment());

        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        var manifest = new SupportBundleManifest(
            DateTimeOffset.Now,
            "onelag-support-bundle-v1",
            versionProvider?.Invoke("unknown") ?? "unknown",
            "Offline review of OneLag diagnostic and responsiveness evidence in Codex, Claude Code, or another local analysis tool.",
            "Redacted reports and derived summaries only by default; no raw OneDrive logs, cache databases, ETL/PML traces, screenshots, document contents, keystrokes, clipboard data, or browser history.",
            reportEntries,
            Array.Empty<SupportBundleFileEntry>());
        WriteText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        var files = EnumerateBundleFiles(outputDirectory).ToArray();
        manifest = manifest with
        {
            Files = files
                .Select(path => new SupportBundleFileEntry(RelativePath(outputDirectory, path), new FileInfo(path).Length, Sha256(path)))
                .OrderBy(file => file.Path, StringComparer.Ordinal)
                .ToArray()
        };
        WriteText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));

        var finalFiles = EnumerateBundleFiles(outputDirectory)
            .Select(path => RelativePath(outputDirectory, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        string? zipPath = null;
        if (options.CreateZip)
        {
            zipPath = outputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
            if (File.Exists(zipPath))
            {
                if (!options.Overwrite)
                {
                    throw new IOException($"Zip file already exists: {zipPath}. Use --overwrite to replace it.");
                }

                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(outputDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }

        return new SupportBundleResult(outputDirectory, zipPath, finalFiles);
    }

    private void CopyReport(
        string reportPath,
        string reportsDirectory,
        int index,
        List<SupportBundleReportEntry> reportEntries,
        bool sourceAlreadyInBundle = false)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("Report path cannot be empty.");
        }

        var fullPath = Path.GetFullPath(reportPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Report file was not found.", fullPath);
        }

        var fileName = $"{index:000}-{SafeFileName(Path.GetFileName(fullPath))}";
        var destination = sourceAlreadyInBundle ? fullPath : Path.Combine(reportsDirectory, fileName);
        if (!sourceAlreadyInBundle)
        {
            File.Copy(fullPath, destination, overwrite: true);
        }

        var summary = reportViewService.Summarize(destination);
        var summaryPath = Path.Combine(reportsDirectory, Path.GetFileNameWithoutExtension(destination) + ".summary.md");
        WriteText(summaryPath, BuildReportSummary(summary));

        var root = Directory.GetParent(reportsDirectory)!.FullName;
        reportEntries.Add(new SupportBundleReportEntry(
            fullPath,
            RelativePath(root, destination),
            RelativePath(root, summaryPath),
            summary.Kind,
            summary.Title,
            summary.KeyFacts));
    }

    private static void PrepareOutputDirectory(string outputDirectory, bool overwrite)
    {
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            if (!overwrite)
            {
                throw new IOException($"Output directory is not empty: {outputDirectory}. Use --overwrite or choose a new directory.");
            }

            foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(outputDirectory);
    }

    private static string BuildReadme()
    {
        return """
        # OneLag Support Bundle

        This bundle is designed for offline review in Codex, Claude Code, or another local analysis tool on a different machine.

        Start with `ANALYZE_WITH_CODEX_OR_CLAUDE.md`, then inspect `manifest.json`, `reports/*.summary.md`, and the copied report files.

        The bundle is redacted by default and intentionally excludes raw OneDrive logs, OneDrive DAT/cache/database files, ETL/PML traces, screenshots, document contents, keystrokes, clipboard data, and browser history.
        """;
    }

    private static string BuildAnalysisPrompt(IReadOnlyList<SupportBundleReportEntry> reports)
    {
        var reportList = string.Join(Environment.NewLine, reports.Select(report => $"- `{report.BundlePath}` ({report.Kind}) with summary `{report.SummaryPath}`"));
        return $"""
        # Prompt For Codex or Claude Code

        You are analyzing a local OneLag support bundle for a Windows 11 responsiveness, keyboard/mouse lag, Explorer freeze, or OneDrive sync-pressure investigation.

        Use only the files in this bundle unless the user gives you more evidence. Start with:

        {reportList}

        OneDrive is one hypothesis among several, not the default. The reports rank ten candidate causes:
        OneDrive sync, driver interrupt/DPC latency, the display and dock pipeline, the Bluetooth and input
        radio, storage saturation, CPU contention, memory paging, Explorer shell blocking, Defender/Search
        scanners, and thermal or power throttling. Do not assume OneDrive.

        Required analysis format:

        1. State the evidence quality first. If the capture is graded Insufficient, say that no diagnosis is
           possible from it and stop ranking causes as though they were established.
        2. Summarize the strongest observed evidence, separating direct observations from inferences.
        3. Rank the likely causes with confidence, explaining what evidence supports AND what weakens each.
        4. Check whether OneDrive has any live evidence at all (process CPU, log churn, disk saturation while
           active). Static folder shape — item count, high-churn directories, sync blockers — describes
           exposure, not an active cause, and on its own cannot implicate OneDrive in a freeze.
        5. Check whether DPC or interrupt time is elevated, especially the per-core maximum. That is kernel
           driver work and cannot be produced by OneDrive's user-mode sync engine.
        6. If a watch report is present, check the configuration correlation: does lag concentrate in one
           hardware configuration (docked, external displays, indirect/USB displays, Bluetooth connected)?
           Lag that tracks the configuration rather than the sync load is a driver problem.
        7. If a driver-interrupt trace is present, name the driver and the subsystem it belongs to.
        8. Identify the safest next diagnostic step.
        9. Identify any remediation that should not be run automatically.
        10. List any missing evidence that would materially change the conclusion.

        Safety rules:

        - Do not recommend deleting files, disabling Defender, disabling Windows Search, clearing Event Viewer, or editing OneDrive cache/database files as a first step.
        - Treat WPR, WPA, ProcMon, ETL, and PML files as privacy-sensitive escalation artifacts.
        - Prefer reversible, Microsoft-supported actions and reviewed dry-run plans.
        - If the evidence is inconclusive, say so directly.
        - Do not blame OneDrive because the bundle came from a tool named OneLag.
        """;
    }

    private static string BuildPrivacyChecklist()
    {
        return """
        # Privacy Checklist

        Review this bundle before sharing it outside your machine.

        Included by default:

        - Redacted OneLag diagnostic or watch reports.
        - Report summaries generated from those reports.
        - A manifest with file hashes and non-identifying runtime context.
        - Optional generated WPR/WPA and ProcMon runbooks if requested.

        Not included by default:

        - Raw OneDrive logs.
        - OneDrive DAT/cache/database files.
        - Raw Event Viewer exports.
        - WPR ETL traces or ProcMon PML captures.
        - Screenshots.
        - Document contents, keystrokes, clipboard data, browser history, or meeting content.
        """;
    }

    private static string BuildUserNotes(string? note, string? oneDriveStatus)
    {
        return $"""
        # User Notes

        Symptom note:

        {ValueOrPlaceholder(note)}

        Observed OneDrive tray/status text:

        {ValueOrPlaceholder(oneDriveStatus)}

        Fill in before analysis if known:

        - Local time when lag/freeze happened:
        - App in foreground:
        - Whether typing, clicking, scrolling, or window switching was affected:
        - Whether OneDrive was syncing, processing changes, paused, or showing an error:
        - Whether the machine was on battery, docked, or under thermal/fan load:
        """;
    }

    private static string BuildEnvironment()
    {
        return $"""
        # Environment Snapshot

        - Generated at: `{DateTimeOffset.Now:O}`
        - OS description: `{System.Runtime.InteropServices.RuntimeInformation.OSDescription}`
        - OS architecture: `{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}`
        - Process architecture: `{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}`
        - Framework: `{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}`
        - Is Windows: `{OperatingSystem.IsWindows()}`
        """;
    }

    private static string BuildReportSummary(LocalReportSummary summary)
    {
        var lines = new List<string>
        {
            $"# {summary.Title} Summary",
            "",
            $"- Source: `{summary.SourcePath}`",
            $"- Kind: `{summary.Kind}`"
        };

        if (summary.KeyFacts.Count > 0)
        {
            lines.Add("");
            lines.Add("## Key Facts");
            lines.AddRange(summary.KeyFacts.Select(fact => $"- {fact}"));
        }

        if (summary.Timeline.Count > 0)
        {
            lines.Add("");
            lines.Add("## Timeline");
            foreach (var item in summary.Timeline.Take(50))
            {
                var timestamp = item.Timestamp?.ToString("O") ?? "not timestamped";
                lines.Add($"- {timestamp} [{item.Kind}] {item.Summary}: {item.Evidence}");
            }

            if (summary.Timeline.Count > 50)
            {
                lines.Add($"- {summary.Timeline.Count - 50:N0} more item(s) omitted from this summary; inspect the original report.");
            }
        }

        if (summary.NextActions.Count > 0)
        {
            lines.Add("");
            lines.Add("## Next Actions From Report");
            lines.AddRange(summary.NextActions.Select(action => $"- {action}"));
        }

        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var safe = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "report.md" : safe;
    }

    private static string ValueOrPlaceholder(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "_Not provided._" : value;
    }

    private static void WriteText(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        File.WriteAllText(path, contents.ReplaceLineEndings(Environment.NewLine));
    }

    private static IEnumerable<string> EnumerateBundleFiles(string outputDirectory)
    {
        return Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories);
    }

    private static string RelativePath(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
