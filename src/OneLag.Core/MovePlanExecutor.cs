namespace OneLag.Core;

public sealed record MoveExecutionOptions(
    string Source,
    string Destination,
    bool Execute,
    bool Acknowledged,
    int MaxItems = 100_000);

public sealed record MoveExecutionResult(
    string Operation,
    string Source,
    string Destination,
    bool Executed,
    bool SourceExists,
    bool DestinationExists,
    long FileCount,
    long DirectoryCount,
    long TotalBytes,
    bool WasCapped,
    long? DestinationAvailableBytes,
    bool? DestinationHasEnoughSpace,
    IReadOnlyList<string> InaccessiblePaths,
    string Message);

public static class MovePlanExecutor
{
    public static MoveExecutionResult Move(MoveExecutionOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        var source = Path.GetFullPath(options.Source);
        var destination = Path.GetFullPath(options.Destination);
        ValidateRelationship(source, destination);

        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Move source does not exist: {source}");
        }

        if (Directory.Exists(destination) || File.Exists(destination))
        {
            throw new IOException($"Move destination already exists: {destination}");
        }

        var inventory = Inspect(source, destination, options.MaxItems, cancellationToken);
        if (inventory.DestinationHasEnoughSpace == false)
        {
            throw new IOException("Destination does not have enough available space for the move plus a 10 percent buffer.");
        }

        if (!options.Execute)
        {
            return inventory with
            {
                Operation = "move",
                Executed = false,
                Message = "Dry run only. Re-run with --execute --i-understand-moves-files to perform the move."
            };
        }

        RequireAcknowledgement(options);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? Environment.CurrentDirectory);
        Directory.Move(source, destination);

        return inventory with
        {
            Operation = "move",
            Executed = true,
            SourceExists = Directory.Exists(source),
            DestinationExists = Directory.Exists(destination),
            Message = "Move complete. Run remediate verify next."
        };
    }

    public static MoveExecutionResult Rollback(MoveExecutionOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        var originalSource = Path.GetFullPath(options.Source);
        var movedDestination = Path.GetFullPath(options.Destination);
        ValidateRelationship(originalSource, movedDestination);

        if (!Directory.Exists(movedDestination))
        {
            throw new DirectoryNotFoundException($"Rollback destination does not exist: {movedDestination}");
        }

        if (Directory.Exists(originalSource) || File.Exists(originalSource))
        {
            throw new IOException($"Original source already exists. Reconcile manually before rollback: {originalSource}");
        }

        var inventory = Inspect(movedDestination, originalSource, options.MaxItems, cancellationToken);
        if (!options.Execute)
        {
            return inventory with
            {
                Operation = "rollback",
                Source = originalSource,
                Destination = movedDestination,
                Executed = false,
                Message = "Dry run only. Re-run with --execute --i-understand-moves-files to perform rollback."
            };
        }

        RequireAcknowledgement(options);
        Directory.CreateDirectory(Path.GetDirectoryName(originalSource) ?? Environment.CurrentDirectory);
        Directory.Move(movedDestination, originalSource);

        return inventory with
        {
            Operation = "rollback",
            Source = originalSource,
            Destination = movedDestination,
            Executed = true,
            SourceExists = Directory.Exists(originalSource),
            DestinationExists = Directory.Exists(movedDestination),
            Message = "Rollback complete."
        };
    }

    public static MoveExecutionResult Verify(string source, string destination, int maxItems, CancellationToken cancellationToken)
    {
        if (maxItems <= 0)
        {
            throw new ArgumentException("Verify max items must be greater than zero.", nameof(maxItems));
        }

        var fullSource = Path.GetFullPath(source);
        var fullDestination = Path.GetFullPath(destination);
        ValidateRelationship(fullSource, fullDestination);

        var target = Directory.Exists(fullDestination) ? fullDestination : fullSource;
        var inventory = Directory.Exists(target)
            ? Inspect(target, fullDestination, maxItems, cancellationToken)
            : Empty("verify", fullSource, fullDestination, "Neither source nor destination exists.");

        return inventory with
        {
            Operation = "verify",
            Source = fullSource,
            Destination = fullDestination,
            Executed = false,
            SourceExists = Directory.Exists(fullSource),
            DestinationExists = Directory.Exists(fullDestination),
            Message = $"Source exists: {Directory.Exists(fullSource)}. Destination exists: {Directory.Exists(fullDestination)}."
        };
    }

    private static void Validate(MoveExecutionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Source))
        {
            throw new ArgumentException("Source is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Destination))
        {
            throw new ArgumentException("Destination is required.", nameof(options));
        }

        if (options.MaxItems <= 0)
        {
            throw new ArgumentException("Max items must be greater than zero.", nameof(options));
        }
    }

    private static void RequireAcknowledgement(MoveExecutionOptions options)
    {
        if (!options.Acknowledged)
        {
            throw new ArgumentException("--execute requires --i-understand-moves-files.");
        }
    }

    private static MoveExecutionResult Inspect(string source, string destination, int maxItems, CancellationToken cancellationToken)
    {
        long fileCount = 0;
        long directoryCount = 0;
        long bytes = 0;
        var capped = false;
        var inaccessible = new List<string>();
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
        return new MoveExecutionResult(
            "inspect",
            source,
            destination,
            false,
            Directory.Exists(source),
            Directory.Exists(destination),
            fileCount,
            directoryCount,
            bytes,
            capped,
            available,
            available.HasValue ? available.Value >= required : null,
            inaccessible,
            "Inspection complete.");
    }

    private static MoveExecutionResult Empty(string operation, string source, string destination, string message)
    {
        return new MoveExecutionResult(
            operation,
            source,
            destination,
            false,
            Directory.Exists(source),
            Directory.Exists(destination),
            0,
            0,
            0,
            false,
            TryGetAvailableBytes(destination),
            null,
            Array.Empty<string>(),
            message);
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

    private static void ValidateRelationship(string source, string destination)
    {
        if (IsSameOrChild(destination, source))
        {
            throw new ArgumentException("Destination must not be inside the source directory.");
        }

        if (IsSameOrChild(source, destination))
        {
            throw new ArgumentException("Source must not be inside the destination directory.");
        }
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
}
