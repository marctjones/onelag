namespace OneLag.Core;

public sealed class ScanRunner
{
    private readonly IPlatformProbe platformProbe;
    private readonly InventoryScanner inventoryScanner;
    private readonly RiskEngine riskEngine;

    public ScanRunner(IPlatformProbe platformProbe, InventoryScanner inventoryScanner, RiskEngine riskEngine)
    {
        this.platformProbe = platformProbe;
        this.inventoryScanner = inventoryScanner;
        this.riskEngine = riskEngine;
    }

    public DiagnosticReport Run(ScanOptions options, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var discoveredRoots = platformProbe.DiscoverOneDriveRoots();
        var roots = ResolveRoots(options.Roots, discoveredRoots);

        var telemetry = platformProbe.CaptureTelemetry();
        var pressure = platformProbe.CaptureSystemPressure();
        var eventLogs = platformProbe.ReadRecentEventSummaries(started.AddMinutes(-10));

        var inventories = new List<InventorySummary>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inventories.Add(inventoryScanner.Scan(root.Path, options.MaxItems, cancellationToken));
        }

        var (diagnosis, findings, recommendations) = riskEngine.Analyze(inventories, telemetry, pressure);
        return new DiagnosticReport(started, DateTimeOffset.UtcNow, roots, inventories, telemetry, pressure, eventLogs, diagnosis, findings, recommendations);
    }

    private static IReadOnlyList<RootCandidate> ResolveRoots(IReadOnlyList<string> requestedRoots, IReadOnlyList<RootCandidate> discoveredRoots)
    {
        if (requestedRoots.Count == 0)
        {
            return discoveredRoots;
        }

        return requestedRoots
            .Select(root => new RootCandidate(Path.GetFullPath(root), "argument", "high", null))
            .ToArray();
    }
}
