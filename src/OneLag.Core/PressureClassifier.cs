namespace OneLag.Core;

public sealed record PressureClassification(
    bool HasCpuPressure,
    bool HasMemoryPressure,
    bool HasDiskPressure,
    IReadOnlyList<string> Evidence)
{
    public bool HasAnyPressure => HasCpuPressure || HasMemoryPressure || HasDiskPressure;
}

public static class PressureClassifier
{
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

        var hasCpuPressure = cpuPercent >= 85
            || processorQueue >= Math.Max(2, Environment.ProcessorCount);
        if (hasCpuPressure)
        {
            evidence.Add($"cpu={Format(cpuPercent, "unknown")}%, processorQueue={Format(processorQueue, "unknown")}");
        }

        var hasMemoryPressure = memoryAvailableMb <= 1024
            || memoryCommitPercent >= 90
            || pagingUsagePercent >= 50;
        if (hasMemoryPressure)
        {
            evidence.Add($"availableMemoryMb={Format(memoryAvailableMb, "unknown")}, commit={Format(memoryCommitPercent, "unknown")}%, paging={Format(pagingUsagePercent, "unknown")}%");
        }

        var hasDiskPressure = diskQueue >= 2
            || diskActivePercent >= 80;
        if (hasDiskPressure)
        {
            evidence.Add($"diskQueue={Format(diskQueue, "unknown")}, diskActive={Format(diskActivePercent, "unknown")}%");
        }

        if (!hasCpuPressure && !hasMemoryPressure && !hasDiskPressure
            && pressure.EvidenceState.Contains("pressure", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(pressure.EvidenceState);
        }

        return new PressureClassification(hasCpuPressure, hasMemoryPressure, hasDiskPressure, evidence);
    }

    private static double? Value(IEnumerable<PerformanceSignal> signals, string kind)
    {
        return signals.FirstOrDefault(signal => signal.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static string Format(double? value, string fallback)
    {
        return value.HasValue ? value.Value.ToString("N1") : fallback;
    }
}
