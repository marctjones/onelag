using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneLag.Core;

public sealed record MovePlanOptions(
    string Source,
    string Destination,
    string OutputDirectory,
    int MaxItems = 100_000);

public sealed record MovePlanSummary(
    string Source,
    string Destination,
    long FileCount,
    long DirectoryCount,
    long TotalBytes,
    bool WasCapped,
    long? DestinationAvailableBytes,
    bool? DestinationHasEnoughSpace,
    IReadOnlyList<string> InaccessiblePaths,
    IReadOnlyList<string> Files);

public static class MovePlanWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static MovePlanSummary Write(MovePlanOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Source))
        {
            throw new ArgumentException("Move-plan source is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Destination))
        {
            throw new ArgumentException("Move-plan destination is required.", nameof(options));
        }

        if (options.MaxItems <= 0)
        {
            throw new ArgumentException("Move-plan max items must be greater than zero.", nameof(options));
        }

        var source = Path.GetFullPath(options.Source);
        var destination = Path.GetFullPath(options.Destination);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Move-plan source does not exist: {source}");
        }

        if (IsSameOrChild(destination, source))
        {
            throw new ArgumentException("Move-plan destination must not be inside the source directory.");
        }

        if (IsSameOrChild(source, destination))
        {
            throw new ArgumentException("Move-plan source must not be inside the destination directory.");
        }

        Directory.CreateDirectory(outputDirectory);
        var summary = Inspect(source, destination, options.MaxItems, cancellationToken);

        File.WriteAllText(Path.Combine(outputDirectory, "move-plan.json"), JsonSerializer.Serialize(summary, JsonOptions));
        File.WriteAllText(Path.Combine(outputDirectory, "move-plan.md"), BuildMarkdown(summary));
        File.WriteAllText(Path.Combine(outputDirectory, "Move-OneLagItems.ps1"), BuildMoveScript(summary));
        File.WriteAllText(Path.Combine(outputDirectory, "Rollback-OneLagMove.ps1"), BuildRollbackScript(summary));
        File.WriteAllText(Path.Combine(outputDirectory, "Verify-OneLagMove.ps1"), BuildVerifyScript(summary));

        return summary;
    }

    private static MovePlanSummary Inspect(string source, string destination, int maxItems, CancellationToken cancellationToken)
    {
        long fileCount = 0;
        long directoryCount = 0;
        long bytes = 0;
        var capped = false;
        var inaccessible = new List<string>();
        var sampleFiles = new List<string>();
        var stack = new Stack<string>();
        stack.Push(source);

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileCount + directoryCount >= maxItems)
            {
                capped = true;
                break;
            }

            var current = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(
                    current,
                    "*",
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = 0
                    });
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or PathTooLongException)
            {
                inaccessible.Add(current);
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fileCount + directoryCount >= maxItems)
                {
                    capped = true;
                    break;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or DirectoryNotFoundException or PathTooLongException)
                {
                    inaccessible.Add(entry);
                    continue;
                }

                if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    directoryCount++;
                    stack.Push(entry);
                }
                else
                {
                    fileCount++;
                    if (sampleFiles.Count < 100)
                    {
                        sampleFiles.Add(entry);
                    }

                    try
                    {
                        bytes += Math.Max(0, new FileInfo(entry).Length);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or PathTooLongException)
                    {
                        inaccessible.Add(entry);
                    }
                }
            }
        }

        var available = TryGetAvailableBytes(destination);
        var required = (long)Math.Ceiling(bytes * 1.10);
        var enoughSpace = available.HasValue ? available.Value >= required : (bool?)null;

        return new MovePlanSummary(
            source,
            destination,
            fileCount,
            directoryCount,
            bytes,
            capped,
            available,
            enoughSpace,
            inaccessible,
            sampleFiles);
    }

    private static long? TryGetAvailableBytes(string destination)
    {
        try
        {
            var root = Path.GetPathRoot(destination);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string BuildMarkdown(MovePlanSummary summary)
    {
        return $"""
        # OneLag Move Plan

        This plan is generated for review. It does not move files until you run `Move-OneLagItems.ps1` with explicit execution flags.

        ## Source And Destination

        - Source: `{summary.Source}`
        - Destination: `{summary.Destination}`

        ## Inventory

        - Files: `{summary.FileCount:N0}`
        - Directories: `{summary.DirectoryCount:N0}`
        - Bytes: `{summary.TotalBytes:N0}`
        - Capped: `{summary.WasCapped}`
        - Inaccessible paths: `{summary.InaccessiblePaths.Count:N0}`
        - Destination available bytes: `{FormatNullable(summary.DestinationAvailableBytes)}`
        - Destination has enough space including 10 percent buffer: `{summary.DestinationHasEnoughSpace?.ToString() ?? "unknown"}`

        ## Execute

        Open PowerShell, review the script, then run:

        ```powershell
        .\Move-OneLagItems.ps1
        .\Move-OneLagItems.ps1 -Execute -IUnderstandMovesFiles
        .\Verify-OneLagMove.ps1
        ```

        ## Rollback

        If the move succeeded but you need to revert it before changing OneDrive settings:

        ```powershell
        .\Rollback-OneLagMove.ps1 -Execute -IUnderstandMovesFiles
        ```

        ## Safety Notes

        - Pause OneDrive before moving a synced folder.
        - Close apps that may hold files open.
        - Confirm backup and work policy before executing.
        - Move folders out of OneDrive only after reviewing this plan.
        - Do not run rollback after new files have been created in both locations without manually reconciling them.
        """ + Environment.NewLine;
    }

    private static string BuildMoveScript(MovePlanSummary summary)
    {
        return $$"""
            param(
                [switch]$Execute,
                [switch]$IUnderstandMovesFiles
        )

            $ErrorActionPreference = "Stop"
            $Source = "{{EscapePowerShell(summary.Source)}}"
            $Destination = "{{EscapePowerShell(summary.Destination)}}"

        Write-Host "OneLag move plan"
        Write-Host "Source: $Source"
        Write-Host "Destination: $Destination"

        if (-not (Test-Path -LiteralPath $Source)) { throw "Source does not exist: $Source" }

        if (Test-Path -LiteralPath $Destination) { throw "Destination already exists. Choose an empty destination or reconcile manually: $Destination" }

        if (-not $Execute) {
            Write-Host "[dry-run] Would create destination parent and move source to destination."
            Write-Host "[dry-run] Re-run with -Execute -IUnderstandMovesFiles to perform the move."
            exit 0
        }

        if (-not $IUnderstandMovesFiles) { throw "Execution requires -IUnderstandMovesFiles." }

        $destinationParent = Split-Path -Parent $Destination
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        Move-Item -LiteralPath $Source -Destination $Destination
        Write-Host "Move complete. Run Verify-OneLagMove.ps1 next."
        """ + Environment.NewLine;
    }

    private static string BuildRollbackScript(MovePlanSummary summary)
    {
        return $$"""
            param(
                [switch]$Execute,
                [switch]$IUnderstandMovesFiles
        )

            $ErrorActionPreference = "Stop"
            $OriginalSource = "{{EscapePowerShell(summary.Source)}}"
            $MovedDestination = "{{EscapePowerShell(summary.Destination)}}"

        Write-Host "OneLag rollback plan"
        Write-Host "Current location: $MovedDestination"
        Write-Host "Rollback location: $OriginalSource"

        if (-not (Test-Path -LiteralPath $MovedDestination)) { throw "Moved destination does not exist: $MovedDestination" }

        if (Test-Path -LiteralPath $OriginalSource) { throw "Original source already exists. Reconcile manually before rollback: $OriginalSource" }

        if (-not $Execute) {
            Write-Host "[dry-run] Would move destination back to original source."
            Write-Host "[dry-run] Re-run with -Execute -IUnderstandMovesFiles to perform rollback."
            exit 0
        }

        if (-not $IUnderstandMovesFiles) { throw "Execution requires -IUnderstandMovesFiles." }

        $sourceParent = Split-Path -Parent $OriginalSource
        New-Item -ItemType Directory -Force -Path $sourceParent | Out-Null
        Move-Item -LiteralPath $MovedDestination -Destination $OriginalSource
        Write-Host "Rollback complete."
        """ + Environment.NewLine;
    }

    private static string BuildVerifyScript(MovePlanSummary summary)
    {
        return $$"""
            $Source = "{{EscapePowerShell(summary.Source)}}"
            $Destination = "{{EscapePowerShell(summary.Destination)}}"

        Write-Host "OneLag move verification"
        Write-Host "Source exists: $(Test-Path -LiteralPath $Source)"
        Write-Host "Destination exists: $(Test-Path -LiteralPath $Destination)"

        if (Test-Path -LiteralPath $Destination) {
            $files = Get-ChildItem -LiteralPath $Destination -Recurse -Force -File -ErrorAction SilentlyContinue
            $directories = Get-ChildItem -LiteralPath $Destination -Recurse -Force -Directory -ErrorAction SilentlyContinue
            Write-Host "Destination files: $($files.Count)"
            Write-Host "Destination directories: $($directories.Count)"
        }
        """ + Environment.NewLine;
    }

    private static bool IsSameOrChild(string path, string possibleParent)
    {
        var normalizedPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        var normalizedParent = EnsureTrailingSeparator(Path.GetFullPath(possibleParent));
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string EscapePowerShell(string value)
    {
        return value.Replace("`", "``", StringComparison.Ordinal).Replace("\"", "`\"", StringComparison.Ordinal);
    }

    private static string FormatNullable(long? value)
    {
        return value.HasValue ? value.Value.ToString("N0") : "unknown";
    }
}
