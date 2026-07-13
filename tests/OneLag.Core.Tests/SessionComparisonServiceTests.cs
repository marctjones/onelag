using OneLag.Core;

namespace OneLag.Core.Tests;

public sealed class SessionComparisonServiceTests : IDisposable
{
    private static readonly DateTimeOffset Origin = DateTimeOffset.Parse("2026-07-13T09:00:00Z");

    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "onelag-compare-tests", Guid.NewGuid().ToString("N"));

    public SessionComparisonServiceTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public void CompareAttributesLagToTheDockedSession()
    {
        // The experiment this exists for: one working day docked with an external display, one undocked.
        // Same machine, same OneDrive, different hardware configuration.
        var docked = WriteSession("docked", count: 60, start: Origin, dockedConfiguration: true, driftMilliseconds: 950);
        var undocked = WriteSession("undocked", count: 60, start: Origin.AddHours(4), dockedConfiguration: false, driftMilliseconds: 4);

        var service = new SessionComparisonService(new WatchService());
        var comparison = service.Compare(new[] { docked, undocked });
        var report = service.BuildReport(comparison);

        Assert.Equal(2, comparison.Sessions.Count);
        Assert.Equal(0, comparison.Sessions.Single(session => session.Name == "undocked").Episodes);
        Assert.True(comparison.Sessions.Single(session => session.Name == "docked").Episodes > 0);

        Assert.Contains("Every lag episode happened in", comparison.Correlation.Conclusion!, StringComparison.Ordinal);
        Assert.Contains("external-display", comparison.Correlation.Conclusion!, StringComparison.Ordinal);

        Assert.Contains("# OneLag Session Comparison", report, StringComparison.Ordinal);
        Assert.Contains("## Lag By Configuration", report, StringComparison.Ordinal);
        Assert.Contains("onelag trace dpc", report, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareRequiresAtLeastOneSession()
    {
        var service = new SessionComparisonService(new WatchService());

        Assert.Throws<ArgumentException>(() => service.Compare(Array.Empty<string>()));
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private string WriteSession(string name, int count, DateTimeOffset start, bool dockedConfiguration, double driftMilliseconds)
    {
        var directory = Path.Combine(tempRoot, name);
        Directory.CreateDirectory(directory);

        var samplesPath = Path.Combine(directory, "samples.ndjson");
        var lines = new List<string>();

        for (var index = 0; index < count; index++)
        {
            var sample = new WatchSample(
                start.AddSeconds(index * 2),
                driftMilliseconds,
                new TelemetrySnapshot(start, Array.Empty<ProcessSample>(), 0, null, "test"),
                new SystemPressureSnapshot(start, "normal", "normal", "normal", "ac", Array.Empty<string>(), "test"),
                "explorer",
                new HostContext(
                    start,
                    dockedConfiguration ? 2 : 1,
                    dockedConfiguration ? 1 : 0,
                    0,
                    Array.Empty<DisplayInfo>(),
                    false,
                    false,
                    0,
                    dockedConfiguration ? "source=ac;battery=100%" : "source=battery;battery=70%",
                    dockedConfiguration,
                    Array.Empty<string>(),
                    dockedConfiguration ? DockStates.DockedLikely : DockStates.UndockedLikely,
                    "test"));

            lines.Add(System.Text.Json.JsonSerializer.Serialize(
                sample,
                new System.Text.Json.JsonSerializerOptions
                {
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                }));
        }

        File.WriteAllLines(samplesPath, lines);
        return directory;
    }
}
