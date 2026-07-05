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
                "view" => RunView(args[1..]),
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
            "move" => RunMove(args[1..], cancellationToken),
            "rollback" => RunRollback(args[1..], cancellationToken),
            "verify" => RunMoveVerify(args[1..], cancellationToken),
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

    private static int RunMove(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseMoveExecutionOptions(args, "remediate move");
        var result = MovePlanExecutor.Move(options, cancellationToken);
        PrintMoveExecutionResult(result);
        return 0;
    }

    private static int RunRollback(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseMoveExecutionOptions(args, "remediate rollback");
        var result = MovePlanExecutor.Rollback(options, cancellationToken);
        PrintMoveExecutionResult(result);
        return 0;
    }

    private static int RunMoveVerify(string[] args, CancellationToken cancellationToken)
    {
        string? source = null;
        string? destination = null;
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
                case "--max-items":
                    maxItems = int.Parse(RequireValue(args, ref i, "--max-items"), CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown remediate verify argument '{args[i]}'.");
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

        var result = MovePlanExecutor.Verify(source, destination, maxItems, cancellationToken);
        PrintMoveExecutionResult(result);
        return result.DestinationExists ? 0 : 1;
    }

    private static MoveExecutionOptions ParseMoveExecutionOptions(string[] args, string commandName)
    {
        string? source = null;
        string? destination = null;
        var maxItems = 100_000;
        var execute = false;
        var acknowledged = false;

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
                case "--max-items":
                    maxItems = int.Parse(RequireValue(args, ref i, "--max-items"), CultureInfo.InvariantCulture);
                    break;
                case "--execute":
                    execute = true;
                    break;
                case "--i-understand-moves-files":
                    acknowledged = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown {commandName} argument '{args[i]}'.");
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

        return new MoveExecutionOptions(source, destination, execute, acknowledged, maxItems);
    }

    private static void PrintMoveExecutionResult(MoveExecutionResult result)
    {
        Console.WriteLine($"Operation: {result.Operation}");
        Console.WriteLine($"Executed: {result.Executed}");
        Console.WriteLine($"Source: {result.Source}");
        Console.WriteLine($"Destination: {result.Destination}");
        Console.WriteLine($"Source exists: {result.SourceExists}");
        Console.WriteLine($"Destination exists: {result.DestinationExists}");
        Console.WriteLine($"Files: {result.FileCount:N0}");
        Console.WriteLine($"Directories: {result.DirectoryCount:N0}");
        Console.WriteLine($"Bytes: {result.TotalBytes:N0}");
        Console.WriteLine($"Capped: {result.WasCapped}");
        Console.WriteLine($"Destination available bytes: {result.DestinationAvailableBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "unknown"}");
        Console.WriteLine($"Destination has enough space: {result.DestinationHasEnoughSpace?.ToString() ?? "unknown"}");
        Console.WriteLine($"Inaccessible paths: {result.InaccessiblePaths.Count:N0}");
        Console.WriteLine(result.Message);
    }

    private static int RunView(string[] args)
    {
        string? reportPath = null;
        var showFullTimeline = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--report":
                case "-r":
                    reportPath = RequireValue(args, ref i, args[i]);
                    break;
                case "--timeline":
                    showFullTimeline = true;
                    break;
                default:
                    if (reportPath is null && !args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        reportPath = args[i];
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown view argument '{args[i]}'.");
                    }

                    break;
            }
        }

        if (reportPath is null)
        {
            throw new ArgumentException("view requires --report PATH or a report path argument.");
        }

        var service = new LocalReportViewService();
        var summary = service.Summarize(reportPath);
        PrintLocalReportSummary(summary, showFullTimeline);
        return summary.Kind == LocalReportKind.Unknown ? 1 : 0;
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

        var platform = new WindowsPlatformProbe();
        var watch = new WatchService();
        var directory = Path.GetFullPath(output);

        Console.WriteLine($"Watch started for {duration}. Output: {directory}");
        Console.WriteLine("Press Ctrl+C to stop, or run `onelag watch stop --output <dir>` from another terminal.");
        var summary = await watch.StartAsync(new WatchStartOptions(duration, interval, directory, maxSamples), platform, cancellationToken);
        Console.WriteLine($"Watch stopped. Samples: {summary.Samples:N0}");
        return 0;
    }

    private static int WatchStop(string[] args)
    {
        var output = ParseOutputOnly(args);
        new WatchService().RequestStop(output);
        Console.WriteLine($"Stop requested for watch directory: {output}");
        return 0;
    }

    private static int WatchStatus(string[] args)
    {
        var output = ParseOutputOnly(args);
        var state = new WatchService().ReadState(output);
        if (state is null)
        {
            Console.WriteLine($"No watch state found in {output}");
            return 1;
        }

        Console.WriteLine(JsonSerializer.Serialize(state, JsonOptions));
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

        var marker = new WatchService().Mark(output, "cli", note);
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

        var fullReportPath = new WatchService().WriteReport(output, reportPath);
        Console.WriteLine($"Watch report: {fullReportPath}");
        return 0;
    }

    private static async Task<int> RunInteractive(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("OneLag");
            Console.WriteLine("1. Run scan");
            Console.WriteLine("2. Start watch mode");
            Console.WriteLine("3. Mark lag now");
            Console.WriteLine("4. Review OneDrive reset plan");
            Console.WriteLine("5. Generate WPR/ProcMon trace escalation plan");
            Console.WriteLine("6. View saved report");
            Console.WriteLine("Q. Quit");
            Console.Write("Choice: ");
            var choice = Console.ReadLine();

            switch (choice?.Trim().ToLowerInvariant())
            {
                case "1":
                    return RunScan(Array.Empty<string>(), cancellationToken);
                case "2":
                    return await WatchStart(Array.Empty<string>(), cancellationToken);
                case "3":
                    return WatchMark(Array.Empty<string>());
                case "4":
                    return RunResetOneDrive(Array.Empty<string>());
                case "5":
                    return RunTracePlan(Array.Empty<string>());
                case "6":
                    Console.Write("Report path: ");
                    var reportPath = Console.ReadLine();
                    if (string.IsNullOrWhiteSpace(reportPath))
                    {
                        return 1;
                    }

                    return RunView(new[] { "--report", reportPath, "--timeline" });
                case "q":
                case "quit":
                case "exit":
                    return 0;
                default:
                    Console.WriteLine("Unknown choice.");
                    Console.WriteLine();
                    break;
            }
        }

        throw new OperationCanceledException(cancellationToken);
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
        return WatchService.GetDefaultDirectory();
    }

    private static void PrintLocalReportSummary(LocalReportSummary summary, bool showFullTimeline)
    {
        Console.WriteLine("OneLag Report View");
        Console.WriteLine($"Report: {summary.SourcePath}");
        Console.WriteLine($"Kind: {summary.Kind}");
        Console.WriteLine($"Title: {summary.Title}");

        if (summary.KeyFacts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Key facts:");
            foreach (var fact in summary.KeyFacts)
            {
                Console.WriteLine($"- {fact}");
            }
        }

        if (summary.Timeline.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Timeline:");
            var timeline = showFullTimeline ? summary.Timeline : summary.Timeline.Take(8);
            foreach (var item in timeline)
            {
                var timestamp = item.Timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "not timestamped";
                Console.WriteLine($"- {timestamp} [{item.Kind}] {item.Summary}: {item.Evidence}");
            }

            if (!showFullTimeline && summary.Timeline.Count > 8)
            {
                Console.WriteLine($"- {summary.Timeline.Count - 8:N0} more timeline item(s); pass --timeline to show all.");
            }
        }

        if (summary.NextActions.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Next actions:");
            foreach (var action in summary.NextActions)
            {
                Console.WriteLine($"- {action}");
            }
        }
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
          view --report PATH [--timeline]
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
          move        Move a reviewed folder only with explicit confirmation flags.
          rollback    Move a reviewed folder back only with explicit confirmation flags.
          verify      Verify source/destination existence and destination inventory.

        Safety:
          Generated scripts do not move files unless run with -Execute -IUnderstandMovesFiles.
          Direct move and rollback commands default to dry-run and require --execute --i-understand-moves-files.
        """);
    }
}
