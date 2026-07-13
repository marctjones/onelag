namespace OneLag.Core;

public sealed record PressureClassification(
    bool HasCpuPressure,
    bool HasMemoryPressure,
    bool HasDiskPressure,
    bool HasInterruptPressure,
    IReadOnlyList<string> Evidence)
{
    public bool HasAnyPressure => HasCpuPressure || HasMemoryPressure || HasDiskPressure || HasInterruptPressure;
}

public static class PressureClassifier
{
    /// <summary>
    /// A driver holding a CPU at high IRQL starves everything else on that core, including the cursor.
    /// Averaged-across-cores DPC time hides a single pinned core, so the per-core maximum is treated as
    /// the primary signal and the total as a weaker corroborator.
    /// </summary>
    public const double DpcPercentThreshold = 3;
    public const double DpcPerCorePercentThreshold = 10;
    public const double InterruptPercentThreshold = 8;
    public const double InterruptPerCorePercentThreshold = 15;
    public const double DpcsQueuedPerSecondThreshold = 30_000;

    public static PressureClassification Classify(SystemPressureSnapshot pressure)
    {
        var signals = pressure.Signals ?? Array.Empty<PerformanceSignal>();
        var evidence = new List<string>();

        var cpuPercent = Value(signals, "processor-total-percent");
        var processorQueue = Value(signals, "processor-queue-length");
        var memoryAvailableMb = Value(signals, "memory-available-mb");
        var memoryCommitPercent = Value(signals, "memory-commit-percent");
        var pagingUsagePercent = Value(signals, "paging-file-usage-percent");
        var diskQueue = Value(signals, "physical-disk-queue-length");
        var diskActivePercent = Value(signals, "physical-disk-active-percent");
        var dpcPercent = Value(signals, "processor-dpc-percent");
        var dpcPerCorePercent = Value(signals, "processor-dpc-percent-max-core");
        var interruptPercent = Value(signals, "processor-interrupt-percent");
        var interruptPerCorePercent = Value(signals, "processor-interrupt-percent-max-core");
        var dpcsQueued = Value(signals, "processor-dpcs-queued-per-second");

        var hasCpuPressure = cpuPercent >= 85
            || processorQueue >= Math.Max(2, Environment.ProcessorCount);
        if (hasCpuPressure)
        {
            evidence.Add($"cpu={Format(cpuPercent)}%, processorQueue={Format(processorQueue)}");
        }

        var hasMemoryPressure = memoryAvailableMb <= 1024
            || memoryCommitPercent >= 90
            || pagingUsagePercent >= 50;
        if (hasMemoryPressure)
        {
            evidence.Add($"availableMemoryMb={Format(memoryAvailableMb)}, commit={Format(memoryCommitPercent)}%, paging={Format(pagingUsagePercent)}%");
        }

        var hasDiskPressure = diskQueue >= 2
            || diskActivePercent >= 80;
        if (hasDiskPressure)
        {
            evidence.Add($"diskQueue={Format(diskQueue)}, diskActive={Format(diskActivePercent)}%");
        }

        var hasInterruptPressure = dpcPerCorePercent >= DpcPerCorePercentThreshold
            || interruptPerCorePercent >= InterruptPerCorePercentThreshold
            || dpcPercent >= DpcPercentThreshold
            || interruptPercent >= InterruptPercentThreshold
            || dpcsQueued >= DpcsQueuedPerSecondThreshold;
        if (hasInterruptPressure)
        {
            evidence.Add($"dpc={Format(dpcPercent)}% (maxCore={Format(dpcPerCorePercent)}%), interrupt={Format(interruptPercent)}% (maxCore={Format(interruptPerCorePercent)}%), dpcsQueued/s={Format(dpcsQueued)}");
        }

        if (!hasCpuPressure && !hasMemoryPressure && !hasDiskPressure && !hasInterruptPressure
            && pressure.EvidenceState.Contains("pressure", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(pressure.EvidenceState);
        }

        return new PressureClassification(hasCpuPressure, hasMemoryPressure, hasDiskPressure, hasInterruptPressure, evidence);
    }

    public static double? Value(IEnumerable<PerformanceSignal> signals, string kind)
    {
        return signals.FirstOrDefault(signal => signal.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string Format(double? value)
    {
        return value.HasValue ? value.Value.ToString("N1") : "unknown";
    }
}
