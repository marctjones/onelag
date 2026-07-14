namespace OneLag.Core;

/// <summary>
/// Thresholds for calling a freeze without the user's help. Every number here is a judgement call, so each
/// one carries the reasoning that produced it: a threshold with no stated justification is indistinguishable
/// from a guess, and this one decides whether the machine gets interrupted for a deep capture.
/// </summary>
public sealed record FreezeDetectorOptions
{
    /// <summary>
    /// A 1s timer that fires 1.5s late means the watcher itself was denied the CPU for half a second. That is
    /// already a stall the user would feel, but a single late tick is ordinary scheduler noise (a GC, a page
    /// fault, a busy core), so drift alone is not enough — see <see cref="SustainedDriftTicks"/>.
    /// </summary>
    public double SustainedDriftMilliseconds { get; init; } = 500;

    /// <summary>
    /// Requiring the drift to persist across consecutive ticks is what separates a stall from jitter. Noise is
    /// uncorrelated between ticks; a machine that is actually wedged stays wedged, so it misses the next
    /// deadline too. Two ticks is the minimum that can distinguish the two at all.
    /// </summary>
    public int SustainedDriftTicks { get; init; } = 2;

    /// <summary>
    /// A single tick this late needs no corroboration. Losing four seconds of wall clock on a 1s timer is not
    /// jitter under any load the scheduler produces on its own, and waiting for a second confirming tick would
    /// mean waiting out the freeze we are trying to capture.
    /// </summary>
    public double SevereDriftMilliseconds { get; init; } = 4_000;

    /// <summary>
    /// Windows itself reporting the shell window as hung is a direct measurement of the symptom the user
    /// describes, so it stands alone: the shell not pumping messages IS the freeze, whatever the timer says.
    /// </summary>
    public bool TriggerOnHungShell { get; init; } = true;

    /// <summary>
    /// The shell's message pump answering a ping in over a second means every click is already queueing.
    /// </summary>
    public double ShellPumpLatencyMilliseconds { get; init; } = 1_000;

    /// <summary>
    /// Under this much commit headroom the memory manager is trimming working sets to stay alive, which is the
    /// hard-fault storm the user experiences as "everything stopped". 512 MB is a floor in absolute bytes on
    /// purpose: a percentage of the commit limit is meaningless when the limit itself is being grown.
    /// </summary>
    public long CommitHeadroomFloorBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>
    /// Sustained hard faults at this rate mean the machine is servicing memory from disk rather than running.
    /// Below a thousand a second this is normal demand paging (every process start pages in its image).
    /// </summary>
    public double HardFaultsPerSecond { get; init; } = 1_000;

    /// <summary>
    /// A freeze lasts seconds to minutes and each deep capture costs real work on a machine that is already
    /// starved, so one capture per two minutes is the ceiling. Detection itself is not debounced by this — the
    /// marker is still written — only the expensive capture is.
    /// </summary>
    public TimeSpan DeepCaptureCooldown { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A badly broken machine could freeze all day. The cap stops an unattended run from filling the disk with
    /// captures; the run reports every detection it dropped, because a silent cap reads as "this only happened
    /// twenty times", which would be a lie.
    /// </summary>
    public int MaxDeepCaptures { get; init; } = 20;
}

/// <summary>
/// Why the detector fired, and what the deep capture did about it.
/// </summary>
public sealed record FreezeDetection(
    bool Triggered,
    bool ShouldCapture,
    string? Trigger,
    string? Note,
    bool SkippedByCooldown,
    bool SkippedByCap);

/// <summary>
/// Decides whether the machine is freezing right now, from the watch loop's own samples.
///
/// The tool previously required the user to act during a freeze — `watch mark`, or the `freeze` command — and
/// that is exactly the moment the machine will not let him act. The watcher has to detect the freeze itself.
///
/// The primary signal is the watcher's own starvation: the loop asks to sleep one second, and if it wakes four
/// seconds later the machine stalled. That measurement survives the very condition it is measuring, which is
/// the property no user-driven trigger has.
///
/// Detection is edge-triggered: it fires when the machine crosses into a freeze, not on every tick it stays
/// there, so a thirty-second freeze produces one episode rather than thirty. State is held here rather than in
/// the loop so the whole decision is testable without a clock.
/// </summary>
public sealed class FreezeDetector
{
    /// <summary>
    /// Tokens embedded in the auto-marker note. The watch report is rebuilt from the recording directory long
    /// after the run has exited, so a dropped capture can only be reported if the drop was written down at the
    /// time. Shared as constants so the writer and the reader cannot drift apart.
    /// </summary>
    public const string CapSkipToken = "[deep-capture-skipped:cap]";

    public const string CooldownSkipToken = "[deep-capture-skipped:cooldown]";

    public const string AutoMarkerSource = "auto-detected";

    private readonly FreezeDetectorOptions options;
    private int consecutiveDriftTicks;
    private bool inFreeze;
    private DateTimeOffset? lastCaptureAt;

    public FreezeDetector(FreezeDetectorOptions? options = null)
    {
        this.options = options ?? new FreezeDetectorOptions();
    }

    /// <summary>Deep captures actually taken. Bounded by <see cref="FreezeDetectorOptions.MaxDeepCaptures"/>.</summary>
    public int DeepCaptures { get; private set; }

    /// <summary>Detections that were denied a deep capture because the per-run cap was reached.</summary>
    public int DeepCapturesDroppedByCap { get; private set; }

    public FreezeDetection Evaluate(WatchSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var trigger = FindTrigger(sample);
        if (trigger is null)
        {
            // The machine has to come back before another freeze can be declared; otherwise a long stall would
            // re-arm on every tick and the "one episode per freeze" property would be lost.
            inFreeze = false;
            return new FreezeDetection(false, false, null, null, false, false);
        }

        if (inFreeze)
        {
            // Still the same freeze. Already marked, already (maybe) captured.
            return new FreezeDetection(false, false, trigger.Value.Kind, null, false, false);
        }

        inFreeze = true;

        var skippedByCap = DeepCaptures >= options.MaxDeepCaptures;
        var skippedByCooldown = !skippedByCap
            && lastCaptureAt is { } last
            && sample.Timestamp - last < options.DeepCaptureCooldown;
        var shouldCapture = !skippedByCap && !skippedByCooldown;

        if (shouldCapture)
        {
            DeepCaptures++;
            lastCaptureAt = sample.Timestamp;
        }
        else if (skippedByCap)
        {
            DeepCapturesDroppedByCap++;
        }

        var note = trigger.Value.Note;
        if (skippedByCap)
        {
            note += $" No deep capture: the per-run cap of {options.MaxDeepCaptures} was already reached. {CapSkipToken}";
        }
        else if (skippedByCooldown)
        {
            note += $" No deep capture: within {options.DeepCaptureCooldown.TotalMinutes:N0} min of the previous one. {CooldownSkipToken}";
        }

        return new FreezeDetection(true, shouldCapture, trigger.Value.Kind, note, skippedByCooldown, skippedByCap);
    }

    /// <summary>
    /// Signals are checked in order of how directly they measure the user's experience. Timer drift is first
    /// because it is the only one that keeps working when the machine is too wedged to answer anything else.
    /// </summary>
    private (string Kind, string Note)? FindTrigger(WatchSample sample)
    {
        if (sample.TimerDriftMilliseconds >= options.SevereDriftMilliseconds)
        {
            consecutiveDriftTicks++;
            return (
                "timer-drift-severe",
                $"Timer drift {sample.TimerDriftMilliseconds:N0} ms: the watcher asked for a short sleep and the machine did not run it for {sample.TimerDriftMilliseconds / 1000:N1} s.");
        }

        if (sample.TimerDriftMilliseconds >= options.SustainedDriftMilliseconds)
        {
            consecutiveDriftTicks++;
            if (consecutiveDriftTicks >= options.SustainedDriftTicks)
            {
                return (
                    "timer-drift-sustained",
                    $"Timer drift {sample.TimerDriftMilliseconds:N0} ms sustained across {consecutiveDriftTicks} consecutive samples: this is a stall, not scheduler jitter.");
            }
        }
        else
        {
            consecutiveDriftTicks = 0;
        }

        if (options.TriggerOnHungShell && sample.ShellResponsiveness?.ShellWindowHung == true)
        {
            return (
                "shell-hung",
                $"Windows reported the Explorer shell window as hung ({sample.ShellResponsiveness.HungTopLevelWindows:N0} hung top-level window(s)): the shell is not pumping messages.");
        }

        if (sample.ShellResponsiveness?.ShellPumpLatencyMilliseconds is { } pump && pump >= options.ShellPumpLatencyMilliseconds)
        {
            return (
                "shell-pump-latency",
                $"The Explorer message pump took {pump:N0} ms to answer: every click is already queueing.");
        }

        if (sample.Memory?.CommitHeadroomBytes is { } headroom && headroom <= options.CommitHeadroomFloorBytes)
        {
            return (
                "commit-headroom-exhausted",
                $"Commit headroom is down to {headroom / (1024.0 * 1024):N0} MB: the memory manager is trimming working sets to keep the machine alive.");
        }

        var hardFaults = PressureClassifier.Value(
            sample.SystemPressure.Signals ?? Array.Empty<PerformanceSignal>(),
            "memory-page-reads-per-second");
        if (hardFaults >= options.HardFaultsPerSecond)
        {
            return (
                "hard-fault-storm",
                $"Hard faults at {hardFaults:N0}/s: the machine is servicing memory from disk rather than running code.");
        }

        return null;
    }
}
