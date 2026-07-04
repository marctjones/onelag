using System.Diagnostics;
using System.Runtime.InteropServices;
using OneLag.Core;

namespace OneLag.Windows;

internal static class WindowsPerformanceSampler
{
    private static readonly TimeSpan DefaultSampleWindow = TimeSpan.FromMilliseconds(250);

    private static readonly CounterDefinition[] CounterDefinitions =
    {
        new(@"\Processor(_Total)\% Processor Time", "processor-total-percent", "percent"),
        new(@"\System\Processor Queue Length", "processor-queue-length", "count"),
        new(@"\PhysicalDisk(_Total)\Avg. Disk Queue Length", "physical-disk-queue-length", "count"),
        new(@"\PhysicalDisk(_Total)\% Disk Time", "physical-disk-active-percent", "percent"),
        new(@"\PhysicalDisk(_Total)\Disk Bytes/sec", "physical-disk-bytes-per-second", "bytes-per-second"),
        new(@"\Paging File(_Total)\% Usage", "paging-file-usage-percent", "percent")
    };

    public static IReadOnlyList<ProcessSample> SampleProcessesByName(string processName)
    {
        var samples = SampleProcessPressure(
            name => name.Equals(processName, StringComparison.OrdinalIgnoreCase),
            DefaultSampleWindow,
            int.MaxValue,
            includePath: true);

        return samples
            .Select(sample => new ProcessSample(
                sample.Name,
                sample.ProcessId,
                sample.WorkingSetBytes,
                sample.TotalProcessorTime,
                sample.Path,
                sample.CpuPercent))
            .ToArray();
    }

    public static IReadOnlyList<ProcessPressureSample> SampleTopProcesses(int take)
    {
        return SampleProcessPressure(
                _ => true,
                DefaultSampleWindow,
                take,
                includePath: false)
            .Select(sample => new ProcessPressureSample(
                sample.Name,
                sample.ProcessId,
                sample.CpuPercent,
                sample.WorkingSetBytes,
                sample.Path))
            .ToArray();
    }

    public static IReadOnlyList<PerformanceSignal> CaptureSignals()
    {
        var signals = new List<PerformanceSignal>();
        signals.AddRange(CapturePdhSignals());
        signals.AddRange(CaptureMemorySignals());
        return signals
            .GroupBy(signal => signal.Kind, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(signal => signal.Value.HasValue) ?? group.First())
            .OrderBy(signal => signal.Kind, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string CapturePowerState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unavailable-on-this-platform";
        }

        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return "unknown";
            }

            var ac = status.ACLineStatus switch
            {
                0 => "battery",
                1 => "ac",
                _ => "unknown"
            };
            var battery = status.BatteryLifePercent == 255
                ? "unknown"
                : $"{status.BatteryLifePercent}%";

            return $"source={ac};battery={battery}";
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "unknown";
        }
    }

    private static IReadOnlyList<ProcessPressurePoint> SampleProcessPressure(
        Func<string, bool> includeName,
        TimeSpan sampleWindow,
        int take,
        bool includePath)
    {
        var first = CaptureProcessPoints(includeName, includePath);
        var stopwatch = Stopwatch.StartNew();
        Thread.Sleep(sampleWindow);
        var second = CaptureProcessPoints(includeName, includePath);
        stopwatch.Stop();

        if (stopwatch.Elapsed <= TimeSpan.Zero)
        {
            return Array.Empty<ProcessPressurePoint>();
        }

        return second.Values
            .Select(point => ToPressurePoint(point, first, stopwatch.Elapsed))
            .Where(point => point is not null)
            .Cast<ProcessPressurePoint>()
            .OrderByDescending(point => point.CpuPercent)
            .ThenByDescending(point => point.WorkingSetBytes)
            .Take(take)
            .ToArray();
    }

    private static ProcessPressurePoint? ToPressurePoint(
        ProcessPoint second,
        IReadOnlyDictionary<int, ProcessPoint> first,
        TimeSpan elapsed)
    {
        if (!first.TryGetValue(second.ProcessId, out var firstPoint))
        {
            return null;
        }

        var cpuDelta = second.TotalProcessorTime - firstPoint.TotalProcessorTime;
        if (cpuDelta < TimeSpan.Zero)
        {
            return null;
        }

        var cpuPercent = cpuDelta.TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        cpuPercent = Math.Clamp(cpuPercent, 0, 100);

        return new ProcessPressurePoint(
            second.Name,
            second.ProcessId,
            second.WorkingSetBytes,
            second.TotalProcessorTime,
            second.Path ?? firstPoint.Path,
            cpuPercent);
    }

    private static Dictionary<int, ProcessPoint> CaptureProcessPoints(Func<string, bool> includeName, bool includePath)
    {
        var points = new Dictionary<int, ProcessPoint>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!includeName(name))
                    {
                        continue;
                    }

                    points[process.Id] = new ProcessPoint(
                        name,
                        process.Id,
                        process.WorkingSet64,
                        process.TotalProcessorTime,
                        includePath ? TryGetProcessPath(process) : null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return points;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PerformanceSignal> CapturePdhSignals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return CounterDefinitions
                .Select(definition => new PerformanceSignal(definition.Kind, null, definition.Unit, "unavailable-on-this-platform"))
                .ToArray();
        }

        uint status;
        IntPtr query;
        try
        {
            status = PdhOpenQuery(null, UIntPtr.Zero, out query);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return CounterDefinitions
                .Select(definition => new PerformanceSignal(definition.Kind, null, definition.Unit, "pdh-entrypoint-unavailable"))
                .ToArray();
        }

        if (status != ErrorSuccess)
        {
            return CounterDefinitions
                .Select(definition => new PerformanceSignal(definition.Kind, null, definition.Unit, $"pdh-open-failed-0x{status:X}"))
                .ToArray();
        }

        try
        {
            var counters = new List<(CounterDefinition Definition, IntPtr Handle)>();
            var failed = new List<PerformanceSignal>();
            foreach (var definition in CounterDefinitions)
            {
                var addStatus = PdhAddEnglishCounter(query, definition.Path, UIntPtr.Zero, out var counter);
                if (addStatus == ErrorSuccess)
                {
                    counters.Add((definition, counter));
                }
                else
                {
                    failed.Add(new PerformanceSignal(definition.Kind, null, definition.Unit, $"pdh-counter-unavailable-0x{addStatus:X}"));
                }
            }

            if (counters.Count == 0)
            {
                return failed;
            }

            _ = PdhCollectQueryData(query);
            Thread.Sleep(DefaultSampleWindow);
            _ = PdhCollectQueryData(query);

            var signals = new List<PerformanceSignal>(failed);
            foreach (var (definition, counter) in counters)
            {
                var valueStatus = PdhGetFormattedCounterValue(counter, PdhFmtDouble, out _, out var value);
                if (valueStatus == ErrorSuccess && value.CStatus == ErrorSuccess && double.IsFinite(value.DoubleValue))
                {
                    signals.Add(new PerformanceSignal(definition.Kind, Math.Max(0, value.DoubleValue), definition.Unit, "pdh"));
                }
                else
                {
                    signals.Add(new PerformanceSignal(definition.Kind, null, definition.Unit, $"pdh-no-data-0x{valueStatus:X}-0x{value.CStatus:X}"));
                }
            }

            return signals;
        }
        finally
        {
            _ = PdhCloseQuery(query);
        }
    }

    private static IReadOnlyList<PerformanceSignal> CaptureMemorySignals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<PerformanceSignal>();
        }

        try
        {
            if (!GetPerformanceInfo(out var info, Marshal.SizeOf<PerformanceInformation>()))
            {
                return new[]
                {
                    new PerformanceSignal("memory-available-mb", null, "megabytes", "get-performance-info-failed"),
                    new PerformanceSignal("memory-commit-percent", null, "percent", "get-performance-info-failed")
                };
            }

            var pageSize = ToDouble(info.PageSize);
            var availableMb = ToDouble(info.PhysicalAvailable) * pageSize / 1024 / 1024;
            var commitPercent = ToDouble(info.CommitLimit) > 0
                ? ToDouble(info.CommitTotal) / ToDouble(info.CommitLimit) * 100
                : (double?)null;

            return new[]
            {
                new PerformanceSignal("memory-available-mb", availableMb, "megabytes", "get-performance-info"),
                new PerformanceSignal("memory-commit-percent", commitPercent, "percent", "get-performance-info")
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Array.Empty<PerformanceSignal>();
        }
    }

    private static double ToDouble(UIntPtr value)
    {
        return value.ToUInt64();
    }

    private sealed record CounterDefinition(string Path, string Kind, string Unit);

    private sealed record ProcessPoint(
        string Name,
        int ProcessId,
        long WorkingSetBytes,
        TimeSpan TotalProcessorTime,
        string? Path);

    private sealed record ProcessPressurePoint(
        string Name,
        int ProcessId,
        long WorkingSetBytes,
        TimeSpan TotalProcessorTime,
        string? Path,
        double CpuPercent);

    private const uint ErrorSuccess = 0;
    private const uint PdhFmtDouble = 0x00000200;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PdhOpenQuery(string? dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFormattedCounterValue value);

    [DllImport("pdh.dll", SetLastError = true)]
    private static extern uint PdhCloseQuery(IntPtr query);

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation performanceInformation, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public int Size;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
