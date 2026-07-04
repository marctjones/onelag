using System.Diagnostics;

namespace OneLag.Cli.Tests;

public sealed class CliSmokeTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-cli-tests", Guid.NewGuid().ToString("N"));

    public CliSmokeTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public async Task VersionCommandPrintsVersion()
    {
        var result = await RunCli("version");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("OneLag", result.StandardOutput);
    }

    [Fact]
    public async Task ScanCommandWritesRedactedReportForSimpleTree()
    {
        var scanRoot = Path.Combine(tempRoot, "OneDrive");
        Directory.CreateDirectory(scanRoot);
        File.WriteAllText(Path.Combine(scanRoot, "document.txt"), "content");
        var reportPath = Path.Combine(tempRoot, "report.md");

        var result = await RunCli("scan", "--root", scanRoot, "--output", reportPath, "--max-items", "1000");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(reportPath));
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("Diagnosis:", report);
        Assert.Contains("Top-level item", report);
        Assert.Contains("<root:1>", report);
        Assert.DoesNotContain(scanRoot, report);
    }

    [Fact]
    public async Task ScanCommandReturnsWarningStatusForHighRiskDevelopmentTree()
    {
        var scanRoot = Path.Combine(tempRoot, "OneDrive");
        var packageRoot = Path.Combine(scanRoot, "project", "node_modules");
        Directory.CreateDirectory(packageRoot);
        File.WriteAllText(Path.Combine(packageRoot, "package.txt"), "content");
        var reportPath = Path.Combine(tempRoot, "risk-report.md");

        var result = await RunCli("scan", "--root", scanRoot, "--output", reportPath, "--max-items", "1000");

        Assert.Equal(1, result.ExitCode);
        var report = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("High-risk directory", report);
        Assert.Contains("node_modules", report);
    }

    [Fact]
    public async Task WatchCommandsCreateMarkerAndReport()
    {
        var watchRoot = Path.Combine(tempRoot, "watch");

        var start = await RunCli("watch", "start", "--duration", "1s", "--interval", "1s", "--output", watchRoot);
        Assert.Equal(0, start.ExitCode);
        Assert.True(File.Exists(Path.Combine(watchRoot, "samples.ndjson")));

        var mark = await RunCli("watch", "mark", "--output", watchRoot, "--note", "test");
        Assert.Equal(0, mark.ExitCode);

        var reportPath = Path.Combine(tempRoot, "watch-report.md");
        var report = await RunCli("watch", "report", "--output", watchRoot, "--report", reportPath);

        Assert.Equal(0, report.ExitCode);
        Assert.True(File.Exists(reportPath));
        Assert.Contains("OneLag Watch Report", await File.ReadAllTextAsync(reportPath));
    }

    [Fact]
    public async Task RepairResetOneDriveDryRunDoesNotRequireWindowsMutation()
    {
        var result = await RunCli("repair", "reset-onedrive");

        Assert.True(result.ExitCode is 0 or 1);
        Assert.Contains("OneDrive reset plan", result.StandardOutput);
        Assert.Contains("Internal sync database parsed: False", result.StandardOutput);
        Assert.Contains("Safety:", result.StandardOutput);
    }

    [Fact]
    public async Task SupportTracePlanWritesEscalationRunbooks()
    {
        var output = Path.Combine(tempRoot, "trace-plan");

        var result = await RunCli("support", "trace-plan", "--output", output);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "README.md")));
        Assert.True(File.Exists(Path.Combine(output, "Start-OneLagWprTrace.ps1")));
        Assert.True(File.Exists(Path.Combine(output, "Stop-OneLagWprTrace.ps1")));
        Assert.True(File.Exists(Path.Combine(output, "Cancel-OneLagWprTrace.ps1")));
        Assert.True(File.Exists(Path.Combine(output, "ProcMon-OneLag-Filters.md")));
        Assert.Contains("Trace escalation plan", result.StandardOutput);
        Assert.Contains("wpr -start GeneralProfile.light", await File.ReadAllTextAsync(Path.Combine(output, "Start-OneLagWprTrace.ps1")));
        Assert.Contains("Drop Filtered Events", await File.ReadAllTextAsync(Path.Combine(output, "ProcMon-OneLag-Filters.md")));
    }

    [Fact]
    public async Task RemediateMovePlanWritesDryRunScripts()
    {
        var source = Path.Combine(tempRoot, "OneDrive", "project");
        var destination = Path.Combine(tempRoot, "LocalDev", "project");
        var output = Path.Combine(tempRoot, "move-plan");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "content");

        var result = await RunCli(
            "remediate",
            "move-plan",
            "--source",
            source,
            "--destination",
            destination,
            "--output",
            output);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(output, "move-plan.md")));
        Assert.True(File.Exists(Path.Combine(output, "move-plan.json")));
        Assert.True(File.Exists(Path.Combine(output, "Move-OneLagItems.ps1")));
        Assert.True(File.Exists(Path.Combine(output, "Rollback-OneLagMove.ps1")));
        Assert.True(File.Exists(Path.Combine(output, "Verify-OneLagMove.ps1")));
        Assert.Contains("Move plan:", result.StandardOutput);
        Assert.Contains("-Execute -IUnderstandMovesFiles", await File.ReadAllTextAsync(Path.Combine(output, "Move-OneLagItems.ps1")));
        Assert.Contains("Rollback", await File.ReadAllTextAsync(Path.Combine(output, "move-plan.md")));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<CliResult> RunCli(params string[] arguments)
    {
        var repoRoot = FindRepoRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        var projectPath = Path.Combine(repoRoot, "src", "OneLag.Cli", "OneLag.Cli.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(configuration);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"Command timed out: {string.Join(' ', arguments)}");
        }

        return new CliResult(process.ExitCode, await stdout, await stderr);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OneLag.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OneLag.slnx.");
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
