using System.ComponentModel;
using System.Diagnostics;

namespace OneLag.Core;

/// <summary>
/// One shell process eligible for a memory-reclaim kill, annotated with what it is currently holding.
///
/// This is a plan, not an action: producing one never kills anything. <see cref="MemoryReclaimService.Plan"/>
/// only returns a candidate for a process that is actually bloated, so a dry run can say "StartMenuExperienceHost
/// is holding 2.7 GB; killing it will reclaim that" rather than recommending a restart for a process sitting at
/// its normal footprint.
/// </summary>
public sealed record MemoryReclaimCandidate(
    string ProcessName,
    int? ProcessId,
    long PrivateBytes,
    string Description,
    string Rationale,
    bool AutoRestarts);

/// <summary>
/// The outcome of attempting to kill one candidate.
/// </summary>
public sealed record MemoryReclaimResult(
    string ProcessName,
    int? ProcessId,
    long ReclaimedBytes,
    bool Killed,
    string Message);

/// <summary>
/// Kills known-safe, self-restarting shell processes to reclaim memory they should not be holding.
///
/// This is the rare fix OneLag can automate. On the machine that motivated this project,
/// StartMenuExperienceHost.exe was holding 2.7 GB against an expected footprint of roughly 100 MB. Killing it
/// costs nothing: Windows relaunches it the instant the Start menu is opened again, it holds no documents or
/// session state, and it needs no elevation. explorer.exe is the same shape — a shell process the OS restarts
/// on its own — so both are on the allowlist and nothing else is. Every other process on a user's machine might
/// be holding unsaved work, so this service never reaches for one.
/// </summary>
public sealed class MemoryReclaimService
{
    /// <summary>
    /// Below this, a kill is not worth the second of shell flicker it costs. 500 MB is well above
    /// StartMenuExperienceHost's normal ~100 MB footprint and explorer's normal range, so it only fires once a
    /// process has actually run away.
    /// </summary>
    private const long BloatThresholdBytes = 500L * 1024 * 1024;

    /// <summary>
    /// The hard-coded, closed set of processes this service will ever recommend or perform a kill against.
    ///
    /// This is a safety boundary, not a convenience default: OneLag must never accept an arbitrary process name
    /// from the command line, because that is the difference between a diagnostic tool and a process killer.
    /// Extending what can be killed means editing this file and this list, not passing a flag.
    /// </summary>
    private static readonly IReadOnlyList<AllowedProcess> Allowlist = new[]
    {
        new AllowedProcess(
            "StartMenuExperienceHost",
            "Renders the Start menu and its search results.",
            "Windows relaunches it the instant the Start menu is opened again. It holds no documents, no open " +
            "windows, and no session state, so killing it loses nothing. Its memory has been observed growing " +
            "unbounded over long uptimes to many times its expected footprint (roughly 100 MB) while contributing " +
            "nothing to responsiveness in between; killing it returns that memory immediately.",
            AutoRestarts: true),
        new AllowedProcess(
            "explorer",
            "The desktop, taskbar, and file-manager shell process.",
            "Killing explorer.exe does not close any running application or document; only the desktop and " +
            "taskbar disappear, for roughly a second. Windows 11 relaunches it automatically, but this service " +
            "also restarts it explicitly afterward so the behavior is the same on Windows 10 and in the rare case " +
            "the automatic relaunch does not fire. Like the Start menu host, its memory can grow unbounded over a " +
            "long uptime, especially with many shell extensions loaded.",
            AutoRestarts: true)
    };

    /// <summary>
    /// Returns the subset of the allowlist that is both present and actually bloated right now, annotated with
    /// its current private bytes so a dry run can quote a real figure instead of a generic warning.
    /// </summary>
    public IReadOnlyList<MemoryReclaimCandidate> Plan(MemoryPressureDetail memory)
    {
        ArgumentNullException.ThrowIfNull(memory);

        var candidates = new List<MemoryReclaimCandidate>();
        foreach (var allowed in Allowlist)
        {
            var sample = memory.TopCommitProcesses
                .Where(process => string.Equals(process.Name, allowed.ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(process => process.PrivateBytes)
                .FirstOrDefault();

            if (sample is null || sample.PrivateBytes < BloatThresholdBytes)
            {
                // Absent, or sitting at a normal footprint: recommending a shell restart to reclaim a few tens
                // of MB is not a trade worth making, so this process is left out of the plan entirely.
                continue;
            }

            candidates.Add(new MemoryReclaimCandidate(
                allowed.ProcessName,
                sample.ProcessId,
                sample.PrivateBytes,
                allowed.Description,
                allowed.Rationale,
                allowed.AutoRestarts));
        }

        return candidates;
    }

    /// <summary>
    /// Kills exactly the candidates given, by the process id each was measured at, and restarts explorer
    /// explicitly if it was one of them.
    ///
    /// Every candidate is re-checked against the hard-coded allowlist before anything is touched, regardless of
    /// where the list came from — this is the one place in OneLag that terminates a process, and it must not
    /// trust its caller. Matching by process id rather than by name also means a stale plan (the process already
    /// exited, or was replaced by a new instance) is skipped rather than guessed at.
    /// </summary>
    public IReadOnlyList<MemoryReclaimResult> Execute(IReadOnlyList<MemoryReclaimCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var results = new List<MemoryReclaimResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var allowed = Allowlist.FirstOrDefault(process =>
                string.Equals(process.ProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase));

            if (allowed is null)
            {
                results.Add(new MemoryReclaimResult(candidate.ProcessName, candidate.ProcessId, 0, false,
                    "Refused: not on the memory-reclaim allowlist."));
                continue;
            }

            if (candidate.ProcessId is not { } processId)
            {
                results.Add(new MemoryReclaimResult(candidate.ProcessName, null, 0, false,
                    "No process id was recorded for this candidate; skipped rather than guessing which instance to kill."));
                continue;
            }

            results.Add(KillOne(allowed, candidate, processId));
        }

        return results;
    }

    private static MemoryReclaimResult KillOne(AllowedProcess allowed, MemoryReclaimCandidate candidate, int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, allowed.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return new MemoryReclaimResult(candidate.ProcessName, processId, 0, false,
                    $"Process id {processId} is no longer '{allowed.ProcessName}'; skipped rather than killing the wrong process.");
            }

            process.Kill(entireProcessTree: false);
            process.WaitForExit(5000);

            if (string.Equals(allowed.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                RestartExplorer();
            }

            return new MemoryReclaimResult(candidate.ProcessName, processId, candidate.PrivateBytes, true,
                "Killed. Windows will relaunch it automatically.");
        }
        catch (ArgumentException)
        {
            return new MemoryReclaimResult(candidate.ProcessName, processId, 0, false, "Process was not running.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return new MemoryReclaimResult(candidate.ProcessName, processId, 0, false, $"Could not kill: {ex.Message}");
        }
    }

    private static void RestartExplorer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Best effort. Windows normally relaunches explorer.exe on its own; failing to start it a second
            // time here should not be reported as the kill itself having failed.
        }
    }

    private sealed record AllowedProcess(string ProcessName, string Description, string Rationale, bool AutoRestarts);
}
