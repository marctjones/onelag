namespace OneLag.Core;

public static class EscalationPlanWriter
{
    public static IReadOnlyList<string> WriteTracePlan(string outputDirectory)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        var files = new[]
        {
            WriteFile(directory, "README.md", BuildReadme()),
            WriteFile(directory, "Start-OneLagWprTrace.ps1", BuildStartWprScript()),
            WriteFile(directory, "Stop-OneLagWprTrace.ps1", BuildStopWprScript()),
            WriteFile(directory, "Cancel-OneLagWprTrace.ps1", BuildCancelWprScript()),
            WriteFile(directory, "ProcMon-OneLag-Filters.md", BuildProcMonGuide())
        };

        return files;
    }

    private static string WriteFile(string directory, string fileName, string contents)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string BuildReadme()
    {
        return """
        # OneLag Trace Escalation Plan

        Use this only when the normal OneLag report shows responsiveness pressure but cannot prove the root cause from lightweight user-mode evidence.

        ## Safety

        - Do not store traces inside OneDrive.
        - Do not upload ETL, PML, or CSV traces without reviewing privacy impact.
        - WPR and ProcMon traces can contain process names, file paths, registry paths, command lines, usernames, and application activity.
        - Keep captures short. Start the trace, reproduce the lag, stop the trace.
        - Cancel the trace if the machine becomes unstable.

        ## Recommended order

        1. Run `Start-OneLagWprTrace.ps1` in an elevated PowerShell window.
        2. Reproduce the keyboard, mouse, Explorer, or app freeze.
        3. Run `Stop-OneLagWprTrace.ps1` as soon as the lag happens.
        4. Open the ETL in Windows Performance Analyzer.
        5. Use ProcMon only when the WPR result points at file system or registry activity that needs per-operation detail.

        ## What WPR helps prove

        WPR/WPA is the right escalation path for CPU scheduling delay, disk I/O, file I/O, hard faults, ready-thread delay, context switches, DPC/ISR suspicion, driver involvement, and Explorer or shell stalls.

        ## What ProcMon helps prove

        ProcMon is useful for high-volume file, registry, process, and thread activity. It is not a low-overhead all-day recorder; use focused filters and short captures.
        """ + Environment.NewLine;
    }

    private static string BuildStartWprScript()
    {
        return """
        #requires -RunAsAdministrator
        $ErrorActionPreference = "Stop"

        $traceRoot = Join-Path $env:LOCALAPPDATA "OneLag\traces"
        New-Item -ItemType Directory -Force -Path $traceRoot | Out-Null

        Write-Host "Starting WPR GeneralProfile.light in memory mode."
        Write-Host "Trace output directory: $traceRoot"
        Write-Host "Reproduce the lag, then run Stop-OneLagWprTrace.ps1."

        wpr -status | Out-Host
        wpr -start GeneralProfile.light
        wpr -marker "OneLag trace started"
        wpr -status
        """ + Environment.NewLine;
    }

    private static string BuildStopWprScript()
    {
        return """
        #requires -RunAsAdministrator
        $ErrorActionPreference = "Stop"

        $traceRoot = Join-Path $env:LOCALAPPDATA "OneLag\traces"
        New-Item -ItemType Directory -Force -Path $traceRoot | Out-Null
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $etl = Join-Path $traceRoot "onelag-wpr-$stamp.etl"

        Write-Host "Stopping WPR and saving: $etl"
        wpr -marker "OneLag lag reproduced before stop"
        wpr -stop $etl "OneLag responsiveness investigation" -skipPdbGen -compress
        Write-Host "Saved WPR trace: $etl"
        Write-Host "Review locally in Windows Performance Analyzer before sharing."
        """ + Environment.NewLine;
    }

    private static string BuildCancelWprScript()
    {
        return """
        #requires -RunAsAdministrator
        $ErrorActionPreference = "Continue"

        Write-Host "Canceling active WPR recording without saving."
        wpr -cancel
        """ + Environment.NewLine;
    }

    private static string BuildProcMonGuide()
    {
        return """
        # ProcMon Filter Guide For OneLag Investigations

        Use ProcMon after WPR or the OneLag report points toward file-system, registry, process, or thread churn. Keep captures short and save the backing file outside OneDrive.

        ## Capture setup

        - Start ProcMon as administrator.
        - Store the backing file under `%LOCALAPPDATA%\OneLag\traces`.
        - Enable `Drop Filtered Events` before reproducing the problem.
        - Stop capture immediately after the lag episode.
        - Save as native PML first. Export CSV only after narrowing filters.

        ## Starting filters

        Add includes for likely actors:

        - `Process Name is OneDrive.exe`
        - `Process Name is explorer.exe`
        - `Process Name is SearchIndexer.exe`
        - `Process Name is MsMpEng.exe`
        - `Path contains \OneDrive`
        - `Path contains \SharePoint`

        Add optional app filters only while reproducing a specific symptom:

        - `Process Name is WINWORD.EXE`
        - `Process Name is EXCEL.EXE`
        - `Process Name is POWERPNT.EXE`
        - `Process Name contains Webex`
        - `Process Name contains Teams`

        ## Signals to look for

        - Repeated `NAME NOT FOUND`, `PATH NOT FOUND`, `ACCESS DENIED`, or `SHARING VIOLATION` in a synced path.
        - Very high operation volume under a single synced folder.
        - Explorer or Office waiting on OneDrive overlay, hydration, or file-lock paths.
        - Defender, SearchIndexer, or build tools repeatedly touching the same synced tree.

        ## Privacy

        ProcMon traces can include full local paths, registry paths, command lines, usernames, and filenames. Do not share the raw PML unless you have reviewed it.
        """ + Environment.NewLine;
    }
}
