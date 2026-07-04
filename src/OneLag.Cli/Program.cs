using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using OneLag.Core;
using OneLag.Windows;

namespace OneLag.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions LineJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        TrySetIdlePriority();

        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "scan" => RunScan(args[1..], cancellation.Token),
                "watch" => await RunWatch(args[1..], cancellation.Token),
                "repair" => RunRepair(args[1..]),
                "support" => RunSupport(args[1..]),
                "remediate" => RunRemediate(args[1..], cancellation.Token),
                "interactive" => await RunInteractive(cancellation.Token),
                "version" => RunVersion(),
                _ => UnknownCommand(args[0])
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Canceled.");
            return 130;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static int RunScan(string[] args, CancellationToken cancellationToken)
    {
        var roots = new List<string>();
        var output = "onelag-report.md";
        var format = "markdown";
        var fullPaths = false;
        var maxItems = 500_000;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    roots.Add(RequireValue(args, ref i, "--root"));
                    break;
                case "--output":
                case "-o":
                    output = RequireValue(args, ref i, args[i]);
                    break;
                case "--format":
                    format = RequireValue(args, ref i, "--format").ToLowerInvariant();
                    break;
                case "--full-paths":
                    fullPaths = true;
                    break;
                case "--max-items":
                    maxItems = int.Parse(RequireValue(args, ref i, "--max-items"), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown scan argument '{args[i]}'.");
            }
        }

        if (format is not ("markdown" or "md" or "json"))
        {
            throw new ArgumentException("--format must be 'markdown' or 'json'.");
        }

        if (maxItems <= 0)
        {
            throw new ArgumentException("--max-items must be greater than zero.");
        }

        var platform = new WindowsPlatformProbe();
        var runner = new ScanRunner(platform, new InventoryScanner(), new RiskEngine());
        var options = new ScanOptions(roots, output, format, fullPaths, maxItems);
        var report = runner.Run(options, cancellationToken);
        var redactor = new Redactor(fullPaths, report.Roots.Select(root => root.Path));

        var text = format is "json"
            ? ReportWriter.ToJson(report)
            : ReportWriter.ToMarkdown(report, redactor);

        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Environment.CurrentDirectory);
        File.WriteAllText(outputPath, text);

        Console.WriteLine($"Diagnosis: {report.Diagnosis}");
        Console.WriteLine($"Findings: {report.Findings.Count}");
        Console.WriteLine($"Report: {outputPath}");
        return report.Findings.Any(finding => finding.Severity is Severity.HighRisk or Severity.Emergency) ? 1 : 0;
    }

    private static async Task<int> RunWatch(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintWatchHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "start" => await WatchStart(args[1..], cancellationToken),
            "stop" => WatchStop(args[1..]),
            "status" => WatchStatus(args[1..]),
            "mark" => WatchMark(args[1..]),
            "report" => WatchReport(args[1..]),
            _ => UnknownCommand($"watch {args[0]}")
        };
    }

    private static int RunRepair(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintRepairHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "reset-onedrive" => RunResetOneDrive(args[1..]),
            _ => UnknownCommand($"repair {args[0]}")
        };
    }

    private static int RunSupport(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintSupportHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "trace-plan" => RunTracePlan(args[1..]),
            _ => UnknownCommand($"support {args[0]}")
        };
    }

    private static int RunTracePlan(string[] args)
    {
        var output = "onelag-trace-plan";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                case "-o":
                    output = RequireValue(args, ref i, args[i]);
                    break;
                default:
                    throw new ArgumentException($"Unknown support trace-plan argument '{args[i]}'.");
            }
        }

        var files = EscalationPlanWriter.WriteTracePlan(output);
        Console.WriteLine($"Trace escalation plan: {Path.GetFullPath(output)}");
        foreach (var file in files)
        {
            Console.WriteLine($"- {file}");
        }

        return 0;
    }

    private static int RunRemediate(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintRemediateHelp();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "move-plan" => RunMovePlan(args[1..], cancellationToken),
            _ => UnknownCommand($"remediate {args[0]}")
        };
    }

    private static int RunMovePlan(string[] args, CancellationToken cancellationToken)
    {
        string? source = null;
        string? destination = null;
        var output = "onelag-move-plan";
        var maxItems = 100_000;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source":
                    source = RequireValue(args, ref i, "--source");
                    break;
                case "--destination":
                    destination = RequireValue(args, ref i, "--destination");
                    break;
                case "--output":
                case "-o":
                    output = RequireValue(args, ref i, args[i]);
                    break;
                case "--max-items":
                    maxItems = int.Parse(RequireValue(args, ref i, "--max-items"), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown remediate move-plan argument '{args[i]}'.");
            }
        }

        if (source is null)
        {
            throw new ArgumentException("--source is required.");
        }

        if (destination is null)
        {
            throw new ArgumentException("--destination is required.");
        }

        var summary = MovePlanWriter.Write(new MovePlanOptions(source, destination, output, maxItems), cancellationToken);
        Console.WriteLine($"Move plan: {Path.GetFullPath(output)}");
        Console.WriteLine($"Files: {summary.FileCount:N0}");
        Console.WriteLine($"Directories: {summary.DirectoryCount:N0}");
        Console.WriteLine($"Bytes: {summary.TotalBytes:N0}");
        Console.WriteLine($"Destination has enough space: {summary.DestinationHasEnoughSpace?.ToString() ?? "unknown"}");
        return 0;
    }

    private static int RunResetOneDrive(string[] args)
    {
        var execute = false;
        var acknowledged = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--execute":
                    execute = true;
                    break;
                case "--i-understand-reset-disconnects-sync":
                    acknowledged = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown repair reset-onedrive argument '{args[i]}'.");
            }
        }

        var platform = new WindowsPlatformProbe();
        var roots = platform.DiscoverOneDriveRoots();
        var telemetry = platform.CaptureTelemetry();
        var health = platform.CaptureOneDriveClientHealth(roots, telemetry);

        Console.WriteLine("OneDrive reset plan");
        Console.WriteLine($"Internal sync database parsed: {health.InternalSyncDatabaseParsed}");
        Console.WriteLine($"Evidence: {health.EvidenceState}");
        Console.WriteLine();
        foreach (var signal in health.Signals)
        {
            Console.WriteLine($"- {signal.Severity} {signal.Kind}: {signal.Evidence}");
        }

        Console.WriteLine();
        Console.WriteLine("Safety:");
        Console.WriteLine("- OneLag does not edit OneDrive cache, DAT, or database files.");
        Console.WriteLine("- Microsoft documents reset as disconnecting sync connections and performing a full sync after restart.");
        Console.WriteLine("- Confirm visible OneDrive status and work policy before executing on a managed laptop.");
        Console.WriteLine();

        if (health.ResetCommands.Count == 0)
        {
            Console.WriteLine("No supported OneDrive reset command candidate was found.");
            return 1;
        }

        Console.WriteLine("Supported reset command candidates:");
        foreach (var command in health.ResetCommands)
        {
            Console.WriteLine($"- {command.ExecutablePath} {command.Arguments} ({command.Source})");
        }

        if (!execute)
        {
            Console.WriteLine();
            Console.WriteLine("Dry run only. To execute the first reset command candidate:");
            Console.WriteLine("onelag repair reset-onedrive --execute --i-understand-reset-disconnects-sync");
            return 0;
        }

        if (!acknowledged)
        {
            throw new ArgumentException("--execute requires --i-understand-reset-disconnects-sync.");
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Executing OneDrive reset is only supported on Windows.");
        }

        var selected = health.ResetCommands[0];
        using var process = Process.Start(new ProcessStartInfo(selected.ExecutablePath, selected.Arguments)
        {
            UseShellExecute = true
        });

        Console.WriteLine($"Started: {selected.ExecutablePath} {selected.Arguments}");
        Console.WriteLine("If OneDrive does not restart automatically, start OneDrive from the Start menu.");
        return 0;
    }

    private static async Task<int> WatchStart(string[] args, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromHours(8);
        var interval = TimeSpan.FromSeconds(2);
        var output = GetDefaultWatchDirectory();
        var maxSamples = 14_400;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--duration":
                    duration = ParseDuration(RequireValue(args, ref i, "--duration"));
                    break;
                case "--interval":
                    interval = ParseDuration(RequireValue(args, ref i, "--interval"));
                    break;
                case "--output":
                    output = RequireValue(args, ref i, "--output");
                    break;
                case "--max-samples":
                    maxSamples = int.Parse(RequireValue(args, ref i, "--max-samples"), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown watch start argument '{args[i]}'.");
            }
        }

        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24))
        {
            throw new ArgumentException("--duration must be greater than zero and no more than 24h.");
        }

        if (interval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException("--interval must be at least 1s.");
        }

        if (maxSamples <= 0)
        {
            throw new ArgumentException("--max-samples must be greater than zero.");
        }

        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        File.Delete(Path.Combine(directory, "stop.request"));

        var statePath = Path.Combine(directory, "state.json");
        var samplesPath = Path.Combine(directory, "samples.ndjson");
        var platform = new WindowsPlatformProbe();
        var startedAt = DateTimeOffset.UtcNow;
        var expected = Stopwatch.StartNew();
        var nextExpected = interval;
        var sampleCount = 0;

        Console.WriteLine($"Watch started for {duration}. Output: {directory}");
        Console.WriteLine("Press Ctrl+C to stop, or run `onelag watch stop --output <dir>` from another terminal.");
        WriteWatchState(statePath, startedAt, directory, sampleCount, "running");

        while (!cancellationToken.IsCancellationRequested && DateTimeOffset.UtcNow - startedAt < duration)
        {
            if (File.Exists(Path.Combine(directory, "stop.request")))
            {
                break;
            }

            await Task.Delay(interval, cancellationToken);
            var drift = (expected.Elapsed - nextExpected).TotalMilliseconds;
            nextExpected += interval;

            var sample = new WatchSample(
                DateTimeOffset.UtcNow,
                drift,
                platform.CaptureTelemetry(),
                platform.CaptureSystemPressure(),
                platform.GetForegroundProcessName());

            AppendJsonLine(samplesPath, sample);
            sampleCount++;

            if (sampleCount > maxSamples)
            {
                TrimJsonLines(samplesPath, maxSamples);
                sampleCount = maxSamples;
            }

            WriteWatchState(statePath, startedAt, directory, sampleCount, "running");
        }

        WriteWatchState(statePath, startedAt, directory, sampleCount, "stopped");
        Console.WriteLine($"Watch stopped. Samples: {sampleCount:N0}");
        return 0;
    }

    private static int WatchStop(string[] args)
    {
        var output = ParseOutputOnly(args);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "stop.request"), DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        Console.WriteLine($"Stop requested for watch directory: {output}");
        return 0;
    }

    private static int WatchStatus(string[] args)
    {
        var output = ParseOutputOnly(args);
        var statePath = Path.Combine(output, "state.json");
        if (!File.Exists(statePath))
        {
            Console.WriteLine($"No watch state found in {output}");
            return 1;
        }

        Console.WriteLine(File.ReadAllText(statePath));
        return 0;
    }

    private static int WatchMark(string[] args)
    {
        var output = GetDefaultWatchDirectory();
        string? note = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    output = RequireValue(args, ref i, "--output");
                    break;
                case "--note":
                    note = RequireValue(args, ref i, "--note");
                    break;
                default:
                    throw new ArgumentException($"Unknown watch mark argument '{args[i]}'.");
            }
        }

        Directory.CreateDirectory(output);
        var marker = new WatchMarker(DateTimeOffset.UtcNow, "cli", note);
        AppendJsonLine(Path.Combine(output, "markers.ndjson"), marker);
        Console.WriteLine($"Lag marker written: {marker.Timestamp:O}");
        return 0;
    }

    private static int WatchReport(string[] args)
    {
        var output = GetDefaultWatchDirectory();
        var reportPath = "onelag-watch-report.md";

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--output":
                    output = RequireValue(args, ref i, "--output");
                    break;
                case "--report":
                    reportPath = RequireValue(args, ref i, "--report");
                    break;
                default:
                    throw new ArgumentException($"Unknown watch report argument '{args[i]}'.");
            }
        }

        var samples = ReadJsonLines<WatchSample>(Path.Combine(output, "samples.ndjson")).ToArray();
        var markers = ReadJsonLines<WatchMarker>(Path.Combine(output, "markers.ndjson")).ToArray();
        var report = BuildWatchReport(samples, markers);
        var fullReportPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullReportPath) ?? Environment.CurrentDirectory);
        File.WriteAllText(fullReportPath, report);
        Console.WriteLine($"Watch report: {fullReportPath}");
        return 0;
    }

    private static async Task<int> RunInteractive(CancellationToken cancellationToken)
    {
        Console.WriteLine("OneLag");
        Console.WriteLine("1. Run scan");
        Console.WriteLine("2. Start watch mode");
        Console.WriteLine("3. Mark lag now");
        Console.WriteLine("4. Review OneDrive reset plan");
        Console.WriteLine("5. Generate WPR/ProcMon trace escalation plan");
        Console.Write("Choice: ");
        var choice = Console.ReadLine();

        return choice switch
        {
            "1" => RunScan(Array.Empty<string>(), cancellationToken),
            "2" => await WatchStart(Array.Empty<string>(), cancellationToken),
            "3" => WatchMark(Array.Empty<string>()),
            "4" => RunResetOneDrive(Array.Empty<string>()),
            "5" => RunTracePlan(Array.Empty<string>()),
            _ => 1
        };
    }

    private static int RunVersion()
    {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        Console.WriteLine($"OneLag {version}");
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintHelp();
        return 2;
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static string ParseOutputOnly(string[] args)
    {
        var output = GetDefaultWatchDirectory();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--output")
            {
                output = RequireValue(args, ref i, "--output");
            }
            else
            {
                throw new ArgumentException($"Unknown watch argument '{args[i]}'.");
            }
        }

        return Path.GetFullPath(output);
    }

    private static TimeSpan ParseDuration(string value)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (value.EndsWith('h') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (value.EndsWith('m') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        if (value.EndsWith('s') && double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        throw new ArgumentException($"Invalid duration '{value}'. Use 30s, 15m, 8h, or 00:30:00.");
    }

    private static string GetDefaultWatchDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".onelag");
        }

        return Path.Combine(localAppData, "OneLag", "watch");
    }

    private static void AppendJsonLine<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        File.AppendAllText(path, JsonSerializer.Serialize(value, LineJsonOptions) + Environment.NewLine);
    }

    private static IEnumerable<T> ReadJsonLines<T>(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var value = JsonSerializer.Deserialize<T>(line, LineJsonOptions);
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static void TrimJsonLines(string path, int maxLines)
    {
        var lines = File.ReadLines(path).TakeLast(maxLines).ToArray();
        File.WriteAllLines(path, lines);
    }

    private static void WriteWatchState(string path, DateTimeOffset startedAt, string directory, int samples, string state)
    {
        var payload = new
        {
            state,
            startedAt,
            updatedAt = DateTimeOffset.UtcNow,
            directory,
            samples
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static string BuildWatchReport(IReadOnlyList<WatchSample> samples, IReadOnlyList<WatchMarker> markers)
    {
        var lines = new List<string>
        {
            "# OneLag Watch Report",
            "",
            $"- Samples: `{samples.Count:N0}`",
            $"- Markers: `{markers.Count:N0}`"
        };

        if (samples.Count > 0)
        {
            lines.Add($"- First sample: `{samples[0].Timestamp:O}`");
            lines.Add($"- Last sample: `{samples[^1].Timestamp:O}`");
            lines.Add($"- Max timer drift: `{samples.Max(sample => sample.TimerDriftMilliseconds):N1} ms`");
        }

        lines.Add("");
        lines.Add("## Markers");
        foreach (var marker in markers)
        {
            lines.Add($"- `{marker.Timestamp:O}` from `{marker.Source}`{(string.IsNullOrWhiteSpace(marker.Note) ? "" : $": {marker.Note}")}");
        }

        lines.Add("");
        lines.Add("## Largest Timer Delays");
        foreach (var sample in samples.OrderByDescending(sample => sample.TimerDriftMilliseconds).Take(10))
        {
            lines.Add($"- `{sample.Timestamp:O}` drift `{sample.TimerDriftMilliseconds:N1} ms`, foreground `{sample.ForegroundProcess ?? "unknown"}`, telemetry `{sample.Telemetry.EvidenceState}`");
        }

        lines.Add("");
        lines.Add("## Interpretation");
        lines.Add("Timer drift is a user-mode responsiveness canary. It can show that normal work was delayed, but WPR/WPA is required to prove driver DPC/ISR root cause.");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void TrySetIdlePriority()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Idle;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Priority lowering is best effort. Failure should not block diagnostics.
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        OneLag

        Commands:
          scan [--root PATH] [--output PATH] [--format markdown|json] [--full-paths]
          watch start [--duration 8h] [--interval 2s] [--output DIR]
          watch stop [--output DIR]
          watch status [--output DIR]
          watch mark [--output DIR] [--note TEXT]
          watch report [--output DIR] [--report PATH]
          repair reset-onedrive [--execute --i-understand-reset-disconnects-sync]
          support trace-plan [--output DIR]
          remediate move-plan --source PATH --destination PATH [--output DIR]
          interactive
          version
        """);
    }

    private static void PrintWatchHelp()
    {
        Console.WriteLine("""
        OneLag watch

        Commands:
          start   Start a bounded foreground responsiveness recorder.
          stop    Request a running recorder to stop.
          status  Print recorder state.
          mark    Record "lag happened now" without capturing input content.
          report  Generate a Markdown timeline report.
        """);
    }

    private static void PrintRepairHelp()
    {
        Console.WriteLine("""
        OneLag repair

        Commands:
          reset-onedrive   Show a dry-run Microsoft-supported OneDrive reset plan.

        Execution:
          reset-onedrive --execute --i-understand-reset-disconnects-sync
        """);
    }

    private static void PrintSupportHelp()
    {
        Console.WriteLine("""
        OneLag support

        Commands:
          trace-plan   Generate local WPR/WPA and ProcMon escalation runbooks and helper scripts.

        Safety:
          The generated plan does not start tracing. Review the files, then run the WPR scripts manually on Windows.
        """);
    }

    private static void PrintRemediateHelp()
    {
        Console.WriteLine("""
        OneLag remediate

        Commands:
          move-plan   Generate a dry-run move plan, execution script, rollback script, and verification script.

        Safety:
          Generated scripts do not move files unless run with -Execute -IUnderstandMovesFiles.
        """);
    }
}
