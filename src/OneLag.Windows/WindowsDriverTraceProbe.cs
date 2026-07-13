using System.Security.Principal;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Attributes kernel DPC and ISR time to the driver images responsible for it.
///
/// The PDH counters can prove that *a* driver is holding a CPU at high IRQL long enough to stall the
/// desktop. They cannot say which driver. This runs a bounded kernel ETW session, records every DPC and ISR
/// with the address of its routine, maps that address into the loaded kernel image that contains it, and
/// reports the drivers by the time they spent at high IRQL.
///
/// This is deliberately a separate, explicit command rather than part of the default scan: it needs
/// administrator rights and a system-wide kernel logger session, which is exactly the kind of heavy tracing
/// the project promised not to start on its own.
/// </summary>
internal static class WindowsDriverTraceProbe
{
    private const int MaxDrivers = 25;

    public static DriverLatencyAttribution Capture(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DriverLatencyAttribution.Unavailable("unavailable-on-this-platform");
        }

        if (!TraceEventSession.IsElevated().GetValueOrDefault())
        {
            return DriverLatencyAttribution.Unavailable("requires-administrator");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return DriverLatencyAttribution.Unavailable("trace-cancelled");
        }

        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            return Run(startedAt, duration, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return DriverLatencyAttribution.Unavailable("kernel-session-access-denied");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            return DriverLatencyAttribution.Unavailable("trace-runtime-unavailable");
        }
    }

    private static DriverLatencyAttribution Run(DateTimeOffset startedAt, TimeSpan duration, CancellationToken cancellationToken)
    {
        var modules = new KernelModuleMap();
        var accumulators = new Dictionary<(string Driver, string Kind), Accumulator>();

        // Guards the stop-before-process race: CancellationToken.Register runs its callback synchronously
        // when the token is already cancelled, which would stop a session that Process() has not yet begun
        // consuming.
        var processing = new ManualResetEventSlim(false);

        using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName)
        {
            StopOnDispose = true
        };

        session.EnableKernelProvider(
            KernelTraceEventParser.Keywords.DeferedProcedureCalls
            | KernelTraceEventParser.Keywords.Interrupt
            | KernelTraceEventParser.Keywords.ImageLoad);

        // The ImageLoad rundown reports every image already resident when the session starts, which is what
        // turns a routine address into a driver name. KernelModuleMap drops user-mode images and ignores the
        // repeat reports from the DCStop rundown.
        void OnImage(ImageLoadTraceData data)
        {
            modules.TryAdd(data.ImageBase, data.ImageSize, data.FileName);
        }

        session.Source.Kernel.ImageLoad += OnImage;
        session.Source.Kernel.ImageDCStart += OnImage;
        session.Source.Kernel.ImageDCStop += OnImage;

        session.Source.Kernel.PerfInfoDPC += data => Record(accumulators, modules, data.Routine, data.ElapsedTimeMSec, "dpc");
        session.Source.Kernel.PerfInfoThreadedDPC += data => Record(accumulators, modules, data.Routine, data.ElapsedTimeMSec, "dpc");
        session.Source.Kernel.PerfInfoTimerDPC += data => Record(accumulators, modules, data.Routine, data.ElapsedTimeMSec, "dpc");
        session.Source.Kernel.PerfInfoISR += data => Record(accumulators, modules, data.Routine, data.ElapsedTimeMSec, "isr");

        using var stopTimer = new Timer(_ => Stop(session, processing), null, duration, Timeout.InfiniteTimeSpan);
        using var cancellationRegistration = cancellationToken.Register(() => Stop(session, processing));

        processing.Set();
        session.Source.Process();

        // Process() only returns once the session has stopped, and by then the controller handle is gone, so
        // reading EventsLost queries a session that no longer exists and throws a COMException. The lost-event
        // count is a quality footnote; losing it must not throw away an otherwise successful trace.
        long eventsLost;
        try
        {
            eventsLost = session.EventsLost;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ObjectDisposedException)
        {
            eventsLost = -1;
        }

        var drivers = accumulators
            .Select(entry => entry.Value.ToSample(entry.Key.Driver, entry.Key.Kind))
            .OrderByDescending(sample => sample.TotalMilliseconds)
            .Take(MaxDrivers)
            .ToArray();

        var evidenceState = drivers.Length == 0
            ? "windows-kernel-etw-no-dpc-isr-events"
            : eventsLost > 0
                // Dropped events mean the totals under-report by an unknown factor. Saying so beats
                // presenting a truncated number as fact.
                ? $"windows-kernel-etw-dpc-isr;events-lost={eventsLost}"
                : "windows-kernel-etw-dpc-isr";

        return new DriverLatencyAttribution(startedAt, duration, drivers, evidenceState);
    }

    /// <summary>
    /// Stopping is best-effort and must never throw: this runs on a timer or cancellation callback, where an
    /// escaping exception terminates the process. Worse, TraceEventSession marks itself stopped before it
    /// calls ControlTrace, so a throwing Stop leaves the session unstoppable and Process() blocked forever.
    /// </summary>
    private static void Stop(TraceEventSession session, ManualResetEventSlim processing)
    {
        try
        {
            processing.Wait(TimeSpan.FromSeconds(5));
            session.Stop(noThrow: true);
        }
        catch (Exception)
        {
            // The session is already gone, or ControlTrace refused. Either way Process() will return.
        }
    }

    private static void Record(
        Dictionary<(string Driver, string Kind), Accumulator> accumulators,
        KernelModuleMap modules,
        ulong routine,
        double elapsedMilliseconds,
        string kind)
    {
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0)
        {
            return;
        }

        var key = (modules.Resolve(routine), kind);
        if (!accumulators.TryGetValue(key, out var accumulator))
        {
            accumulator = new Accumulator();
            accumulators[key] = accumulator;
        }

        accumulator.Add(elapsedMilliseconds);
    }

    private sealed class Accumulator
    {
        private double total;
        private double max;
        private long count;

        public void Add(double milliseconds)
        {
            total += milliseconds;
            count++;
            if (milliseconds > max)
            {
                max = milliseconds;
            }
        }

        public DriverLatencySample ToSample(string driver, string kind)
        {
            return new DriverLatencySample(driver, kind, total, max, count);
        }
    }

    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
