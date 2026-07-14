namespace OneLag.Core;

public sealed record WatchPreflightDecision(
    bool CanProceed,
    bool Acknowledged,
    CollectorReadinessReport Readiness,
    IReadOnlyList<string> Lines)
{
    public IReadOnlyList<CollectorReadiness> Blocking => Readiness.Blocking;

    public string Message => string.Join(Environment.NewLine, Lines);
}

/// <summary>
/// The gate in front of an 8-hour unattended recording.
///
/// A watch session recorded with degraded collectors produces an authoritative-looking report containing
/// nothing, and it costs a working day to find that out. That day is the entire reason this class exists: when
/// the collectors that a leak hunt actually depends on are broken, the correct behaviour is not to warn and
/// record anyway — it is to refuse to start, say exactly what would have been missing, and say exactly how to
/// fix it. A run that silently collects nothing is worse than no run at all.
///
/// The refusal can be overridden with an explicit acknowledgement flag, mirroring the convention used by
/// `repair reset-onedrive` and `remediate move`: the user may knowingly record a partial session (the memory
/// series alone is still worth something), but not accidentally.
///
/// This is deliberately a pure decision over a readiness report rather than a prompt. There is no console read
/// anywhere in this path, so a CI job or a non-interactive shell can never hang on it: the flag is honoured, or
/// the command exits.
///
/// NOTE THE ASYMMETRY WITH `onelag freeze`, which warns and continues. It is deliberate, not an inconsistency.
/// A freeze capture is opportunistic and the user is mid-episode: partial evidence captured now beats perfect
/// evidence captured never, because the freeze will be over in seconds and cannot be summoned back. A watch
/// session is the opposite — it is about to consume a day, and it can simply be started again in two minutes
/// from an elevated terminal.
/// </summary>
public static class WatchPreflight
{
    public const string AcknowledgementFlag = "i-understand-collectors-are-degraded";

    public static WatchPreflightDecision Evaluate(CollectorReadinessReport readiness, bool acknowledged)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        var blocking = readiness.Blocking;
        var advisory = readiness.Degraded.Where(collector => !collector.BlocksLeakHunt).ToArray();
        var lines = new List<string>();

        if (blocking.Count == 0 && advisory.Length == 0)
        {
            lines.Add("Collector check: every collector this run depends on is working.");
            return new WatchPreflightDecision(CanProceed: true, acknowledged, readiness, lines);
        }

        lines.Add("========================================================================");
        lines.Add("COLLECTORS ARE DEGRADED. READ THIS BEFORE YOU WALK AWAY FROM THE MACHINE.");
        lines.Add("========================================================================");
        lines.Add(string.Empty);
        lines.Add(readiness.ElevationLine);
        lines.Add(string.Empty);

        foreach (var collector in blocking)
        {
            lines.Add($"  [MISSING] {collector.Describe()}");
            lines.Add(string.Empty);
        }

        foreach (var collector in advisory)
        {
            lines.Add($"  [PARTIAL] {collector.Describe()}");
            lines.Add(string.Empty);
        }

        if (blocking.Count == 0)
        {
            // Nothing here guts the run. Losing shell extensions or the file-system context thins the report;
            // it cannot corrupt the memory series, which is what an all-day leak hunt is for.
            lines.Add("None of the above stops this run from being worth recording. Starting the watch.");
            return new WatchPreflightDecision(CanProceed: true, acknowledged, readiness, lines);
        }

        if (acknowledged)
        {
            lines.Add($"Proceeding anyway because --{AcknowledgementFlag} was given.");
            lines.Add("The report from this session will NOT be able to answer the questions listed above. Do not read its silence as a clean bill of health.");
            return new WatchPreflightDecision(CanProceed: true, acknowledged, readiness, lines);
        }

        lines.Add("REFUSING TO START.");
        lines.Add(string.Empty);
        lines.Add("An 8-hour run that silently collects nothing is worse than no run at all: it produces an authoritative-looking report containing nothing, and it costs you the day to find that out.");
        lines.Add(string.Empty);
        lines.Add("Close this terminal, open a new one as administrator, and run the same command again. It takes two minutes and it is the difference between a report that names your leak and a report that cannot.");
        lines.Add(string.Empty);
        lines.Add($"If you have read the above and want a partial session anyway, re-run with --{AcknowledgementFlag}.");

        return new WatchPreflightDecision(CanProceed: false, acknowledged, readiness, lines);
    }
}
