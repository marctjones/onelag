using OneLag.Windows;

namespace OneLag.Windows.Tests;

/// <summary>
/// A fake OneDrive log store.
///
/// OneDrive writes `.odl` / `.odlgz` / `.odlsent` files under
/// %LocalAppData%\Microsoft\OneDrive\logs\{Personal,Business1,...}. The contents are a binary, obfuscated,
/// undocumented format, and parsing them is an explicit non-goal. What the source guide actually relies on is
/// churn — more than five log files written per minute means the sync engine is thrashing — and churn is pure
/// file metadata. So the log store can be modelled exactly: the right directory shape, the rotation
/// extensions, and controlled write times.
/// </summary>
public sealed class OneDriveLogStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    private readonly string root = Path.Combine(Path.GetTempPath(), "onelag-log-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ChurnCountsOnlyLogsWrittenInsideTheWindow()
    {
        var logs = CreateLogRoot();

        // Six logs written in the last minute: the sync engine is thrashing. The source guide's threshold is
        // more than five per minute.
        WriteLog(logs, "Personal", "general-1.odl", Now.AddSeconds(-10));
        WriteLog(logs, "Personal", "general-2.odl", Now.AddSeconds(-20));
        WriteLog(logs, "Personal", "SyncEngine-1.odl", Now.AddSeconds(-30));
        WriteLog(logs, "Business1", "general-1.odl", Now.AddSeconds(-40));
        WriteLog(logs, "Business1", "SyncEngine-1.odl", Now.AddSeconds(-50));
        WriteLog(logs, "Business1", "SyncEngine-2.odlgz", Now.AddSeconds(-55));

        // Older rotated logs must not count as current churn.
        WriteLog(logs, "Personal", "general-old.odlgz", Now.AddHours(-3));
        WriteLog(logs, "Business1", "SyncEngine-old.odlgz", Now.AddDays(-1));

        var churn = OneDriveLogStore.Measure(logs, Now);

        Assert.Equal(6, churn.FilesChangedLastMinute);
        Assert.Equal(8, churn.FilesSampled);
        Assert.False(churn.Truncated);
        Assert.Equal("onedrive-log-metadata", churn.EvidenceState);
    }

    [Fact]
    public void AQuietSyncEngineReportsZeroChurn()
    {
        var logs = CreateLogRoot();
        WriteLog(logs, "Personal", "general-1.odl", Now.AddHours(-2));
        WriteLog(logs, "Personal", "general-2.odlgz", Now.AddDays(-2));

        var churn = OneDriveLogStore.Measure(logs, Now);

        Assert.Equal(0, churn.FilesChangedLastMinute);
        Assert.Equal(2, churn.FilesSampled);
    }

    [Fact]
    public void AMissingLogStoreIsNotReportedAsAQuietOne()
    {
        // Zero churn from a log store that does not exist would read as a measured "OneDrive is calm" when
        // in fact nothing was measured at all. The evidence state has to say which it was.
        var churn = OneDriveLogStore.Measure(Path.Combine(root, "does-not-exist"), Now);

        Assert.Equal(0, churn.FilesChangedLastMinute);
        Assert.Equal("onedrive-log-root-not-found", churn.EvidenceState);

        Assert.Equal("onedrive-log-root-unknown", OneDriveLogStore.Measure(null, Now).EvidenceState);
    }

    [Fact]
    public void LogsStampedInTheFutureAreNotCountedAsChurnThatJustHappened()
    {
        // Clock changes and daylight-saving shifts genuinely produce this, and a file "written" ten minutes
        // from now must not be read as a log that appeared in the last sixty seconds.
        var logs = CreateLogRoot();
        WriteLog(logs, "Personal", "future.odl", Now.AddMinutes(10));
        WriteLog(logs, "Personal", "recent.odl", Now.AddSeconds(-5));

        var churn = OneDriveLogStore.Measure(logs, Now);

        Assert.Equal(1, churn.FilesChangedLastMinute);
    }

    [Fact]
    public void AnEmptyLogStoreIsReadableAndReportsZero()
    {
        var logs = CreateLogRoot();

        var churn = OneDriveLogStore.Measure(logs, Now);

        Assert.Equal(0, churn.FilesChangedLastMinute);
        Assert.Equal(0, churn.FilesSampled);
        Assert.Equal("onedrive-log-metadata", churn.EvidenceState);
    }

    [Fact]
    public void APathologicalLogStoreIsCappedAndSaysSo()
    {
        var logs = CreateLogRoot();
        var directory = Directory.CreateDirectory(Path.Combine(logs, "Personal"));

        for (var index = 0; index < OneDriveLogStore.MaxSampledFiles + 25; index++)
        {
            var path = Path.Combine(directory.FullName, $"general-{index}.odl");
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, Now.AddHours(-5).UtcDateTime);
        }

        var churn = OneDriveLogStore.Measure(logs, Now);

        Assert.True(churn.Truncated);
        Assert.Equal(OneDriveLogStore.MaxSampledFiles, churn.FilesSampled);
        Assert.Contains("truncated", churn.EvidenceState, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string CreateLogRoot()
    {
        var logs = Path.Combine(root, "Microsoft", "OneDrive", "logs");
        Directory.CreateDirectory(logs);
        return logs;
    }

    private static void WriteLog(string logRoot, string account, string name, DateTimeOffset lastWrite)
    {
        var directory = Directory.CreateDirectory(Path.Combine(logRoot, account));
        var path = Path.Combine(directory.FullName, name);
        File.WriteAllText(path, "opaque binary log content is never parsed");
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
    }
}
