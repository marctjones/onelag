using OneLag.Core;
using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// The collector's enumeration touches the real filesystem, wevtutil, and system commands, so the meaningful
/// checks are Windows-only. The one thing worth asserting off Windows is that it degrades honestly rather
/// than throwing.
/// </summary>
public sealed class WindowsLogCollectorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    [Fact]
    public void OffWindowsItYieldsANoteRatherThanThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var items = new WindowsLogCollector()
            .Enumerate(new LogCollectionScope(TimeSpan.FromHours(48)), Now)
            .ToArray();

        var note = Assert.Single(items);
        Assert.IsType<TextCollectionItem>(note);
        Assert.Equal(CollectionCategory.SystemInfo, note.Category);
    }

    [WindowsFact]
    public void CollectsRealEventLogsAndSystemInfoIntoABundle()
    {
        var scope = new LogCollectionScope(TimeSpan.FromHours(72));
        var items = new WindowsLogCollector().Enumerate(scope, DateTimeOffset.UtcNow);

        var output = Path.Combine(Path.GetTempPath(), "onelag-collect-it", Guid.NewGuid().ToString("N"));
        try
        {
            var result = new LogCollectionService().Collect(
                new LogCollectionOptions(output, MaxTotalBytes: 200L * 1024 * 1024, Zip: false),
                items,
                DateTimeOffset.UtcNow);

            // The System event log always has recent entries on a real machine, so at least one rendered
            // event-log file must have been produced. A bundle with none means wevtutil never ran or the
            // XPath is wrong.
            Assert.Contains(result.Entries, entry =>
                entry.Category == CollectionCategory.EventLog
                && entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated);

            var systemLog = result.Entries.FirstOrDefault(entry =>
                entry.RelativePath.Contains("System", StringComparison.OrdinalIgnoreCase)
                && entry.Category == CollectionCategory.EventLog);
            if (systemLog is not null)
            {
                var content = File.ReadAllText(Path.Combine(result.Directory, systemLog.RelativePath));
                Assert.Contains("<Event", content, StringComparison.Ordinal);
            }

            Assert.True(File.Exists(result.ManifestPath));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }
}
