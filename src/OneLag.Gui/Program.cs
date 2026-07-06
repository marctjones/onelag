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
        menu.Items.Add("Start Watch", null, async (_, _) => await mainForm.StartWatchFromTrayAsync());
        menu.Items.Add("Stop Watch", null, (_, _) => mainForm.RequestStopWatch());
        menu.Items.Add("Mark Lag Now", null, (_, _) => mainForm.MarkLagFromTray());
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
        tabs.TabPages.Add(BuildScanTab());
        tabs.TabPages.Add(BuildWatchTab());
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
        Controls.Add(split);
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
        AddCommandRow(layout, runButton);
        page.Controls.Add(layout);
        return page;
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
            await Task.Run(() =>
            {
                var platform = new WindowsPlatformProbe();
                var report = new ScanRunner(platform, new InventoryScanner(), new RiskEngine()).Run(
                    new ScanOptions(roots, output, "markdown", FullPaths: false, (int)scanMaxItemsBox.Value),
                    CancellationToken.None);
                var redactor = new Redactor(fullPaths: false, report.Roots.Select(root => root.Path));
                Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Environment.CurrentDirectory);
                File.WriteAllText(output, ReportWriter.ToMarkdown(report, redactor));
            });

            reportPathBox.Text = output;
            bundleReportPathBox.Text = output;
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
    }

    private static void ApplySystemColors(Control control)
    {
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
