namespace OneLag.Core;

public sealed record HypothesisInputs(
    IReadOnlyList<InventorySummary> Inventories,
    TelemetrySnapshot Telemetry,
    SystemPressureSnapshot Pressure,
    OneDriveClientHealthSnapshot ClientHealth,
    IReadOnlyList<EventLogSummary> EventLogs,
    HostContext? HostContext,
    ShellResponsiveness? Shell,
    EvidenceQuality EvidenceQuality,
    double? MaxTimerDriftMilliseconds = null,
    DriverLatencyAttribution? DriverLatency = null);

/// <summary>
/// Ranks candidate causes of desktop lag.
///
/// The previous design could only ask "is OneDrive to blame?", so every capture either implicated OneDrive
/// or shrugged. Non-OneDrive causes had no collectors and therefore could never win. This engine scores every
/// hypothesis against the same evidence, records what argues *against* each one, and refuses to promote any
/// hypothesis above Possible when the underlying evidence is insufficient to test it.
/// </summary>
public sealed class HypothesisEngine
{
    private const int StronglySupportedThreshold = 70;
    private const int LikelyThreshold = 45;
    private const int PossibleThreshold = 20;

    public IReadOnlyList<Hypothesis> Rank(HypothesisInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var hypotheses = new List<Hypothesis>
        {
            EvaluateOneDriveSync(inputs),
            EvaluateDriverInterruptLatency(inputs),
            EvaluateDisplayOrDock(inputs),
            EvaluateBluetoothOrInput(inputs),
            EvaluateStorageSaturation(inputs),
            EvaluateCpuContention(inputs),
            EvaluateMemoryPaging(inputs),
            EvaluateShellExtensionBlocking(inputs),
            EvaluateSecurityOrSearchScanner(inputs),
            EvaluateThermalOrPower(inputs)
        };

        return hypotheses
            .Select(hypothesis => ApplyDriverAttribution(hypothesis, inputs))
            .OrderByDescending(hypothesis => hypothesis.Score)
            .ThenBy(hypothesis => hypothesis.Kind.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// A named driver is the strongest evidence the tool can produce. When a kernel trace attributes real
    /// DPC or ISR time to a driver image, that time is credited to the subsystem the driver belongs to,
    /// rather than left as trivia in a table.
    /// </summary>
    private static Hypothesis ApplyDriverAttribution(Hypothesis hypothesis, HypothesisInputs inputs)
    {
        var significant = DriverClassifier.Significant(inputs.DriverLatency);
        if (significant.Count == 0)
        {
            return hypothesis;
        }

        // DriverInterruptLatency is the general "a driver is stalling this machine" hypothesis, so any named
        // driver supports it. The subsystem hypotheses only take the drivers that belong to them.
        var attributed = significant
            .Select(driver => (Driver: driver, Classification: DriverClassifier.Classify(driver.Driver)))
            .Where(entry => hypothesis.Kind == HypothesisKind.DriverInterruptLatency
                || entry.Classification.Kind == hypothesis.Kind)
            .ToArray();

        if (attributed.Length == 0)
        {
            return hypothesis;
        }

        var supporting = hypothesis.Supporting.ToList();
        var score = hypothesis.Score;

        foreach (var (driver, classification) in attributed.Take(3))
        {
            // Milliseconds of high-IRQL time over the trace window. A single routine over about 1 ms is
            // already enough to drop a frame and stutter the cursor.
            score += driver.TotalMilliseconds >= 100 ? 45 : 30;

            var subsystem = classification.Subsystem is null
                ? string.Empty
                : $" - {classification.Subsystem}";

            supporting.Add(
                $"Kernel trace attributed {driver.TotalMilliseconds:N1} ms of {driver.Kind.ToUpperInvariant()} time " +
                $"(worst single routine {driver.MaxMilliseconds:N2} ms, {driver.Count:N0} occurrences) " +
                $"to `{driver.Driver}`{subsystem}.");
        }

        // A kernel trace is a direct measurement, so any earlier "this could not be measured" evidence is now
        // stale. Leaving it in place would produce a report that calls a hypothesis strongly supported and
        // untested in the same breath.
        var opposing = hypothesis.Opposing
            .Where(evidence => !evidence.Contains("untested", StringComparison.OrdinalIgnoreCase)
                && !evidence.Contains("could not be measured", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var top = attributed[0];
        var topSubsystem = top.Classification.Subsystem is null
            ? string.Empty
            : $" ({top.Classification.Subsystem})";
        var nextStep = $"`{top.Driver.Driver}`{topSubsystem} is holding the CPU at high IRQL. Update or roll back that driver, or disconnect the hardware it serves, then re-run `onelag trace dpc` to confirm the time drops.";

        return hypothesis with
        {
            Score = score,
            Verdict = Verdict(score, inputs.EvidenceQuality),
            Supporting = supporting,
            Opposing = opposing,
            NextStep = nextStep
        };
    }

    private static Hypothesis EvaluateOneDriveSync(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var telemetry = inputs.Telemetry;
        var oneDriveRunning = telemetry.OneDriveProcesses.Count > 0;
        var oneDriveCpu = telemetry.OneDriveProcesses
            .Where(process => process.CpuPercent.HasValue)
            .Sum(process => process.CpuPercent.GetValueOrDefault());
        var logChurn = telemetry.OneDriveLogFilesChangedLastMinute;

        var totalItems = inputs.Inventories.Sum(inventory => inventory.TotalItems);
        var highRiskDirectories = inputs.Inventories.Sum(inventory => inventory.HighRiskDirectories.Count);
        var capped = inputs.Inventories.Any(inventory => inventory.WasCapped);
        var emergencyBlockers = inputs.Inventories
            .SelectMany(inventory => inventory.SyncBlockers)
            .Count(blocker => blocker.Severity == Severity.Emergency);

        var pressure = PressureClassifier.Classify(inputs.Pressure);

        // Live evidence. Only these can promote OneDrive past "possible".
        var hasLiveEvidence = false;
        if (oneDriveCpu >= 15)
        {
            score += 30;
            hasLiveEvidence = true;
            supporting.Add($"OneDrive processes used about {oneDriveCpu:N1}% CPU during the sample.");
        }

        if (logChurn >= 5)
        {
            score += 30;
            hasLiveEvidence = true;
            supporting.Add($"{logChurn:N0} OneDrive log files changed in the last minute, at or above the source guide's threshold of 5 per minute.");
        }

        if (oneDriveRunning && pressure.HasDiskPressure && oneDriveCpu > 0)
        {
            score += 15;
            hasLiveEvidence = true;
            supporting.Add("OneDrive was active while the disk subsystem was saturated.");
        }

        if (inputs.Shell?.ShellWindowHung == true && oneDriveRunning)
        {
            score += 15;
            hasLiveEvidence = true;
            supporting.Add("The Explorer shell was hung while OneDrive was running, which is consistent with a stalled sync-status query blocking the shell.");
        }

        // Static evidence. Describes exposure, not causation.
        if (totalItems >= 1_000_000 || capped)
        {
            score += 20;
            supporting.Add(capped
                ? "Inventory hit the configured item cap before completing."
                : $"Synced trees contain {totalItems:N0} items, beyond the documented 1,000,000-item preview profile.");
        }
        else if (totalItems >= 300_000)
        {
            score += 20;
            supporting.Add($"Synced trees contain {totalItems:N0} items, above Microsoft's 300,000-item guidance.");
        }
        else if (totalItems >= 200_000)
        {
            score += 10;
            supporting.Add($"Synced trees contain {totalItems:N0} items, approaching Microsoft's 300,000-item guidance.");
        }
        else
        {
            opposing.Add($"Synced trees contain only {totalItems:N0} items, far below the 300,000-item threshold the source guide associates with this failure mode.");
        }

        if (highRiskDirectories > 0)
        {
            score += 20;
            supporting.Add($"{highRiskDirectories:N0} high-churn development or build directories sit inside a synced tree.");
        }
        else
        {
            opposing.Add("No high-churn development or build directories were found inside the synced trees.");
        }

        if (emergencyBlockers > 0)
        {
            score += 10;
            supporting.Add($"{emergencyBlockers:N0} emergency-severity sync blocker(s) were found.");
        }

        if (!oneDriveRunning)
        {
            opposing.Add("OneDrive was not running at capture time. No live OneDrive evidence could be collected, so this hypothesis is untested rather than disproven.");
        }
        else if (!hasLiveEvidence)
        {
            opposing.Add("OneDrive was running but showed no elevated CPU, no elevated log churn, and no disk saturation.");
        }

        // The gate: static folder shape describes risk, not an active cause. Without at least one live
        // signal, OneDrive cannot be promoted past "possible" no matter how much static risk exists.
        if (!hasLiveEvidence)
        {
            score = Math.Min(score, PossibleThreshold + 5);
        }

        var verdict = Verdict(score, inputs.EvidenceQuality);
        if (!oneDriveRunning && !hasLiveEvidence && score < LikelyThreshold)
        {
            verdict = HypothesisVerdict.NotSupported;
        }

        return new Hypothesis(
            HypothesisKind.OneDriveSync,
            verdict,
            score,
            "OneDrive sync load saturates storage or blocks Explorer through its shell extensions.",
            supporting,
            opposing,
            hasLiveEvidence
                ? "Pause OneDrive from the tray and confirm the lag stops. If it does, use `onelag remediate move-plan` on the high-churn folders."
                : "Re-run `onelag scan` while the machine is actually lagging and OneDrive is running, or use `onelag watch start` so live evidence is captured during an episode.");
    }

    private static Hypothesis EvaluateDriverInterruptLatency(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var signals = inputs.Pressure.Signals ?? Array.Empty<PerformanceSignal>();
        var dpc = PressureClassifier.Value(signals, "processor-dpc-percent");
        var dpcMaxCore = PressureClassifier.Value(signals, "processor-dpc-percent-max-core");
        var interrupt = PressureClassifier.Value(signals, "processor-interrupt-percent");
        var interruptMaxCore = PressureClassifier.Value(signals, "processor-interrupt-percent-max-core");
        var dpcsQueued = PressureClassifier.Value(signals, "processor-dpcs-queued-per-second");

        var measured = dpc.HasValue || dpcMaxCore.HasValue || interrupt.HasValue;
        if (!measured)
        {
            opposing.Add("DPC and interrupt counters were unavailable, so driver latency could not be measured. This hypothesis is untested.");
            return new Hypothesis(
                HypothesisKind.DriverInterruptLatency,
                HypothesisVerdict.Unknown,
                0,
                "A kernel driver holds a CPU at high IRQL long enough to stall the desktop and the cursor.",
                supporting,
                opposing,
                "Run LatencyMon, or capture a WPR trace with the DPC/ISR profile during a freeze, to attribute DPC time to a specific driver.");
        }

        if (dpcMaxCore >= PressureClassifier.DpcPerCorePercentThreshold)
        {
            score += 40;
            supporting.Add($"One CPU core spent {dpcMaxCore:N1}% of its time in DPC routines, which starves everything else scheduled on that core.");
        }

        if (dpc >= PressureClassifier.DpcPercentThreshold)
        {
            score += 25;
            supporting.Add($"DPC time averaged {dpc:N1}% across all cores.");
        }

        if (interruptMaxCore >= PressureClassifier.InterruptPerCorePercentThreshold)
        {
            score += 20;
            supporting.Add($"One CPU core spent {interruptMaxCore:N1}% of its time servicing interrupts.");
        }

        if (interrupt >= PressureClassifier.InterruptPercentThreshold)
        {
            score += 15;
            supporting.Add($"Interrupt time averaged {interrupt:N1}% across all cores.");
        }

        if (dpcsQueued >= PressureClassifier.DpcsQueuedPerSecondThreshold)
        {
            score += 15;
            supporting.Add($"{dpcsQueued:N0} DPCs were queued per second.");
        }

        var pressure = PressureClassifier.Classify(inputs.Pressure);
        if (inputs.MaxTimerDriftMilliseconds >= 500 && !pressure.HasCpuPressure && !pressure.HasDiskPressure && !pressure.HasMemoryPressure)
        {
            score += 20;
            supporting.Add($"The machine stalled for up to {inputs.MaxTimerDriftMilliseconds:N0} ms while CPU, disk, and memory were all unremarkable, which points below user mode.");
        }

        if (score == 0)
        {
            opposing.Add($"DPC time ({Format(dpc)}%) and interrupt time ({Format(interrupt)}%) were both within normal range.");
        }

        return new Hypothesis(
            HypothesisKind.DriverInterruptLatency,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "A kernel driver holds a CPU at high IRQL long enough to stall the desktop and the cursor.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Run LatencyMon or `onelag support trace-plan` and capture a WPR DPC/ISR trace during a freeze to name the offending driver file."
                : "No action. Interrupt latency is not currently implicated.");
    }

    private static Hypothesis EvaluateDisplayOrDock(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var host = inputs.HostContext;
        if (host is null || host.DisplayCount == 0)
        {
            opposing.Add("No display or dock context was captured, so this hypothesis is untested.");
            return new Hypothesis(
                HypothesisKind.DisplayOrDockPipeline,
                HypothesisVerdict.Unknown,
                0,
                "An external display, dock, or USB graphics driver stalls the desktop rendering path.",
                supporting,
                opposing,
                "Re-run on Windows so display topology and dock state can be captured.");
        }

        var pressure = PressureClassifier.Classify(inputs.Pressure);

        if (host.IndirectDisplayCount > 0)
        {
            score += 35;
            supporting.Add($"{host.IndirectDisplayCount:N0} display(s) are driven by an indirect/USB graphics driver (DisplayLink-class). These render frames on the CPU and push them over USB, and are a well-known source of long DPC routines and desktop stutter.");
        }

        if (host.IndirectDisplayDrivers.Count > 0)
        {
            score += 15;
            supporting.Add($"Indirect display software is running: {string.Join(", ", host.IndirectDisplayDrivers)}.");
        }

        if (host.ExternalDisplayCount > 0)
        {
            score += 15;
            supporting.Add($"{host.ExternalDisplayCount:N0} external display(s) are attached.");

            if (pressure.HasInterruptPressure)
            {
                score += 25;
                supporting.Add("External displays are attached and interrupt/DPC pressure is elevated at the same time.");
            }

            var refreshRates = host.Displays
                .Where(display => display.RefreshHz > 0)
                .Select(display => Math.Round(display.RefreshHz))
                .Distinct()
                .ToArray();
            if (refreshRates.Length > 1)
            {
                score += 10;
                supporting.Add($"Attached displays run at mixed refresh rates ({string.Join(", ", refreshRates.Select(rate => $"{rate:N0} Hz"))}), which can keep the GPU out of stable power states and cause desktop stutter.");
            }
        }
        else
        {
            opposing.Add("No external displays are attached. If lag is present right now, the display and dock path is unlikely to explain it.");
        }

        var displayResetEvents = inputs.EventLogs
            .Where(summary => summary.EventId == 4101
                || summary.Provider.Contains("Display", StringComparison.OrdinalIgnoreCase)
                || summary.Provider.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
            .Sum(summary => summary.Count);
        if (displayResetEvents > 0)
        {
            score += 20;
            supporting.Add($"{displayResetEvents:N0} recent display-driver event(s) were recorded (including display driver reset / event 4101).");
        }

        if (host.DockState == DockStates.DockedLikely)
        {
            score += 10;
            supporting.Add("The laptop appears to be docked.");
        }

        return new Hypothesis(
            HypothesisKind.DisplayOrDockPipeline,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "An external display, dock, or USB graphics driver stalls the desktop rendering path.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Undock and run on the internal panel for a working session. If the lag disappears, update or remove the dock/DisplayLink driver and the GPU driver, then re-test one display at a time."
                : "No action. The display and dock path is not currently implicated.");
    }

    private static Hypothesis EvaluateBluetoothOrInput(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var host = inputs.HostContext;
        if (host?.BluetoothRadioPresent is null)
        {
            opposing.Add("Bluetooth state was not captured, so this hypothesis is untested.");
            return new Hypothesis(
                HypothesisKind.BluetoothOrInputRadio,
                HypothesisVerdict.Unknown,
                0,
                "A Bluetooth radio or input device stalls the system, often through 2.4 GHz coexistence with Wi-Fi.",
                supporting,
                opposing,
                "Re-run on Windows so Bluetooth radio and device state can be captured.");
        }

        if (host.BluetoothRadioPresent != true || host.BluetoothRadioEnabled != true)
        {
            opposing.Add("The Bluetooth radio is absent or disabled, so it cannot be causing lag right now.");
            return new Hypothesis(
                HypothesisKind.BluetoothOrInputRadio,
                HypothesisVerdict.RuledOut,
                0,
                "A Bluetooth radio or input device stalls the system, often through 2.4 GHz coexistence with Wi-Fi.",
                supporting,
                opposing,
                "No action while the radio is off. If lag appears only when Bluetooth is on, re-run this capture then.");
        }

        var connected = host.ConnectedBluetoothDevices;
        var pressure = PressureClassifier.Classify(inputs.Pressure);

        supporting.Add("The Bluetooth radio is enabled.");
        score += 10;

        if (connected is > 0)
        {
            score += 15;
            supporting.Add($"{connected:N0} Bluetooth device(s) are connected.");
        }
        else if (connected is null)
        {
            // Windows' classic Bluetooth enumeration cannot see LE devices, and most modern peripherals are
            // LE. An unknown count must not be read as "nothing connected".
            opposing.Add("The connected-device count is unknown: Windows' classic Bluetooth enumeration does not report Bluetooth LE devices, and most modern mice and keyboards are LE.");
        }
        else
        {
            opposing.Add("The radio is on but no classic Bluetooth devices are connected. Bluetooth LE devices would not be visible to this check.");
        }

        if (connected is not 0 && pressure.HasInterruptPressure)
        {
            score += 30;
            supporting.Add("The Bluetooth radio is live while interrupt/DPC pressure is elevated. A Bluetooth mouse or keyboard sharing the 2.4 GHz band with Wi-Fi produces exactly this signature.");
        }

        return new Hypothesis(
            HypothesisKind.BluetoothOrInputRadio,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "A Bluetooth radio or input device stalls the system, often through 2.4 GHz coexistence with Wi-Fi.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Turn Bluetooth off for a working session and use wired input. If the lag disappears, switch peripherals to a 2.4 GHz USB dongle or update the Bluetooth and Wi-Fi drivers together."
                : "No action. Bluetooth is not currently implicated.");
    }

    private static Hypothesis EvaluateStorageSaturation(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var signals = inputs.Pressure.Signals ?? Array.Empty<PerformanceSignal>();
        var diskQueue = PressureClassifier.Value(signals, "physical-disk-queue-length");
        var diskActive = PressureClassifier.Value(signals, "physical-disk-active-percent");

        if (diskQueue >= 2.5)
        {
            score += 40;
            supporting.Add($"Average disk queue length was {diskQueue:N1}, above the source guide's threshold of 2.5.");
        }
        else if (diskQueue >= 2)
        {
            score += 25;
            supporting.Add($"Average disk queue length was {diskQueue:N1}.");
        }

        if (diskActive >= 90)
        {
            score += 30;
            supporting.Add($"The disk was active {diskActive:N1}% of the sample window.");
        }
        else if (diskActive >= 80)
        {
            score += 20;
            supporting.Add($"The disk was active {diskActive:N1}% of the sample window.");
        }

        if (score == 0)
        {
            opposing.Add($"Disk queue ({Format(diskQueue)}) and disk active time ({Format(diskActive)}%) were both normal.");
        }

        return new Hypothesis(
            HypothesisKind.StorageSaturation,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "The storage subsystem is saturated, so every process that touches the disk blocks.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Identify the process driving disk I/O with Resource Monitor or a ProcMon capture before changing any files."
                : "No action. Storage is not currently saturated.");
    }

    private static Hypothesis EvaluateCpuContention(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var signals = inputs.Pressure.Signals ?? Array.Empty<PerformanceSignal>();
        var cpu = PressureClassifier.Value(signals, "processor-total-percent");
        var queue = PressureClassifier.Value(signals, "processor-queue-length");

        if (cpu >= 90)
        {
            score += 35;
            supporting.Add($"Total CPU was {cpu:N1}%.");
        }
        else if (cpu >= 85)
        {
            score += 25;
            supporting.Add($"Total CPU was {cpu:N1}%.");
        }

        if (queue >= Math.Max(2, Environment.ProcessorCount))
        {
            score += 25;
            supporting.Add($"Processor queue length was {queue:N1} with {Environment.ProcessorCount} logical processors.");
        }

        var topProcesses = (inputs.Pressure.TopProcessSamples ?? Array.Empty<ProcessPressureSample>())
            .Where(process => process.CpuPercent >= 25)
            .OrderByDescending(process => process.CpuPercent)
            .Take(3)
            .ToArray();
        if (topProcesses.Length > 0)
        {
            score += 20;
            supporting.Add($"Top CPU consumers: {string.Join(", ", topProcesses.Select(process => $"{process.Name} {process.CpuPercent:N1}%"))}.");
        }

        if (score == 0)
        {
            opposing.Add($"Total CPU ({Format(cpu)}%) and processor queue ({Format(queue)}) were both normal.");
        }

        return new Hypothesis(
            HypothesisKind.CpuContention,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "User-mode processes are consuming the CPU, so the foreground application is starved.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Identify and stop or reschedule the top CPU consumer listed above."
                : "No action. The CPU is not contended.");
    }

    private static Hypothesis EvaluateMemoryPaging(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var signals = inputs.Pressure.Signals ?? Array.Empty<PerformanceSignal>();
        var availableMb = PressureClassifier.Value(signals, "memory-available-mb");
        var commit = PressureClassifier.Value(signals, "memory-commit-percent");
        var paging = PressureClassifier.Value(signals, "paging-file-usage-percent");

        if (availableMb <= 512)
        {
            score += 40;
            supporting.Add($"Only {availableMb:N0} MB of memory was available.");
        }
        else if (availableMb <= 1024)
        {
            score += 25;
            supporting.Add($"Only {availableMb:N0} MB of memory was available.");
        }

        if (commit >= 95)
        {
            score += 30;
            supporting.Add($"Commit charge was {commit:N1}%.");
        }
        else if (commit >= 90)
        {
            score += 20;
            supporting.Add($"Commit charge was {commit:N1}%.");
        }

        if (paging >= 50)
        {
            score += 20;
            supporting.Add($"Paging file usage was {paging:N1}%.");
        }

        if (score == 0)
        {
            opposing.Add($"Available memory ({Format(availableMb)} MB) and commit charge ({Format(commit)}%) were both healthy.");
        }

        return new Hypothesis(
            HypothesisKind.MemoryPaging,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "The machine is short on memory and hard-faulting, so everything waits on the page file.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Close the largest memory consumers, or add RAM. Hard faults, not CPU, are the bottleneck here."
                : "No action. Memory is not under pressure.");
    }

    private static Hypothesis EvaluateShellExtensionBlocking(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var shell = inputs.Shell;
        if (shell?.ShellWindowHung is null)
        {
            opposing.Add("Explorer shell responsiveness was not measured, so this hypothesis is untested.");
            return new Hypothesis(
                HypothesisKind.ShellExtensionBlocking,
                HypothesisVerdict.Unknown,
                0,
                "A shell extension blocks Explorer's message pump, so the desktop and taskbar appear frozen while other apps still respond.",
                supporting,
                opposing,
                "Re-run on Windows so the shell message pump can be probed.");
        }

        if (shell.ShellWindowHung == true)
        {
            score += 45;
            supporting.Add("The Explorer shell window was not pumping messages (Windows reports it as hung).");
        }

        if (shell.ShellPumpLatencyMilliseconds >= 1000)
        {
            score += 30;
            supporting.Add($"The shell took {shell.ShellPumpLatencyMilliseconds:N0} ms to answer a null message.");
        }
        else if (shell.ShellPumpLatencyMilliseconds >= 200)
        {
            score += 15;
            supporting.Add($"The shell took {shell.ShellPumpLatencyMilliseconds:N0} ms to answer a null message.");
        }

        if (shell.HungTopLevelWindows > 0)
        {
            score += 15;
            supporting.Add($"{shell.HungTopLevelWindows:N0} top-level window(s) were hung.");
        }

        if (score == 0)
        {
            opposing.Add($"The Explorer shell answered a null message in {Format(shell.ShellPumpLatencyMilliseconds)} ms and is pumping messages normally.");
        }

        return new Hypothesis(
            HypothesisKind.ShellExtensionBlocking,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "A shell extension blocks Explorer's message pump, so the desktop and taskbar appear frozen while other apps still respond.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Disable non-Microsoft shell extensions one at a time (OneDrive status overlays, cloud storage clients, archivers, antivirus context menus) and re-test."
                : "No action. Explorer is pumping messages normally.");
    }

    private static Hypothesis EvaluateSecurityOrSearchScanner(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var scannerNames = new[] { "MsMpEng", "MpDefenderCoreService", "SearchIndexer", "SearchProtocolHost", "SearchFilterHost", "TiWorker", "TrustedInstaller" };
        var scanners = (inputs.Pressure.TopProcessSamples ?? Array.Empty<ProcessPressureSample>())
            .Where(process => scannerNames.Any(name => process.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .Where(process => process.CpuPercent >= 10)
            .OrderByDescending(process => process.CpuPercent)
            .ToArray();

        foreach (var scanner in scanners.Take(3))
        {
            score += scanner.CpuPercent >= 25 ? 30 : 15;
            supporting.Add($"{scanner.Name} used {scanner.CpuPercent:N1}% CPU during the sample.");
        }

        var pressure = PressureClassifier.Classify(inputs.Pressure);
        if (scanners.Length > 0 && pressure.HasDiskPressure)
        {
            score += 20;
            supporting.Add("A scanner or indexer was active while the disk was saturated.");
        }

        if (score == 0)
        {
            opposing.Add("Defender, Windows Search, and Windows Update servicing processes were not consuming meaningful CPU.");
        }

        return new Hypothesis(
            HypothesisKind.SecurityOrSearchScanner,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "Defender, Windows Search, or Windows Update servicing is scanning in the background and saturating CPU or disk.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Check whether a full Defender scan or a Search index rebuild is in progress, and let it finish before drawing conclusions. Add build and dependency folders to Defender exclusions if they are being rescanned."
                : "No action. Background scanners are quiet.");
    }

    private static Hypothesis EvaluateThermalOrPower(HypothesisInputs inputs)
    {
        var supporting = new List<string>();
        var opposing = new List<string>();
        var score = 0;

        var throttleEvents = inputs.EventLogs
            .Where(summary => summary.Provider.Contains("Kernel-Processor-Power", StringComparison.OrdinalIgnoreCase)
                || summary.Provider.Contains("Thermal", StringComparison.OrdinalIgnoreCase)
                || summary.Provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase))
            .Sum(summary => summary.Count);

        if (throttleEvents > 0)
        {
            score += 40;
            supporting.Add($"{throttleEvents:N0} recent processor-power, thermal, or hardware-error event(s) were recorded.");
        }

        var host = inputs.HostContext;
        if (host is not null && host.PowerSource.Contains("battery", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
            supporting.Add("The machine is running on battery, where power policy can cap CPU and GPU clocks.");
        }

        if (score == 0)
        {
            opposing.Add("No thermal, processor-power, or hardware-error events were recorded.");
        }

        return new Hypothesis(
            HypothesisKind.ThermalOrPowerThrottling,
            Verdict(score, inputs.EvidenceQuality),
            score,
            "The CPU or GPU is being clock-limited by thermal or power policy.",
            supporting,
            opposing,
            score >= PossibleThreshold
                ? "Check the Windows power mode and the vendor power/thermal utility, and confirm the machine is not thermally throttled under load."
                : "No action. Thermal and power throttling are not implicated.");
    }

    /// <summary>
    /// Deliberately not clamped by overall evidence quality. Each hypothesis already refuses to score on
    /// evidence it could not collect — an unmeasured hypothesis returns Unknown rather than a low score — so
    /// a thin capture cannot manufacture a verdict. Clamping again here would suppress a hypothesis that
    /// *was* directly measured just because unrelated collectors were unavailable.
    /// </summary>
    private static HypothesisVerdict Verdict(int score, EvidenceQuality quality)
    {
        _ = quality;

        return score switch
        {
            >= StronglySupportedThreshold => HypothesisVerdict.StronglySupported,
            >= LikelyThreshold => HypothesisVerdict.Likely,
            >= PossibleThreshold => HypothesisVerdict.Possible,
            _ => HypothesisVerdict.NotSupported
        };
    }

    private static string Format(double? value)
    {
        return value.HasValue ? value.Value.ToString("N1") : "unknown";
    }
}
