using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

public enum LocalReportKind
{
    Diagnostic,
    Watch,
    Unknown
}

public sealed record LocalReportSummary(
    string SourcePath,
    LocalReportKind Kind,
    string Title,
    IReadOnlyList<string> KeyFacts,
    IReadOnlyList<ReportTimelineItem> Timeline,
    IReadOnlyList<string> NextActions);

public sealed record ReportTimelineItem(
    DateTimeOffset? Timestamp,
    string Kind,
    string Summary,
    string Evidence);

public interface IReportViewService
{
    LocalReportSummary Summarize(string reportPath);
}

public sealed class LocalReportViewService : IReportViewService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    public LocalReportSummary Summarize(string reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            throw new ArgumentException("Report path is required.", nameof(reportPath));
        }

        var fullPath = Path.GetFullPath(reportPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Report file was not found.", fullPath);
        }

        var text = File.ReadAllText(fullPath);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            var summary = TrySummarizeDiagnosticJson(fullPath, trimmed);
            if (summary is not null)
            {
                return summary;
            }
        }

        if (text.Contains("# OneLag Watch Report", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeWatchMarkdown(fullPath, text);
        }

        if (text.Contains("# OneLag Diagnostic Report", StringComparison.OrdinalIgnoreCase))
        {
            return SummarizeDiagnosticMarkdown(fullPath, text);
        }

        return SummarizeUnknown(fullPath, text);
    }

    private static LocalReportSummary? TrySummarizeDiagnosticJson(string path, string text)
    {
        try
        {
            var report = JsonSerializer.Deserialize<DiagnosticReport>(text, JsonOptions);
            if (report is null)
            {
                return null;
            }

            var totalFiles = report.Inventories.Sum(inventory => inventory.FileCount);
            var totalDirectories = report.Inventories.Sum(inventory => inventory.DirectoryCount);
            var highRiskDirectories = report.Inventories.Sum(inventory => inventory.HighRiskDirectories.Count);
            var syncBlockers = report.Inventories.Sum(inventory => inventory.SyncBlockers.Count);
            var keyFacts = new List<string>();

            // Evidence quality and the ranked cause come first: they are what tells the reader whether
            // anything else on the list is worth believing.
            if (report.EvidenceQuality is { } quality)
            {
                keyFacts.Add($"Evidence quality: {quality.Grade} ({quality.Score}/100)");
            }

            var rankedCauses = report.Hypotheses ?? Array.Empty<Hypothesis>();
            foreach (var hypothesis in rankedCauses
                .Where(candidate => candidate.Verdict is HypothesisVerdict.Possible or HypothesisVerdict.Likely or HypothesisVerdict.StronglySupported)
                .Take(3))
            {
                keyFacts.Add($"Cause: {hypothesis.Kind} ({hypothesis.Verdict}, score {hypothesis.Score})");
            }

            var namedDrivers = DriverClassifier.Significant(report.DriverLatency);
            foreach (var driver in namedDrivers.Take(3))
            {
                keyFacts.Add($"Driver at high IRQL: {driver.Driver} ({driver.Kind}, {driver.TotalMilliseconds:N1} ms)");
            }

            if (report.HostContext is { } host)
            {
                keyFacts.Add($"Host: {host.DockState}, displays {host.DisplayCount:N0} ({host.ExternalDisplayCount:N0} external, {host.IndirectDisplayCount:N0} indirect/USB), bluetooth {(host.BluetoothRadioEnabled == true ? "on" : "off")}");
            }

            if (report.ShellResponsiveness is { ShellWindowHung: true })
            {
                keyFacts.Add("Explorer shell was hung");
            }

            keyFacts.AddRange(new[]
            {
                $"Diagnosis: {report.Diagnosis}",
                $"Roots: {report.Roots.Count:N0}",
                $"Files: {totalFiles:N0}",
                $"Directories: {totalDirectories:N0}",
                $"High-risk directories: {highRiskDirectories:N0}",
                $"Known sync issues: {syncBlockers:N0}",
                $"Findings: {report.Findings.Count:N0}",
                $"Recommendations: {report.Recommendations.Count:N0}",
                $"Telemetry: {report.Telemetry.EvidenceState}",
                $"System pressure: {report.SystemPressure.EvidenceState}"
            });

            var timeline = new List<ReportTimelineItem>
            {
                new(report.StartedAt, "scan-started", "Diagnostic scan started", "OneLag report metadata"),
                new(report.FinishedAt, "scan-finished", "Diagnostic scan finished", "OneLag report metadata")
            };

            foreach (var eventLog in report.EventLogs.Where(item => item.NewestTimestamp.HasValue).Take(20))
            {
                timeline.Add(new ReportTimelineItem(
                    eventLog.NewestTimestamp,
                    "windows-event",
                    $"{eventLog.Provider} event {eventLog.EventId}",
                    $"{eventLog.LogName} {eventLog.Level}, count {eventLog.Count:N0}"));
            }

            foreach (var finding in report.Findings.Take(20))
            {
                timeline.Add(new ReportTimelineItem(
                    null,
                    "finding",
                    finding.Title,
                    $"{finding.Severity}; confidence {finding.Confidence}; {finding.Evidence}"));
            }

            var nextActions = report.Recommendations
                .Take(12)
                .Select(recommendation => $"{recommendation.Title} ({recommendation.Kind}): {recommendation.Rationale}")
                .ToArray();

            return new LocalReportSummary(
                path,
                LocalReportKind.Diagnostic,
                "OneLag Diagnostic Report",
                keyFacts,
                timeline.OrderBy(item => item.Timestamp ?? DateTimeOffset.MaxValue).ToArray(),
                nextActions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LocalReportSummary SummarizeDiagnosticMarkdown(string path, string text)
    {
        var sections = MarkdownSections(text);
        var keyFacts = new List<string>();

        // The ranked-cause table and evidence-quality banner are the headline of a diagnostic report, so a
        // summary that omitted them would reproduce the old failure of leading with the OneDrive verdict.
        if (sections.TryGetValue("Evidence Quality", out var qualityLines))
        {
            var grade = qualityLines.FirstOrDefault(line => line.StartsWith("**", StringComparison.Ordinal));
            if (grade is not null)
            {
                keyFacts.Add($"Evidence quality: {CleanMarkdownText(grade)}");
            }
        }

        if (sections.TryGetValue("Ranked Causes", out var causeLines))
        {
            foreach (var row in causeLines
                .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
                .Skip(2)
                .Take(3))
            {
                var cells = row.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (cells.Length >= 3 && !cells[1].Equals("NotSupported", StringComparison.Ordinal) && !cells[1].Equals("Unknown", StringComparison.Ordinal))
                {
                    keyFacts.Add($"Cause: {cells[0]} ({cells[1]}, score {cells[2]})");
                }
            }
        }

        keyFacts.AddRange(TopBulletsBeforeFirstSection(text)
            .Select(CleanMarkdownText)
            .Take(12));

        var timeline = new List<ReportTimelineItem>();
        if (sections.TryGetValue("Recent Windows Events", out var eventLines))
        {
            foreach (var line in eventLines.Where(IsBullet).Take(20))
            {
                timeline.Add(new ReportTimelineItem(
                    TryParseNewestTimestamp(line),
                    "windows-event",
                    FirstBacktickValue(line) ?? "Windows event",
                    CleanMarkdownText(line)));
            }
        }

        if (sections.TryGetValue("Findings", out var findingLines))
        {
            foreach (var line in findingLines.Where(IsBullet).Take(20))
            {
                var evidence = CleanMarkdownText(line);
                timeline.Add(new ReportTimelineItem(
                    null,
                    "finding",
                    MarkdownFindingSummary(evidence),
                    evidence));
            }
        }

        var nextActions = sections.TryGetValue("Recommendations", out var recommendationLines)
            ? recommendationLines.Where(IsBullet).Take(12).Select(CleanMarkdownText).ToArray()
            : Array.Empty<string>();

        return new LocalReportSummary(
            path,
            LocalReportKind.Diagnostic,
            "OneLag Diagnostic Report",
            keyFacts,
            timeline.OrderBy(item => item.Timestamp ?? DateTimeOffset.MaxValue).ToArray(),
            nextActions);
    }

    private static LocalReportSummary SummarizeWatchMarkdown(string path, string text)
    {
        var sections = MarkdownSections(text);
        var keyFacts = TopBulletsBeforeFirstSection(text)
            .Select(CleanMarkdownText)
            .Take(12)
            .ToArray();

        var timeline = new List<ReportTimelineItem>();
        if (sections.TryGetValue("Markers", out var markerLines))
        {
            foreach (var line in markerLines.Where(IsBullet).Take(50))
            {
                timeline.Add(new ReportTimelineItem(
                    TryParseFirstTimestamp(line),
                    "lag-marker",
                    "Manual lag marker",
                    CleanMarkdownText(line)));
            }
        }

        if (sections.TryGetValue("Episodes", out var episodeLines))
        {
            foreach (var line in episodeLines.Where(IsBullet).Take(50))
            {
                if (line.Contains("No lag episodes", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                timeline.Add(new ReportTimelineItem(
                    TryParseFirstTimestamp(line),
                    "lag-episode",
                    EpisodeSummary(line),
                    CleanMarkdownText(line)));
            }
        }

        if (sections.TryGetValue("Largest Timer Delays", out var delayLines))
        {
            foreach (var line in delayLines.Where(IsBullet).Take(10))
            {
                timeline.Add(new ReportTimelineItem(
                    TryParseFirstTimestamp(line),
                    "timer-delay",
                    "Timer delay sample",
                    CleanMarkdownText(line)));
            }
        }

        var hasUnknownEpisode = timeline.Any(item =>
            item.Kind == "lag-episode" &&
            item.Evidence.Contains("Unknown", StringComparison.OrdinalIgnoreCase));
        var hasOneDriveEpisode = timeline.Any(item =>
            item.Kind == "lag-episode" &&
            item.Evidence.Contains("OneDrivePossible", StringComparison.OrdinalIgnoreCase));
        var nextActions = new List<string>();
        if (hasOneDriveEpisode)
        {
            nextActions.Add("Run a fresh scan near the lag window and compare OneDrive inventory risk with watch evidence.");
        }

        if (hasUnknownEpisode)
        {
            nextActions.Add("Use the trace escalation plan for WPR/WPA or ProcMon if episodes remain unknown.");
        }

        if (timeline.Count == 0)
        {
            nextActions.Add("Keep watch mode running during a real lag event and add manual lag markers.");
        }

        return new LocalReportSummary(
            path,
            LocalReportKind.Watch,
            "OneLag Watch Report",
            keyFacts,
            timeline.OrderBy(item => item.Timestamp ?? DateTimeOffset.MaxValue).ToArray(),
            nextActions);
    }

    private static LocalReportSummary SummarizeUnknown(string path, string text)
    {
        var lines = text.SplitLines();
        var title = lines.FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?.TrimStart('#', ' ')
            ?? "Unknown report";

        return new LocalReportSummary(
            path,
            LocalReportKind.Unknown,
            title,
            new[] { $"Lines: {lines.Length:N0}", "Format: unknown" },
            Array.Empty<ReportTimelineItem>(),
            new[] { "Open the report directly or regenerate it with the current OneLag release." });
    }

    private static Dictionary<string, List<string>> MarkdownSections(string text)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? section = null;

        foreach (var line in text.SplitLines())
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                section = line[3..].Trim();
                sections[section] = new List<string>();
                continue;
            }

            if (section is not null)
            {
                sections[section].Add(line);
            }
        }

        return sections;
    }

    private static IEnumerable<string> TopBulletsBeforeFirstSection(string text)
    {
        foreach (var line in text.SplitLines())
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                yield break;
            }

            if (IsBullet(line))
            {
                yield return line;
            }
        }
    }

    private static bool IsBullet(string line)
    {
        return line.TrimStart().StartsWith("- ", StringComparison.Ordinal);
    }

    private static string CleanMarkdownText(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return text.Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string? FirstBacktickValue(string line)
    {
        var first = line.IndexOf('`');
        if (first < 0)
        {
            return null;
        }

        var second = line.IndexOf('`', first + 1);
        return second > first ? line[(first + 1)..second] : null;
    }

    private static DateTimeOffset? TryParseFirstTimestamp(string line)
    {
        var value = FirstBacktickValue(line);
        return TryParseTimestamp(value);
    }

    private static DateTimeOffset? TryParseNewestTimestamp(string line)
    {
        const string marker = "newest `";
        var markerIndex = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = markerIndex + marker.Length;
        var end = line.IndexOf('`', start);
        return end > start ? TryParseTimestamp(line[start..end]) : null;
    }

    private static DateTimeOffset? TryParseTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private static string EpisodeSummary(string line)
    {
        var values = BacktickValues(line).ToArray();
        return values.Length >= 3 ? $"{values[2]} episode" : "Lag episode";
    }

    private static string MarkdownFindingSummary(string evidence)
    {
        var colonIndex = evidence.IndexOf(": ", StringComparison.Ordinal);
        if (colonIndex >= 0)
        {
            var firstSentenceEnd = evidence.IndexOf(". ", colonIndex + 2, StringComparison.Ordinal);
            if (firstSentenceEnd > 0)
            {
                return evidence[..firstSentenceEnd];
            }
        }

        var confidenceIndex = evidence.IndexOf(". Confidence:", StringComparison.OrdinalIgnoreCase);
        return confidenceIndex > 0 ? evidence[..confidenceIndex] : evidence;
    }

    private static IEnumerable<string> BacktickValues(string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            var first = line.IndexOf('`', index);
            if (first < 0)
            {
                yield break;
            }

            var second = line.IndexOf('`', first + 1);
            if (second < 0)
            {
                yield break;
            }

            yield return line[(first + 1)..second];
            index = second + 1;
        }
    }
}

internal static class StringLineExtensions
{
    public static string[] SplitLines(this string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }
}
