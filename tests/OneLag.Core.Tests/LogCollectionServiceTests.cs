using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OneLag.Core;

namespace OneLag.Core.Tests;

/// <summary>
/// The collection engine is platform-neutral: it stages an arbitrary set of items into a bounded, hashed,
/// manifested bundle. The Windows collector supplies real items; here the items are synthetic, so the caps,
/// the manifest, the path sanitisation, and the zip are all exercised without touching Windows.
/// </summary>
public sealed class LogCollectionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-13T10:00:00Z");

    private readonly string workspace = Path.Combine(Path.GetTempPath(), "onelag-collect-tests", Guid.NewGuid().ToString("N"));

    public LogCollectionServiceTests()
    {
        Directory.CreateDirectory(workspace);
    }

    [Fact]
    public void CollectsFilesAndTextIntoAManifestedBundle()
    {
        var log = SourceFile("onedrive.odl", "opaque binary", Now.AddMinutes(-2));
        var options = Options(zip: false);

        var result = new LogCollectionService().Collect(
            options,
            new CollectionItem[]
            {
                new FileCollectionItem(CollectionCategory.OneDriveLog, "onedrive/general.odl", log),
                new TextCollectionItem(CollectionCategory.EventLog, "eventlogs/System.xml", "<Events><Event/></Events>")
            },
            Now);

        Assert.Equal(2, result.Collected);
        Assert.True(File.Exists(Path.Combine(result.Directory, "onedrive", "general.odl")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "eventlogs", "System.xml")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "PRIVACY.txt")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "analysis-prompt.md")));

        var manifest = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(result.ManifestPath));
        Assert.Equal(2, manifest.GetProperty("totalFiles").GetInt32());
        Assert.NotEqual(0, manifest.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public void EveryCollectedEntryCarriesAShaThatMatchesTheStagedBytes()
    {
        var source = SourceFile("a.log", "the actual bytes", Now);

        var result = new LogCollectionService().Collect(
            Options(zip: false),
            new[] { new FileCollectionItem(CollectionCategory.WindowsLog, "windows/a.log", source) },
            Now);

        var entry = result.Entries.Single(candidate => candidate.Status == CollectionStatus.Collected);
        var staged = Path.Combine(result.Directory, entry.RelativePath);
        var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(staged)));

        Assert.Equal(actualHash, entry.Sha256);
    }

    [Fact]
    public void AFileOverThePerFileCapIsSkippedAndRecordedRatherThanCopied()
    {
        var big = SourceFile("huge.log", new string('x', 4_000), Now);

        var result = new LogCollectionService().Collect(
            Options(zip: false, maxFileBytes: 1_000),
            new[] { new FileCollectionItem(CollectionCategory.WindowsLog, "windows/huge.log", big) },
            Now);

        Assert.Equal(0, result.Collected);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(CollectionStatus.SkippedTooLarge, entry.Status);
        Assert.False(File.Exists(Path.Combine(result.Directory, "windows", "huge.log")));
        // A skipped file is still in the manifest, so nothing is silently missing.
        Assert.Contains("per-file cap", entry.Note);
    }

    [Fact]
    public void TheTotalCapStopsCollectionAndTheRemainderIsNotWalked()
    {
        var items = Enumerable.Range(0, 50)
            .Select(index => new FileCollectionItem(
                CollectionCategory.WindowsLog,
                $"windows/log-{index}.log",
                SourceFile($"log-{index}.log", new string('y', 800), Now)))
            .Cast<CollectionItem>()
            .ToArray();

        var result = new LogCollectionService().Collect(
            Options(zip: false, maxTotalBytes: 2_500),
            items,
            Now);

        Assert.True(result.Collected is >= 2 and <= 4, $"expected a few files under a 2,500 byte cap, got {result.Collected}");
        Assert.Contains(result.Entries, entry => entry.Status == CollectionStatus.SkippedTotalCap);
        Assert.True(result.TotalBytes <= 2_500);
    }

    [Fact]
    public void TheFileCountCapStopsCollection()
    {
        var items = Enumerable.Range(0, 20)
            .Select(index => new TextCollectionItem(CollectionCategory.EventLog, $"eventlogs/c{index}.xml", "<Events/>"))
            .Cast<CollectionItem>()
            .ToArray();

        var result = new LogCollectionService().Collect(
            Options(zip: false, maxFiles: 5),
            items,
            Now);

        Assert.Equal(5, result.Collected);
        Assert.Contains(result.Entries, entry => entry.Status == CollectionStatus.SkippedCountCap);
    }

    [Fact]
    public void OversizeGeneratedTextIsTruncatedNotDropped()
    {
        var result = new LogCollectionService().Collect(
            Options(zip: false, maxFileBytes: 100),
            new[] { new TextCollectionItem(CollectionCategory.EventLog, "eventlogs/big.xml", new string('z', 5_000)) },
            Now);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(CollectionStatus.Truncated, entry.Status);
        Assert.Equal(100, new FileInfo(Path.Combine(result.Directory, entry.RelativePath)).Length);
    }

    [Theory]
    [InlineData(@"C:\Windows\Logs\CBS\CBS.log", "C/Windows/Logs/CBS/CBS.log")]
    [InlineData("../../etc/passwd", "etc/passwd")]
    [InlineData(@"..\..\Windows\System32\x.log", "Windows/System32/x.log")]
    [InlineData("eventlogs/System.xml", "eventlogs/System.xml")]
    public void RelativePathsAreSanitisedToStayInsideTheBundle(string input, string expectedUnix)
    {
        var expected = expectedUnix.Replace('/', Path.DirectorySeparatorChar);
        Assert.Equal(expected, LogCollectionService.SanitizeRelativePath(input));
    }

    [Fact]
    public void ASanitisedPathCannotEscapeTheBundleDirectory()
    {
        var result = new LogCollectionService().Collect(
            Options(zip: false),
            new[] { new TextCollectionItem(CollectionCategory.Other, @"..\..\..\escape.txt", "nope") },
            Now);

        var entry = Assert.Single(result.Entries);
        var staged = Path.GetFullPath(Path.Combine(result.Directory, entry.RelativePath));
        Assert.StartsWith(Path.GetFullPath(result.Directory), staged, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.GetFullPath(Path.Combine(result.Directory, "..", "..", "..", "escape.txt"))));
    }

    [Fact]
    public void ZippingProducesASinglePullablePackageContainingTheManifest()
    {
        var result = new LogCollectionService().Collect(
            Options(zip: true),
            new[] { new TextCollectionItem(CollectionCategory.EventLog, "eventlogs/System.xml", "<Events/>") },
            Now);

        Assert.NotNull(result.ZipPath);
        Assert.True(File.Exists(result.ZipPath));

        using var archive = ZipFile.OpenRead(result.ZipPath!);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("manifest.json", StringComparison.Ordinal));
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("PRIVACY.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void AMissingSourceFileIsRecordedAsAnErrorRatherThanThrowing()
    {
        var result = new LogCollectionService().Collect(
            Options(zip: false),
            new[] { new FileCollectionItem(CollectionCategory.WindowsLog, "windows/gone.log", Path.Combine(workspace, "does-not-exist.log")) },
            Now);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(CollectionStatus.Error, entry.Status);
    }

    [Fact]
    public void RefusesToOverwriteANonEmptyDirectoryUnlessAsked()
    {
        var options = Options(zip: false);
        Directory.CreateDirectory(options.OutputDirectory);
        File.WriteAllText(Path.Combine(options.OutputDirectory, "existing.txt"), "keep me");

        Assert.Throws<IOException>(() => new LogCollectionService().Collect(
            options,
            new[] { new TextCollectionItem(CollectionCategory.Other, "x.txt", "data") },
            Now));

        var overwrite = options with { Overwrite = true };
        var result = new LogCollectionService().Collect(
            overwrite,
            new[] { new TextCollectionItem(CollectionCategory.Other, "x.txt", "data") },
            Now);

        Assert.Equal(1, result.Collected);
        Assert.False(File.Exists(Path.Combine(result.Directory, "existing.txt")));
    }

    private LogCollectionOptions Options(
        bool zip,
        long maxTotalBytes = 100L * 1024 * 1024,
        long maxFileBytes = 100L * 1024 * 1024,
        int maxFiles = 50_000)
    {
        return new LogCollectionOptions(
            Path.Combine(workspace, "bundle"),
            MaxTotalBytes: maxTotalBytes,
            MaxFileBytes: maxFileBytes,
            MaxFiles: maxFiles,
            Zip: zip);
    }

    private string SourceFile(string name, string content, DateTimeOffset lastWrite)
    {
        var sources = Directory.CreateDirectory(Path.Combine(workspace, "sources"));
        var path = Path.Combine(sources.FullName, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWrite.UtcDateTime);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
