using System.Diagnostics;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.Win32;
using OneLag.Core;
using OneLag.Windows;

namespace OneLag.Gui;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
        {
            return RunSmoke();
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        Application.Run(context);
        return 0;
    }

    private static int RunSmoke()
    {
        _ = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var service = new WatchService();
        _ = service.BuildReport(Array.Empty<WatchSample>(), Array.Empty<WatchMarker>());
        _ = new LocalReportViewService();
        _ = new SessionComparisonService(service);
        _ = new SelfTestService(new WindowsPlatformProbe());
        _ = new LogCollectionService();
        return 0;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon notifyIcon;
    private readonly MainForm mainForm;

    public TrayApplicationContext()
    {
        mainForm = new MainForm();
        notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "OneLag",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        notifyIcon.DoubleClick += (_, _) => ShowDashboard();
        mainForm.FormClosed += (_, _) =>
        {
            if (mainForm.AllowExit)
            {
                ExitThread();
            }
        };
        mainForm.Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            notifyIcon.Dispose();
            mainForm.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Self Test", null, async (_, _) => await mainForm.RunSelfTestFromTrayAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Start Watch", null, async (_, _) => await mainForm.StartWatchFromTrayAsync());
        menu.Items.Add("Stop Watch", null, (_, _) => mainForm.RequestStopWatch());
        menu.Items.Add("Mark Lag Now", null, (_, _) => mainForm.MarkLagFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Collect Logs", null, async (_, _) => await mainForm.CollectLogsFromTrayAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        return menu;
    }

    private void ShowDashboard()
    {
        if (!mainForm.Visible)
        {
            mainForm.Show();
        }

        mainForm.WindowState = FormWindowState.Normal;
        mainForm.Activate();
    }

    private void ExitApplication()
    {
        notifyIcon.Visible = false;
        mainForm.AllowExit = true;
        mainForm.Close();
        ExitThread();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class MainForm : Form
{
    private readonly WatchService watchService = new();
    private readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox scanRootBox = new();
    private readonly TextBox scanOutputBox = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "onelag-report.md") };
    private readonly NumericUpDown scanMaxItemsBox = new() { Minimum = 1, Maximum = 5_000_000, Value = 500_000, Increment = 10_000 };
    private readonly TextBox watchDirectoryBox = new() { Text = WatchService.GetDefaultDirectory() };
    private readonly TextBox watchDurationBox = new() { Text = "8h" };
    private readonly NumericUpDown watchIntervalBox = new() { Minimum = 1, Maximum = 60, Value = 2 };
    private readonly Label watchStateLabel = new() { Text = "Watch state: unknown", AutoSize = true };
    private readonly TextBox reportPathBox = new();
    private readonly ListView reportList = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly TextBox bundleReportPathBox = new();
    private readonly TextBox bundleWatchDirectoryBox = new() { Text = WatchService.GetDefaultDirectory() };
    private readonly TextBox bundleOutputBox = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "onelag-support-bundle") };
    private readonly TextBox bundleNoteBox = new() { Multiline = true, Height = 72, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox bundleOneDriveStatusBox = new();
    private readonly CheckBox bundleZipBox = new() { Text = "Zip bundle", AutoSize = true, Checked = true };
    private readonly CheckBox bundleTracePlanBox = new() { Text = "Include trace plan", AutoSize = true };
    private readonly CheckBox bundleOverwriteBox = new() { Text = "Overwrite output", AutoSize = true };
    private readonly TextBox remediationSourceBox = new();
    private readonly TextBox remediationDestinationBox = new();
    private readonly TextBox remediationOutputBox = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "onelag-move-plan") };
    private readonly CheckBox acknowledgeMoveBox = new() { Text = "I understand this moves files", AutoSize = true };
    private readonly Panel readinessBanner = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Label readinessLabel = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 8, 12, 8) };
    private readonly Label selfTestSummaryLabel = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
    private readonly TextBox collectOutputBox = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"onelag-logs-{DateTime.Now:yyyyMMdd-HHmmss}") };
    private readonly NumericUpDown collectHoursBox = new() { Minimum = 1, Maximum = 168, Value = 48 };
    private readonly NumericUpDown collectMaxTotalBox = new() { Minimum = 16, Maximum = 20_480, Value = 2_048, Increment = 256 };
    private readonly CheckBox collectAllChannelsBox = new() { Text = "All event channels", AutoSize = true };
    private readonly CheckBox collectZipBox = new() { Text = "Zip bundle", AutoSize = true, Checked = true };
    private readonly CheckBox collectWindowsTreeBox = new() { Text = "Windows log tree", AutoSize = true, Checked = true };
    private readonly CheckBox collectOverwriteBox = new() { Text = "Overwrite", AutoSize = true };
    private readonly TextBox compareSessionABox = new() { Text = WatchService.GetDefaultDirectory() };
    private readonly TextBox compareSessionBBox = new();
    private readonly TextBox compareOutputBox = new() { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "onelag-comparison.md") };
    // A dark neutral so the white banner text has contrast from the first paint, before the self-test
    // returns. If the self-test throws, the banner stays readable rather than white-on-light-gray.
    private Color readinessColor = Color.FromArgb(70, 70, 70);
    private CancellationTokenSource? watchCancellation;
    private Task? watchTask;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool AllowExit { get; set; }

    public MainForm()
    {
        Text = "OneLag";
        MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        BuildUi();
        ApplySystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Tell a non-CLI user immediately whether this machine can measure anything, before they invest a day
        // recording a watch session whose collectors were all dead.
        _ = RunSelfTestAsync(startup: true);
    }

    public async Task StartWatchFromTrayAsync()
    {
        if (watchTask is { IsCompleted: false })
        {
            MarkLagFromTray();
            return;
        }

        await StartWatchAsync();
    }

    public void RequestStopWatch()
    {
        try
        {
            watchService.RequestStop(watchDirectoryBox.Text);
            watchCancellation?.Cancel();
            Log("Watch stop requested.");
            RefreshWatchState();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    public void MarkLagFromTray()
    {
        try
        {
            var marker = watchService.Mark(watchDirectoryBox.Text, "tray", "lag marker from tray");
            Log($"Lag marker written: {marker.Timestamp:O}");
            RefreshWatchState();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        watchCancellation?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            watchCancellation?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildUi()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildDiagnoseTab());
        tabs.TabPages.Add(BuildScanTab());
        tabs.TabPages.Add(BuildWatchTab());
        tabs.TabPages.Add(BuildCollectTab());
        tabs.TabPages.Add(BuildCompareTab());
        tabs.TabPages.Add(BuildReportTab());
        tabs.TabPages.Add(BuildSupportTab());
        tabs.TabPages.Add(BuildRemediationTab());

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 430
        };
        split.Panel1.Controls.Add(tabs);
        split.Panel2.Controls.Add(logBox);

        readinessLabel.Text = "Checking whether this machine can measure anything...";
        readinessBanner.Controls.Add(readinessLabel);

        // The Fill control is added before the Top banner so the banner sits above it and the split fills the
        // remainder.
        Controls.Add(split);
        Controls.Add(readinessBanner);
    }

    private TabPage BuildDiagnoseTab()
    {
        var page = new TabPage("Diagnose");
        var layout = CreateTable(3);
        selfTestSummaryLabel.Text = "Run a self test to see which probes are measuring live data on this machine.";
        AddCommandRow(layout, selfTestSummaryLabel);

        var selfTestButton = new Button { Text = "Run Self Test", AutoSize = true };
        selfTestButton.Click += async (_, _) => await RunSelfTestAsync();
        AddCommandRow(layout, selfTestButton);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildCollectTab()
    {
        var page = new TabPage("Collect Logs");
        var layout = CreateTable(6);
        AddRow(layout, "Bundle output", collectOutputBox, BrowseFolderButton(collectOutputBox));
        AddRow(layout, "Event window (h)", collectHoursBox);
        AddRow(layout, "Max total (MB)", collectMaxTotalBox);
        AddCommandRow(layout, collectWindowsTreeBox, collectAllChannelsBox, collectZipBox, collectOverwriteBox);

        var collectButton = new Button { Text = "Collect Logs", AutoSize = true };
        collectButton.Click += async (_, _) => await RunCollectAsync();
        AddCommandRow(layout, collectButton);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildCompareTab()
    {
        var page = new TabPage("Compare");
        var layout = CreateTable(4);
        AddRow(layout, "Session A", compareSessionABox, BrowseFolderButton(compareSessionABox));
        AddRow(layout, "Session B", compareSessionBBox, BrowseFolderButton(compareSessionBBox));
        AddRow(layout, "Report", compareOutputBox, SaveFileButton(compareOutputBox, "Markdown report|*.md"));

        var compareButton = new Button { Text = "Compare Sessions", AutoSize = true };
        compareButton.Click += (_, _) => RunCompare();
        AddCommandRow(layout, compareButton);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildScanTab()
    {
        var page = new TabPage("Scan");
        var layout = CreateTable(5);
        AddRow(layout, "Root", scanRootBox, BrowseFolderButton(scanRootBox));
        AddRow(layout, "Report", scanOutputBox, SaveFileButton(scanOutputBox, "Markdown report|*.md"));
        AddRow(layout, "Max items", scanMaxItemsBox);
        var runButton = new Button { Text = "Run Scan", AutoSize = true };
        runButton.Click += async (_, _) => await RunScanAsync();
        var traceButton = new Button { Text = "Trace Drivers (30s)", AutoSize = true };
        traceButton.Click += async (_, _) => await RunDriverTraceAsync();
        AddCommandRow(layout, runButton, traceButton);
        page.Controls.Add(layout);
        return page;
    }

    /// <summary>
    /// Names the driver holding the CPU at high IRQL. Needs administrator rights, so it says so plainly
    /// rather than returning an empty result.
    /// </summary>
    private async Task RunDriverTraceAsync()
    {
        try
        {
            Log("Tracing kernel DPC and ISR activity for 30s. Reproduce the lag now: move the mouse, drag a window.");

            var attribution = await Task.Run(() => new WindowsPlatformProbe()
                .CaptureDriverLatency(TimeSpan.FromSeconds(30), CancellationToken.None));

            if (attribution.EvidenceState == "requires-administrator")
            {
                Log("A kernel trace needs administrator rights. Restart OneLag as administrator and try again.");
                return;
            }

            var drivers = DriverClassifier.Significant(attribution);
            if (drivers.Count == 0)
            {
                Log("No driver accumulated meaningful high-IRQL time during the trace window.");
                return;
            }

            foreach (var driver in drivers.Take(5))
            {
                var subsystem = DriverClassifier.Classify(driver.Driver).Subsystem ?? "unclassified";
                Log($"Driver {driver.Driver}: {driver.TotalMilliseconds:N1} ms {driver.Kind.ToUpperInvariant()} total, worst {driver.MaxMilliseconds:N2} ms ({subsystem})");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    /// <summary>
    /// Runs every probe once and reports which ones measured live data. On startup it only updates the
    /// banner; run explicitly, it also writes each probe's result to the log.
    /// </summary>
    private async Task RunSelfTestAsync(bool startup = false)
    {
        try
        {
            if (!startup)
            {
                Log("Running self test...");
            }

            var report = await Task.Run(() => new SelfTestService(new WindowsPlatformProbe()).Run());
            UpdateReadiness(report);

            if (!startup)
            {
                foreach (var probe in report.Probes)
                {
                    var marker = probe.Status switch
                    {
                        ProbeStatus.Live => "OK",
                        ProbeStatus.Degraded => "WARN",
                        _ => "FAIL"
                    };
                    Log($"  [{marker}] {probe.Probe}: {probe.Detail}");
                }

                Log($"Evidence quality: {report.Quality.Grade} ({report.Quality.Score}/100)");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void UpdateReadiness(SelfTestReport report)
    {
        var live = report.Probes.Count(probe => probe.Status == ProbeStatus.Live);
        var total = report.Probes.Count;

        string banner;
        string detail;
        if (report.ReadyToDiagnose)
        {
            readinessColor = Color.FromArgb(30, 110, 60);
            banner = $"Ready to diagnose  —  {live}/{total} probes live, evidence {report.Quality.Grade}";
            detail = "This machine is instrumented well enough to record a watch session.";
        }
        else if (live > 0)
        {
            readinessColor = Color.FromArgb(150, 110, 20);
            banner = $"Partly instrumented  —  {live}/{total} probes live";
            detail = "Some evidence is missing. Run OneLag as administrator, then run the self test again. A session recorded now would be incomplete.";
        }
        else
        {
            readinessColor = Color.FromArgb(150, 40, 40);
            banner = "Not measuring anything  —  every probe is unavailable";
            detail = "A watch session recorded now would be empty. Run OneLag as administrator on a Windows 11 machine, then run the self test again.";
        }

        readinessBanner.BackColor = readinessColor;
        readinessLabel.BackColor = readinessColor;
        readinessLabel.ForeColor = Color.White;
        readinessLabel.Text = banner;
        selfTestSummaryLabel.Text = $"{banner}.{Environment.NewLine}{detail}";
    }

    /// <summary>
    /// Collects the raw log files off this machine into one bundle. Raw and unredacted by design; the bundle
    /// carries its own privacy notice.
    /// </summary>
    private async Task RunCollectAsync()
    {
        try
        {
            var scope = new LogCollectionScope(
                TimeSpan.FromHours((double)collectHoursBox.Value),
                IncludeWindowsTree: collectWindowsTreeBox.Checked,
                AllEventChannels: collectAllChannelsBox.Checked);

            var options = new LogCollectionOptions(
                collectOutputBox.Text,
                MaxTotalBytes: (long)collectMaxTotalBox.Value * 1024 * 1024,
                Zip: collectZipBox.Checked,
                Overwrite: collectOverwriteBox.Checked);

            Log($"Collecting logs (last {collectHoursBox.Value:N0}h of events). This can take a minute...");

            var result = await Task.Run(() =>
            {
                var items = new WindowsLogCollector().Enumerate(scope, DateTimeOffset.UtcNow);
                return new LogCollectionService().Collect(options, items, DateTimeOffset.UtcNow);
            });

            Log($"Collected {result.Collected:N0} file(s), {result.TotalBytes / 1024.0 / 1024.0:N1} MB.");
            if (result.Skipped > 0)
            {
                Log($"  Skipped {result.Skipped:N0} over-cap or oversize item(s); see manifest.json.");
            }

            if (result.Errors > 0)
            {
                Log($"  {result.Errors:N0} file(s) were locked or access-denied; see manifest.json.");
            }

            Log(result.ZipPath is not null
                ? $"Bundle: {result.ZipPath} (raw and unredacted; read PRIVACY.txt before sharing)"
                : $"Bundle directory: {result.Directory} (raw and unredacted; read PRIVACY.txt before sharing)");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RunCompare()
    {
        try
        {
            var sessions = new[] { compareSessionABox.Text, compareSessionBBox.Text }
                .Where(session => !string.IsNullOrWhiteSpace(session))
                .ToArray();

            if (sessions.Length < 2)
            {
                MessageBox.Show(this, "Choose two watch session directories to compare, for example a docked day and an undocked day.", "OneLag", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var service = new SessionComparisonService(watchService);
            var comparison = service.Compare(sessions);
            var report = service.BuildReport(comparison);

            var output = Path.GetFullPath(compareOutputBox.Text);
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Environment.CurrentDirectory);
            File.WriteAllText(output, report);
            reportPathBox.Text = output;

            foreach (var session in comparison.Sessions)
            {
                Log($"{session.Name}: {session.Samples:N0} samples, {session.Episodes:N0} episodes, max drift {session.MaxTimerDriftMilliseconds:N0} ms");
            }

            if (!string.IsNullOrWhiteSpace(comparison.Correlation.Conclusion))
            {
                Log(comparison.Correlation.Conclusion);
            }

            Log($"Comparison report written: {output}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    public async Task RunSelfTestFromTrayAsync()
    {
        ShowFromTray();
        await RunSelfTestAsync();
    }

    public async Task CollectLogsFromTrayAsync()
    {
        ShowFromTray();
        await RunCollectAsync();
    }

    private void ShowFromTray()
    {
        if (!Visible)
        {
            Show();
        }

        WindowState = FormWindowState.Normal;
        Activate();
    }

    private TabPage BuildSupportTab()
    {
        var page = new TabPage("Support");
        var layout = CreateTable(8);
        AddRow(layout, "Report", bundleReportPathBox, OpenFileButton(bundleReportPathBox, "OneLag reports|*.md;*.json|All files|*.*"));
        AddRow(layout, "Watch directory", bundleWatchDirectoryBox, BrowseFolderButton(bundleWatchDirectoryBox));
        AddRow(layout, "Bundle output", bundleOutputBox, BrowseFolderButton(bundleOutputBox));
        AddRow(layout, "Symptom note", bundleNoteBox);
        AddRow(layout, "OneDrive status", bundleOneDriveStatusBox);
        AddCommandRow(layout, bundleZipBox, bundleTracePlanBox, bundleOverwriteBox);
        var bundleButton = new Button { Text = "Create Bundle", AutoSize = true };
        bundleButton.Click += (_, _) => CreateSupportBundle();
        AddCommandRow(layout, bundleButton);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildWatchTab()
    {
        var page = new TabPage("Watch");
        var layout = CreateTable(7);
        AddRow(layout, "Directory", watchDirectoryBox, BrowseFolderButton(watchDirectoryBox));
        AddRow(layout, "Duration", watchDurationBox);
        AddRow(layout, "Interval seconds", watchIntervalBox);
        AddCommandRow(layout, watchStateLabel);

        var startButton = new Button { Text = "Start Watch", AutoSize = true };
        startButton.Click += async (_, _) => await StartWatchAsync();
        var stopButton = new Button { Text = "Stop Watch", AutoSize = true };
        stopButton.Click += (_, _) => RequestStopWatch();
        var markButton = new Button { Text = "Mark Lag Now", AutoSize = true };
        markButton.Click += (_, _) => MarkLagFromTray();
        var reportButton = new Button { Text = "Write Watch Report", AutoSize = true };
        reportButton.Click += (_, _) => WriteWatchReport();
        AddCommandRow(layout, startButton, stopButton, markButton, reportButton);

        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildReportTab()
    {
        var page = new TabPage("Reports");
        var layout = CreateTable(3);
        AddRow(layout, "Report", reportPathBox, OpenFileButton(reportPathBox, "OneLag reports|*.md;*.json|All files|*.*"));
        var viewButton = new Button { Text = "View Report", AutoSize = true };
        viewButton.Click += (_, _) => ViewReport();
        AddCommandRow(layout, viewButton);
        reportList.Columns.Add("Time", 210);
        reportList.Columns.Add("Kind", 130);
        reportList.Columns.Add("Summary", 360);
        reportList.Columns.Add("Evidence", 720);
        layout.Controls.Add(reportList, 0, 2);
        layout.SetColumnSpan(reportList, 3);
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildRemediationTab()
    {
        var page = new TabPage("Remediation");
        var layout = CreateTable(7);
        AddRow(layout, "Source", remediationSourceBox, BrowseFolderButton(remediationSourceBox));
        AddRow(layout, "Destination", remediationDestinationBox, BrowseFolderButton(remediationDestinationBox));
        AddRow(layout, "Plan output", remediationOutputBox, BrowseFolderButton(remediationOutputBox));
        AddCommandRow(layout, acknowledgeMoveBox);

        var planButton = new Button { Text = "Generate Move Plan", AutoSize = true };
        planButton.Click += (_, _) => GenerateMovePlan();
        var dryRunButton = new Button { Text = "Dry Run Move", AutoSize = true };
        dryRunButton.Click += (_, _) => RunMove(execute: false);
        var executeButton = new Button { Text = "Execute Move", AutoSize = true };
        executeButton.Click += (_, _) => RunMove(execute: true);
        var verifyButton = new Button { Text = "Verify", AutoSize = true };
        verifyButton.Click += (_, _) => VerifyMove();
        var rollbackButton = new Button { Text = "Rollback", AutoSize = true };
        rollbackButton.Click += (_, _) => RollbackMove();
        AddCommandRow(layout, planButton, dryRunButton, executeButton, verifyButton, rollbackButton);

        page.Controls.Add(layout);
        return page;
    }

    private async Task RunScanAsync()
    {
        try
        {
            var output = Path.GetFullPath(scanOutputBox.Text);
            var roots = string.IsNullOrWhiteSpace(scanRootBox.Text) ? Array.Empty<string>() : new[] { scanRootBox.Text };
            var report = await Task.Run(() =>
            {
                var platform = new WindowsPlatformProbe();
                var result = new ScanRunner(platform, new InventoryScanner(), new RiskEngine()).Run(
                    new ScanOptions(roots, output, "markdown", FullPaths: false, (int)scanMaxItemsBox.Value),
                    CancellationToken.None);
                var redactor = new Redactor(fullPaths: false, result.Roots.Select(root => root.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Environment.CurrentDirectory);
                File.WriteAllText(output, ReportWriter.ToMarkdown(result, redactor));
                return result;
            });

            reportPathBox.Text = output;
            bundleReportPathBox.Text = output;

            if (report.EvidenceQuality is { } quality)
            {
                Log($"Evidence quality: {quality.Grade} ({quality.Score}/100). {quality.Summary}");
            }

            foreach (var hypothesis in (report.Hypotheses ?? Array.Empty<Hypothesis>())
                .Where(candidate => candidate.Verdict is HypothesisVerdict.Possible or HypothesisVerdict.Likely or HypothesisVerdict.StronglySupported)
                .Take(3))
            {
                Log($"Cause: {hypothesis.Kind} - {hypothesis.Verdict} (score {hypothesis.Score}). Next: {hypothesis.NextStep}");
            }

            Log($"Scan report written: {output}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async Task StartWatchAsync()
    {
        if (watchTask is { IsCompleted: false })
        {
            Log("Watch is already running.");
            return;
        }

        try
        {
            var options = new WatchStartOptions(
                ParseDuration(watchDurationBox.Text),
                TimeSpan.FromSeconds((double)watchIntervalBox.Value),
                watchDirectoryBox.Text);
            watchCancellation = new CancellationTokenSource();
            watchTask = Task.Run(async () =>
            {
                var summary = await watchService.StartAsync(options, new WindowsPlatformProbe(), watchCancellation.Token);
                BeginInvoke(() =>
                {
                    Log($"Watch stopped. Samples: {summary.Samples:N0}");
                    RefreshWatchState();
                });
            });

            Log($"Watch started: {Path.GetFullPath(watchDirectoryBox.Text)}");
            RefreshWatchState();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void WriteWatchReport()
    {
        try
        {
            var report = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "onelag-watch-report.md");
            var fullPath = watchService.WriteReport(watchDirectoryBox.Text, report);
            reportPathBox.Text = fullPath;
            bundleReportPathBox.Text = fullPath;
            Log($"Watch report written: {fullPath}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshWatchState()
    {
        var state = watchService.ReadState(watchDirectoryBox.Text);
        watchStateLabel.Text = state is null
            ? "Watch state: no state file"
            : $"Watch state: {state.State}, samples {state.Samples:N0}, updated {state.UpdatedAt.LocalDateTime}";
    }

    private void ViewReport()
    {
        try
        {
            reportList.Items.Clear();
            var summary = new LocalReportViewService().Summarize(reportPathBox.Text);
            foreach (var item in summary.Timeline)
            {
                reportList.Items.Add(new ListViewItem(new[]
                {
                    item.Timestamp?.LocalDateTime.ToString("G") ?? string.Empty,
                    item.Kind,
                    item.Summary,
                    item.Evidence
                }));
            }

            Log($"Viewed {summary.Kind} report: {summary.SourcePath}");
            foreach (var fact in summary.KeyFacts.Take(6))
            {
                Log($"  {fact}");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void GenerateMovePlan()
    {
        try
        {
            var summary = MovePlanWriter.Write(
                new MovePlanOptions(remediationSourceBox.Text, remediationDestinationBox.Text, remediationOutputBox.Text),
                CancellationToken.None);
            Log($"Move plan written: {Path.GetFullPath(remediationOutputBox.Text)}");
            Log($"  Files {summary.FileCount:N0}, directories {summary.DirectoryCount:N0}, enough space {summary.DestinationHasEnoughSpace?.ToString() ?? "unknown"}");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RunMove(bool execute)
    {
        try
        {
            if (execute && !ConfirmMove("Execute move?"))
            {
                return;
            }

            var result = MovePlanExecutor.Move(
                new MoveExecutionOptions(remediationSourceBox.Text, remediationDestinationBox.Text, execute, acknowledgeMoveBox.Checked),
                CancellationToken.None);
            LogMoveResult(result);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RollbackMove()
    {
        try
        {
            if (!ConfirmMove("Rollback move?"))
            {
                return;
            }

            var result = MovePlanExecutor.Rollback(
                new MoveExecutionOptions(remediationSourceBox.Text, remediationDestinationBox.Text, Execute: true, acknowledgeMoveBox.Checked),
                CancellationToken.None);
            LogMoveResult(result);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void VerifyMove()
    {
        try
        {
            LogMoveResult(MovePlanExecutor.Verify(remediationSourceBox.Text, remediationDestinationBox.Text, 100_000, CancellationToken.None));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void CreateSupportBundle()
    {
        try
        {
            var reports = new List<string>();
            if (!string.IsNullOrWhiteSpace(bundleReportPathBox.Text))
            {
                reports.Add(bundleReportPathBox.Text);
            }
            else if (!string.IsNullOrWhiteSpace(reportPathBox.Text))
            {
                reports.Add(reportPathBox.Text);
            }

            var watchDirectory = string.IsNullOrWhiteSpace(bundleWatchDirectoryBox.Text) ? null : bundleWatchDirectoryBox.Text;
            var result = new SupportBundleWriter(versionProvider: _ => VersionText()).Write(new SupportBundleOptions(
                bundleOutputBox.Text,
                reports,
                watchDirectory,
                bundleTracePlanBox.Checked,
                bundleZipBox.Checked,
                bundleOverwriteBox.Checked,
                string.IsNullOrWhiteSpace(bundleNoteBox.Text) ? null : bundleNoteBox.Text,
                string.IsNullOrWhiteSpace(bundleOneDriveStatusBox.Text) ? null : bundleOneDriveStatusBox.Text));

            Log($"Support bundle written: {result.OutputDirectory}");
            if (result.ZipPath is not null)
            {
                Log($"Support bundle zip: {result.ZipPath}");
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private bool ConfirmMove(string title)
    {
        if (!acknowledgeMoveBox.Checked)
        {
            MessageBox.Show(this, "Check the acknowledgement before executing file moves.", "OneLag", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return MessageBox.Show(
            this,
            "This will move files on disk. Confirm OneDrive is paused and you have reviewed the plan.",
            title,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    private void LogMoveResult(MoveExecutionResult result)
    {
        Log($"{result.Operation}: {result.Message}");
        Log($"  Executed {result.Executed}; source exists {result.SourceExists}; destination exists {result.DestinationExists}; files {result.FileCount:N0}; directories {result.DirectoryCount:N0}");
    }

    private void Log(string message)
    {
        logBox.AppendText($"[{DateTimeOffset.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void ShowError(Exception ex)
    {
        Log($"Error: {ex.Message}");
        MessageBox.Show(this, ex.Message, "OneLag", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        ApplySystemTheme();
    }

    private void ApplySystemTheme()
    {
        ApplySystemColors(this);
        reportList.BackColor = SystemColors.Window;
        reportList.ForeColor = SystemColors.WindowText;
        logBox.BackColor = SystemColors.Window;
        logBox.ForeColor = SystemColors.WindowText;

        // The readiness banner is a semantic status indicator, so its colour survives the theme sweep.
        readinessBanner.BackColor = readinessColor;
        readinessLabel.BackColor = readinessColor;
        readinessLabel.ForeColor = Color.White;
    }

    private void ApplySystemColors(Control control)
    {
        if (ReferenceEquals(control, readinessBanner))
        {
            return;
        }

        if (control is TextBoxBase or ListView)
        {
            control.BackColor = SystemColors.Window;
            control.ForeColor = SystemColors.WindowText;
        }
        else
        {
            control.BackColor = SystemColors.Control;
            control.ForeColor = SystemColors.ControlText;
        }

        foreach (Control child in control.Controls)
        {
            ApplySystemColors(child);
        }
    }

    private static string VersionText()
    {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? typeof(Program).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        return $"OneLag {version}";
    }

    private static TableLayoutPanel CreateTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = rows,
            Padding = new Padding(12),
            AutoScroll = true,
            Tag = 0
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var i = 0; i < rows; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return table;
    }

    private static void AddRow(TableLayoutPanel table, string label, Control control, Control? button = null)
    {
        var row = NextRow(table);
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 0) }, 0, row);
        control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        table.Controls.Add(control, 1, row);
        if (button is not null)
        {
            table.Controls.Add(button, 2, row);
        }
    }

    private static void AddCommandRow(TableLayoutPanel table, params Control[] controls)
    {
        var row = NextRow(table);
        var panel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        panel.Controls.AddRange(controls);
        table.Controls.Add(panel, 1, row);
        table.SetColumnSpan(panel, 2);
    }

    private static int NextRow(TableLayoutPanel table)
    {
        var row = table.Tag is int value ? value : 0;
        table.Tag = row + 1;
        return row;
    }

    private static Button BrowseFolderButton(TextBox target)
    {
        var button = new Button { Text = "...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog();
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                target.Text = dialog.SelectedPath;
            }
        };
        return button;
    }

    private static Button SaveFileButton(TextBox target, string filter)
    {
        var button = new Button { Text = "...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = filter };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                target.Text = dialog.FileName;
            }
        };
        return button;
    }

    private static Button OpenFileButton(TextBox target, string filter)
    {
        var button = new Button { Text = "...", AutoSize = true };
        button.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = filter };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                target.Text = dialog.FileName;
            }
        };
        return button;
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
}
