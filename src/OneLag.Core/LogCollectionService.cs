using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

public enum CollectionCategory
{
    OneDriveLog,
    WindowsLog,
    EventLog,
    CrashDump,
    SystemInfo,
    Other
}

public enum CollectionStatus
{
    Collected,
    SkippedTooLarge,
    SkippedTotalCap,
    SkippedCountCap,
    Truncated,
    Error
}

/// <summary>
/// One thing to collect. Either a file already on disk, or generated text (an event-log export, a command's
/// output). Producing these is the platform-specific part; turning them into a bounded, hashed, manifested
/// bundle is not, and lives in <see cref="LogCollectionService"/>.
/// </summary>
public abstract record CollectionItem(CollectionCategory Category, string RelativePath);

public sealed record FileCollectionItem(CollectionCategory Category, string RelativePath, string SourcePath)
    : CollectionItem(Category, RelativePath);

public sealed record TextCollectionItem(CollectionCategory Category, string RelativePath, string Content, string Source = "generated")
    : CollectionItem(Category, RelativePath);

public sealed record LogCollectionOptions(
    string OutputDirectory,
    long MaxTotalBytes = 2L * 1024 * 1024 * 1024,
    long MaxFileBytes = 100L * 1024 * 1024,
    int MaxFiles = 50_000,
    bool Zip = true,
    bool Overwrite = false);

public sealed record CollectedEntry(
    CollectionCategory Category,
    string RelativePath,
    long Bytes,
    string? Sha256,
    string Source,
    DateTimeOffset? LastWriteUtc,
    CollectionStatus Status,
    string? Note);

public sealed record LogCollectionResult(
    string Directory,
    string? ZipPath,
    string ManifestPath,
    IReadOnlyList<CollectedEntry> Entries,
    long TotalBytes)
{
    public int Collected => Entries.Count(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated);

    public int Skipped => Entries.Count(entry => entry.Status is CollectionStatus.SkippedTooLarge or CollectionStatus.SkippedTotalCap or CollectionStatus.SkippedCountCap);

    public int Errors => Entries.Count(entry => entry.Status == CollectionStatus.Error);
}

/// <summary>
/// Stages an arbitrary set of log files and generated text into one bounded, hashed, manifested bundle that
/// is easy to pull off the machine and reason over directly.
///
/// The point is to stop guessing at which logs matter and instead collect the actual bytes — every `.odl`,
/// every `.log` under the Windows tree, the recent event logs — so analysis runs over real evidence rather
/// than memory or a web search. That "collect everything" intent has two hard edges this class exists to
/// hold: the Windows tree contains multi-gigabyte logs and locked files, so collection is bounded by
/// per-file, total, and count caps that record what they dropped rather than failing or ballooning; and the
/// bytes are raw and unredacted by design, so a loud privacy notice and a complete manifest ship in the
/// bundle so the contents can be reviewed before the bundle goes anywhere.
/// </summary>
public sealed class LogCollectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LogCollectionResult Collect(LogCollectionOptions options, IEnumerable<CollectionItem> items, DateTimeOffset collectedAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(items);

        var directory = Path.GetFullPath(options.OutputDirectory);
        PrepareDirectory(directory, options.Overwrite);

        var entries = new List<CollectedEntry>();
        long totalBytes = 0;
        var fileCount = 0;
        var capReached = false;

        foreach (var item in items)
        {
            if (capReached)
            {
                break;
            }

            if (fileCount >= options.MaxFiles)
            {
                entries.Add(CapEntry(item.Category, CollectionStatus.SkippedCountCap, $"File-count cap of {options.MaxFiles:N0} reached; remaining items were not collected."));
                capReached = true;
                break;
            }

            var relative = SanitizeRelativePath(item.RelativePath);
            var destination = Path.Combine(directory, relative);

            switch (item)
            {
                case FileCollectionItem file:
                    capReached = !CollectFile(file, destination, relative, options, entries, ref totalBytes, ref fileCount);
                    break;
                case TextCollectionItem text:
                    capReached = !CollectText(text, destination, relative, options, entries, ref totalBytes, ref fileCount);
                    break;
            }
        }

        var manifestPath = WriteManifest(directory, entries, totalBytes, collectedAt);
        WriteReadme(directory, entries, totalBytes, collectedAt);
        WritePrivacyNotice(directory);
        WriteAnalysisPrompt(directory);

        var zipPath = options.Zip ? CreateZip(directory, options.Overwrite) : null;

        return new LogCollectionResult(directory, zipPath, manifestPath, entries, totalBytes);
    }

    private static bool CollectFile(
        FileCollectionItem file,
        string destination,
        string relative,
        LogCollectionOptions options,
        List<CollectedEntry> entries,
        ref long totalBytes,
        ref int fileCount)
    {
        long size;
        DateTimeOffset? lastWrite;
        try
        {
            var info = new FileInfo(file.SourcePath);
            if (!info.Exists)
            {
                entries.Add(new CollectedEntry(file.Category, relative, 0, null, file.SourcePath, null, CollectionStatus.Error, "Source file no longer exists."));
                return true;
            }

            size = info.Length;
            lastWrite = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries.Add(new CollectedEntry(file.Category, relative, 0, null, file.SourcePath, null, CollectionStatus.Error, ex.GetType().Name));
            return true;
        }

        if (size > options.MaxFileBytes)
        {
            entries.Add(new CollectedEntry(file.Category, relative, size, null, file.SourcePath, lastWrite, CollectionStatus.SkippedTooLarge,
                $"File is {Megabytes(size)} MB, above the {Megabytes(options.MaxFileBytes)} MB per-file cap."));
            return true;
        }

        if (totalBytes + size > options.MaxTotalBytes)
        {
            entries.Add(CapEntry(file.Category, CollectionStatus.SkippedTotalCap,
                $"Total-size cap of {Megabytes(options.MaxTotalBytes)} MB reached; remaining items were not collected."));
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
            // Copy through a share-all read stream because these logs are often held open by the writing
            // process; File.Copy would fail on exactly the busy files that matter most.
            using (var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }

            var hash = HashFile(destination);
            totalBytes += size;
            fileCount++;
            entries.Add(new CollectedEntry(file.Category, relative, size, hash, file.SourcePath, lastWrite, CollectionStatus.Collected, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries.Add(new CollectedEntry(file.Category, relative, size, null, file.SourcePath, lastWrite, CollectionStatus.Error, ex.GetType().Name));
        }

        return true;
    }

    private static bool CollectText(
        TextCollectionItem text,
        string destination,
        string relative,
        LogCollectionOptions options,
        List<CollectedEntry> entries,
        ref long totalBytes,
        ref int fileCount)
    {
        var content = text.Content ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(content);
        var status = CollectionStatus.Collected;
        string? note = null;

        if (bytes.Length > options.MaxFileBytes)
        {
            var cap = (int)Math.Min(options.MaxFileBytes, int.MaxValue);
            bytes = bytes[..cap];
            status = CollectionStatus.Truncated;
            note = $"Content truncated to the {Megabytes(options.MaxFileBytes)} MB per-file cap.";
        }

        if (totalBytes + bytes.Length > options.MaxTotalBytes)
        {
            entries.Add(CapEntry(text.Category, CollectionStatus.SkippedTotalCap,
                $"Total-size cap of {Megabytes(options.MaxTotalBytes)} MB reached; remaining items were not collected."));
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
            File.WriteAllBytes(destination, bytes);
            totalBytes += bytes.Length;
            fileCount++;
            entries.Add(new CollectedEntry(text.Category, relative, bytes.Length, HashBytes(bytes), text.Source, null, status, note));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries.Add(new CollectedEntry(text.Category, relative, bytes.Length, null, text.Source, null, CollectionStatus.Error, ex.GetType().Name));
        }

        return true;
    }

    /// <summary>
    /// A collected path is under the collector's control, but treating it as trusted would let a
    /// crafted source path escape the bundle directory. Drive letters, roots, and parent traversals are
    /// stripped so the relative path can only ever land inside the staging folder.
    /// </summary>
    internal static string SanitizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "unnamed";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var segments = relativePath
            .Split('\\', '/')
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0 && segment != "." && segment != "..")
            .Select(segment => segment.Replace(":", string.Empty))
            .Select(segment => new string(segment.Select(character => invalid.Contains(character) ? '_' : character).ToArray()))
            .Where(segment => segment.Length > 0)
            .ToArray();

        return segments.Length == 0 ? "unnamed" : string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static CollectedEntry CapEntry(CollectionCategory category, CollectionStatus status, string note)
    {
        return new CollectedEntry(category, "(collection stopped)", 0, null, "collector", null, status, note);
    }

    private static void PrepareDirectory(string directory, bool overwrite)
    {
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            if (!overwrite)
            {
                throw new IOException($"Output directory already exists and is not empty: {directory}. Use --overwrite to replace it.");
            }

            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
    }

    private string WriteManifest(string directory, IReadOnlyList<CollectedEntry> entries, long totalBytes, DateTimeOffset collectedAt)
    {
        var manifest = new
        {
            collectedAtUtc = collectedAt.ToUniversalTime(),
            totalBytes,
            totalFiles = entries.Count(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated),
            byCategory = entries
                .GroupBy(entry => entry.Category)
                .ToDictionary(
                    group => group.Key.ToString(),
                    group => new
                    {
                        collected = group.Count(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated),
                        bytes = group.Where(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated).Sum(entry => entry.Bytes)
                    }),
            entries
        };

        var path = Path.Combine(directory, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));
        return path;
    }

    private static void WriteReadme(string directory, IReadOnlyList<CollectedEntry> entries, long totalBytes, DateTimeOffset collectedAt)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# OneLag Log Collection");
        builder.AppendLine();
        builder.AppendLine($"- Collected: `{collectedAt.ToUniversalTime():O}`");
        builder.AppendLine($"- Total size: `{Megabytes(totalBytes):N1} MB`");
        builder.AppendLine($"- Files collected: `{entries.Count(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated):N0}`");
        builder.AppendLine();
        builder.AppendLine("This bundle contains raw, unredacted log files copied from this machine, so that analysis");
        builder.AppendLine("can run over the actual bytes instead of guessing at which logs matter.");
        builder.AppendLine();
        builder.AppendLine("## Contents");
        builder.AppendLine();

        foreach (var group in entries.GroupBy(entry => entry.Category).OrderBy(group => group.Key.ToString(), StringComparer.Ordinal))
        {
            var collected = group.Count(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated);
            var bytes = group.Where(entry => entry.Status is CollectionStatus.Collected or CollectionStatus.Truncated).Sum(entry => entry.Bytes);
            builder.AppendLine($"- **{group.Key}**: {collected:N0} file(s), {Megabytes(bytes):N1} MB");
        }

        var skipped = entries.Where(entry => entry.Status is CollectionStatus.SkippedTooLarge or CollectionStatus.SkippedTotalCap or CollectionStatus.SkippedCountCap or CollectionStatus.Error).ToArray();
        if (skipped.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## What Was Not Collected");
            builder.AppendLine();
            builder.AppendLine("See `manifest.json` for the full list. Nothing here was silently dropped.");
            builder.AppendLine();
            foreach (var group in skipped.GroupBy(entry => entry.Status))
            {
                builder.AppendLine($"- {group.Key}: {group.Count():N0} item(s)");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Files In This Bundle");
        builder.AppendLine();
        builder.AppendLine("- `manifest.json` - every item, with size, SHA-256, source path, and status.");
        builder.AppendLine("- `PRIVACY.txt` - what raw logs can contain; read before sharing.");
        builder.AppendLine("- `analysis-prompt.md` - a starting prompt for offline analysis.");

        File.WriteAllText(Path.Combine(directory, "README.md"), builder.ToString());
    }

    private static void WritePrivacyNotice(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "PRIVACY.txt"), """
        PRIVACY NOTICE
        ==============

        This bundle is RAW and UNREDACTED by design. To be useful for analysis it preserves the actual log
        contents, and those logs can contain:

        - Your user name, machine name, and full file paths.
        - Names of files and folders you have opened or synced.
        - URLs, tenant and account identifiers, and device identifiers.
        - Software versions and hardware serial numbers.

        It does NOT deliberately collect passwords or document contents, but raw logs have not been scrubbed
        and have not been reviewed line by line.

        Before sharing this bundle:
        - Review manifest.json to see exactly what is inside.
        - Share it only with people or tools you trust with the above.
        - Prefer a private, non-indexed transfer. Once shared, it may be cached or retained.

        For a redacted, curated summary suitable for wider sharing, use `onelag support bundle` instead.
        """);
    }

    private static void WriteAnalysisPrompt(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "analysis-prompt.md"), """
        # Prompt For Offline Analysis

        This bundle contains raw Windows logs collected to diagnose desktop lag, keyboard/mouse stutter, or
        Explorer freezes. OneDrive is one hypothesis among many, not the default.

        Start from `manifest.json` for the inventory, then reason over the actual files:

        - `eventlogs/` - recent Windows events, rendered as XML with message text. Look for display driver
          resets (Display / event 4101), disk I/O retries (disk / 153), storage timeouts (storahci, stornvme),
          Bluetooth transport errors (BTHUSB), WHEA hardware errors, Kernel-Power (41), Kernel-PnP, and
          DPC/ISR watchdog bugchecks. Correlate their timestamps with when the lag was felt.
        - `onedrive/` - OneDrive `.odl` logs. Treat volume and write frequency as churn evidence; do not
          assume the contents prove causation.
        - `windows/` - `.log` files from the Windows tree (CBS, DISM, Panther, setupapi, WindowsUpdate,
          storage, driver setup). Use these to identify recently installed or updated drivers.
        - `crashdumps/` - minidumps and live kernel reports, if any. A dump names the driver that faulted.
        - `systeminfo/` - installed drivers and system summary, to turn a driver file name into a product.

        Produce: the most likely cause with the specific evidence for and against it, the driver or component
        implicated if any, and the safest next step. Say plainly if the logs are inconclusive.
        """);
    }

    private static string? CreateZip(string directory, bool overwrite)
    {
        var zipPath = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".zip";
        if (File.Exists(zipPath))
        {
            if (!overwrite)
            {
                throw new IOException($"Zip file already exists: {zipPath}. Use --overwrite to replace it.");
            }

            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(directory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zipPath;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static string HashBytes(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static double Megabytes(long bytes) => bytes / 1024.0 / 1024.0;
}
