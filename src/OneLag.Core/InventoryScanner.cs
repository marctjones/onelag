namespace OneLag.Core;

public sealed class InventoryScanner
{
    private static readonly HashSet<string> HighRiskDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
        "node_modules",
        ".venv",
        "venv",
        "env",
        "bin",
        "obj",
        "target",
        ".gradle",
        ".next",
        ".nuxt",
        "dist",
        "build",
        "coverage",
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        ".terraform"
    };

    private static readonly HashSet<string> LargeRiskExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pst",
        ".ost",
        ".zip",
        ".7z",
        ".rar",
        ".tar",
        ".gz",
        ".mp4",
        ".mov",
        ".mkv",
        ".iso",
        ".vhd",
        ".vhdx"
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9"
    };

    public InventorySummary Scan(string root, int maxItems, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Root path is required.", nameof(root));
        }

        var normalizedRoot = Path.GetFullPath(root);
        var stack = new Stack<(string Path, int Depth)>();
        var inaccessible = new List<string>();
        var risks = new Dictionary<string, DirectoryRisk>(StringComparer.OrdinalIgnoreCase);
        var blockers = new List<SyncBlocker>();

        long fileCount = 0;
        long directoryCount = 0;
        long totalBytes = 0;
        var maxDepth = 0;
        var capped = false;

        stack.Push((normalizedRoot, 0));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fileCount + directoryCount >= maxItems)
            {
                capped = true;
                break;
            }

            var (currentDirectory, depth) = stack.Pop();
            maxDepth = Math.Max(maxDepth, depth);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(
                    currentDirectory,
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
                inaccessible.Add(currentDirectory);
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

                var name = Path.GetFileName(entry);
                AddPathBlockers(blockers, entry, name, attributes);

                if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                {
                    directoryCount++;

                    if (HighRiskDirectoryNames.Contains(name))
                    {
                        risks.TryAdd(entry, new DirectoryRisk(entry, name, "high-churn development or build directory", 1));
                    }

                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        blockers.Add(new SyncBlocker(entry, "reparse-point", "OneDrive does not support syncing through symbolic links or junction points.", Severity.HighRisk));
                        continue;
                    }

                    stack.Push((entry, depth + 1));
                }
                else
                {
                    fileCount++;

                    try
                    {
                        var info = new FileInfo(entry);
                        totalBytes += Math.Max(0, info.Length);
                        AddFileBlockers(blockers, entry, info);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or PathTooLongException)
                    {
                        inaccessible.Add(entry);
                    }
                }
            }
        }

        return new InventorySummary(
            normalizedRoot,
            fileCount,
            directoryCount,
            totalBytes,
            maxDepth,
            capped,
            inaccessible,
            risks.Values.OrderBy(risk => risk.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            blockers.Take(250).ToArray());
    }

    private static void AddPathBlockers(List<SyncBlocker> blockers, string path, string name, FileAttributes attributes)
    {
        if (path.Length > 400)
        {
            blockers.Add(new SyncBlocker(path, "long-path", "Decoded OneDrive paths over 400 characters are sync risks.", Severity.Warning));
        }

        if (name.Length > 255)
        {
            blockers.Add(new SyncBlocker(path, "long-segment", "Path segment exceeds the common 255-character segment limit.", Severity.HighRisk));
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (ReservedWindowsNames.Contains(baseName))
        {
            blockers.Add(new SyncBlocker(path, "reserved-name", "Reserved Windows device name cannot safely sync.", Severity.HighRisk));
        }

        if (name is ".lock" or "_vti_" or "desktop.ini" || name.StartsWith("~$", StringComparison.Ordinal))
        {
            blockers.Add(new SyncBlocker(path, "blocked-name", "Name is blocked or special-cased by OneDrive guidance.", Severity.Warning));
        }

        if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
        {
            blockers.Add(new SyncBlocker(path, "hidden", "Hidden files can explain sync-pending states when no visible file appears responsible.", Severity.Info));
        }
    }

    private static void AddFileBlockers(List<SyncBlocker> blockers, string path, FileInfo info)
    {
        if (info.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || info.Name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new SyncBlocker(path, "temporary-file", "Temporary files can block or prolong OneDrive sync and may belong to another app.", Severity.Warning));
        }

        if (LargeRiskExtensions.Contains(info.Extension) && info.Length >= 250L * 1024 * 1024)
        {
            blockers.Add(new SyncBlocker(path, "large-risk-file", "Large archive, media, or mail data files can prolong processing changes.", Severity.Warning));
        }
    }
}
