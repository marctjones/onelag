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

    public InventorySummary Scan(string root, int maxItems, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Root path is required.", nameof(root));
        }

        var normalizedRoot = Path.GetFullPath(root);
        var stack = new Stack<(string Path, int Depth, string? TopLevelPath, string? TopLevelName)>();
        var inaccessible = new List<string>();
        var risks = new Dictionary<string, DirectoryRisk>(StringComparer.OrdinalIgnoreCase);
        var blockers = new List<SyncBlocker>();
        var topLevelItems = new Dictionary<string, TopLevelInventoryBuilder>(StringComparer.OrdinalIgnoreCase);

        long fileCount = 0;
        long directoryCount = 0;
        long totalBytes = 0;
        var maxDepth = 0;
        var capped = false;

        stack.Push((normalizedRoot, 0, null, null));
        blockers.AddRange(OneDriveKnownIssueRules.InspectRoot(normalizedRoot));

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (fileCount + directoryCount >= maxItems)
            {
                capped = true;
                break;
            }

            var (currentDirectory, depth, currentTopLevelPath, currentTopLevelName) = stack.Pop();
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

            var seenNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var childEntryCount = 0;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (fileCount + directoryCount >= maxItems)
                {
                    capped = true;
                    break;
                }

                childEntryCount++;
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
                var isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
                var entryTopLevelPath = currentTopLevelPath ?? entry;
                var entryTopLevelName = currentTopLevelName ?? name;
                var normalizedName = name.Normalize();
                if (seenNames.TryGetValue(normalizedName, out var existingEntry))
                {
                    blockers.Add(OneDriveKnownIssueRules.CreateDuplicateNameBlocker(entry, existingEntry));
                }
                else
                {
                    seenNames[normalizedName] = entry;
                }

                blockers.AddRange(OneDriveKnownIssueRules.InspectEntry(normalizedRoot, entry, name, attributes, isDirectory, depth));

                if (isDirectory)
                {
                    directoryCount++;
                    GetTopLevel(topLevelItems, entryTopLevelPath, entryTopLevelName).DirectoryCount++;

                    if (HighRiskDirectoryNames.Contains(name))
                    {
                        risks.TryAdd(entry, new DirectoryRisk(entry, name, "high-churn development or build directory", 1));
                    }

                    if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    {
                        blockers.Add(new SyncBlocker(entry, "reparse-point", "OneDrive does not support syncing through symbolic links or junction points.", Severity.HighRisk));
                        continue;
                    }

                    stack.Push((entry, depth + 1, entryTopLevelPath, entryTopLevelName));
                }
                else
                {
                    fileCount++;
                    var topLevel = GetTopLevel(topLevelItems, entryTopLevelPath, entryTopLevelName);
                    topLevel.FileCount++;

                    try
                    {
                        var info = new FileInfo(entry);
                        totalBytes += Math.Max(0, info.Length);
                        topLevel.TotalBytes += Math.Max(0, info.Length);
                        blockers.AddRange(OneDriveKnownIssueRules.InspectFile(entry, info));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or PathTooLongException)
                    {
                        inaccessible.Add(entry);
                    }
                }
            }

            if (childEntryCount > 50_000)
            {
                blockers.Add(OneDriveKnownIssueRules.CreateLargeFolderSharingBlocker(currentDirectory, childEntryCount));
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
            topLevelItems.Values
                .Select(item => item.ToInventory())
                .OrderByDescending(item => item.TotalItems)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            risks.Values.OrderBy(risk => risk.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            blockers
                .OrderByDescending(blocker => SeverityRank(blocker.Severity))
                .ThenBy(blocker => blocker.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(blocker => blocker.Path, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToArray());
    }

    private static int SeverityRank(Severity severity) => severity switch
    {
        Severity.Emergency => 4,
        Severity.HighRisk => 3,
        Severity.Warning => 2,
        Severity.Info => 1,
        _ => 0
    };

    private static TopLevelInventoryBuilder GetTopLevel(
        Dictionary<string, TopLevelInventoryBuilder> topLevelItems,
        string path,
        string name)
    {
        if (!topLevelItems.TryGetValue(path, out var item))
        {
            item = new TopLevelInventoryBuilder(path, name);
            topLevelItems[path] = item;
        }

        return item;
    }

    private sealed class TopLevelInventoryBuilder
    {
        public TopLevelInventoryBuilder(string path, string name)
        {
            Path = path;
            Name = name;
        }

        public string Path { get; }

        public string Name { get; }

        public long FileCount { get; set; }

        public long DirectoryCount { get; set; }

        public long TotalBytes { get; set; }

        public TopLevelInventory ToInventory()
        {
            return new TopLevelInventory(Path, Name, FileCount, DirectoryCount, TotalBytes);
        }
    }
}
