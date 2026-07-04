namespace OneLag.Core;

public static class OneDriveKnownIssueRules
{
    public const int MaximumDecodedRelativePathLength = 400;
    public const int MaximumLocalSyncPathLength = 520;
    public const int MaximumPathSegmentLength = 255;
    public const int ExplorerPathWarningLength = 256;
    public const long MaximumSyncFileSizeBytes = 250L * 1024 * 1024 * 1024;
    public const long PreviewGenerationLimitBytes = 100L * 1024 * 1024;

    private static readonly char[] InvalidNameCharacters =
    {
        '"',
        '*',
        ':',
        '<',
        '>',
        '?',
        '/',
        '\\',
        '|'
    };

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM0",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT0",
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

    private static readonly HashSet<string> MailDataExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pst",
        ".ost"
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

    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".jpg",
        ".jpeg",
        ".png",
        ".gif",
        ".bmp",
        ".tif",
        ".tiff",
        ".webp"
    };

    private static readonly HashSet<string> OneNoteExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".one",
        ".onetoc2",
        ".onepkg"
    };

    public static IReadOnlyList<SyncBlocker> InspectRoot(string root)
    {
        var blockers = new List<SyncBlocker>();

        try
        {
            var attributes = File.GetAttributes(root);
            if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                blockers.Add(new SyncBlocker(root, "root-reparse-point", "OneDrive does not support sync roots that rely on symbolic links or junction points.", Severity.HighRisk));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            blockers.Add(new SyncBlocker(root, "root-inaccessible", "The configured sync root could not be inspected.", Severity.Warning));
        }

        try
        {
            var pathRoot = Path.GetPathRoot(root);
            if (!string.IsNullOrWhiteSpace(pathRoot))
            {
                var drive = new DriveInfo(pathRoot);
                if (drive.DriveType == DriveType.Network)
                {
                    blockers.Add(new SyncBlocker(root, "network-sync-location", "OneDrive does not support using a network or mapped drive as the sync location.", Severity.HighRisk));
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            blockers.Add(new SyncBlocker(root, "root-drive-unknown", "The sync root drive type could not be inspected.", Severity.Info));
        }

        return blockers;
    }

    public static IReadOnlyList<SyncBlocker> InspectEntry(
        string root,
        string path,
        string name,
        FileAttributes attributes,
        bool isDirectory,
        int parentDepth)
    {
        var blockers = new List<SyncBlocker>();
        var relativePath = SafeRelativePath(root, path);
        var normalizedName = name.Normalize();

        if (relativePath.Length > MaximumDecodedRelativePathLength)
        {
            blockers.Add(new SyncBlocker(path, "long-onedrive-relative-path", "Decoded OneDrive relative paths over 400 characters are not supported.", Severity.HighRisk));
        }

        if (path.Length > MaximumLocalSyncPathLength)
        {
            blockers.Add(new SyncBlocker(path, "long-local-sync-path", "OneDrive root plus relative path over 520 characters is not supported for synced PC paths.", Severity.HighRisk));
        }

        if (path.Length > ExplorerPathWarningLength)
        {
            blockers.Add(new SyncBlocker(path, "windows-explorer-path-limit", "Windows Explorer can still have lower path handling limits than the OneDrive service limit.", Severity.Warning));
        }

        if (normalizedName.Length > MaximumPathSegmentLength)
        {
            blockers.Add(new SyncBlocker(path, "long-segment", "Path segment exceeds the common 255-character segment limit.", Severity.HighRisk));
        }

        if (normalizedName.Length > 0 && (char.IsWhiteSpace(normalizedName[0]) || char.IsWhiteSpace(normalizedName[^1])))
        {
            blockers.Add(new SyncBlocker(path, "leading-or-trailing-space", "Leading and trailing spaces in file or folder names are not allowed.", Severity.HighRisk));
        }

        if (normalizedName.IndexOfAny(InvalidNameCharacters) >= 0)
        {
            blockers.Add(new SyncBlocker(path, "invalid-character", "The name contains a character that OneDrive and SharePoint do not allow.", Severity.HighRisk));
        }

        var baseName = Path.GetFileNameWithoutExtension(normalizedName);
        if (ReservedWindowsNames.Contains(baseName))
        {
            blockers.Add(new SyncBlocker(path, "reserved-name", "Reserved Windows device names are not allowed in OneDrive.", Severity.HighRisk));
        }

        if (normalizedName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new SyncBlocker(path, "desktop-ini-metadata", "Windows Explorer metadata file. OneDrive treats this as a special restricted name, but it is common and usually not a root cause by itself.", Severity.Info));
        }
        else if (normalizedName.StartsWith("~$", StringComparison.Ordinal))
        {
            blockers.Add(new SyncBlocker(path, "office-temp-name", "Office temporary lock files can leave sync pending evidence while a document is open.", Severity.Warning));
        }
        else if (normalizedName.Equals(".lock", StringComparison.OrdinalIgnoreCase)
            || normalizedName.Contains("_vti_", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new SyncBlocker(path, "blocked-name", "The name is blocked or special-cased by OneDrive restrictions.", Severity.HighRisk));
        }

        if (parentDepth == 0 && normalizedName.Equals("forms", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new SyncBlocker(path, "root-forms-name", "A file or folder named forms at the root of a library is restricted.", Severity.Warning));
        }

        if (isDirectory)
        {
            if (normalizedName.Contains(';', StringComparison.Ordinal))
            {
                blockers.Add(new SyncBlocker(path, "office-folder-semicolon", "Office desktop apps can fail to save through Backstage when a OneDrive folder name contains a semicolon.", Severity.Warning));
            }

            if (normalizedName.StartsWith('\u309B') || normalizedName.StartsWith('\u1027'))
            {
                blockers.Add(new SyncBlocker(path, "invalid-leading-folder-character", "This leading folder character is not allowed by OneDrive restrictions.", Severity.HighRisk));
            }
        }

        if ((attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
        {
            blockers.Add(new SyncBlocker(path, "hidden", "Hidden files can explain sync-pending states when no visible file appears responsible.", Severity.Info));
        }

        return blockers;
    }

    public static IReadOnlyList<SyncBlocker> InspectFile(string path, FileInfo info)
    {
        var blockers = new List<SyncBlocker>();

        if (info.Extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) || info.Name.EndsWith(".temp", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new SyncBlocker(path, "temporary-file", "Temporary TMP files are not synced to OneDrive and can leave sync pending evidence.", Severity.Warning));
        }

        if (info.Name.Equals(".ds_store", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add(new SyncBlocker(path, "local-system-metadata", "Local system metadata files do not normally sync and can be deleted from the cloud copy after migration.", Severity.Info));
        }

        if (MailDataExtensions.Contains(info.Extension))
        {
            blockers.Add(new SyncBlocker(path, "mail-data-file", "Outlook PST and OST data files are synced less frequently than normal files and can create confusing lag or sync states.", Severity.Warning));
        }

        if (OneNoteExtensions.Contains(info.Extension))
        {
            blockers.Add(new SyncBlocker(path, "onenote-notebook-file", "OneNote notebooks have their own sync mechanism; moving existing local notebook files into OneDrive is not supported.", Severity.Warning));
        }

        if (info.Length > MaximumSyncFileSizeBytes)
        {
            blockers.Add(new SyncBlocker(path, "file-too-large-for-sync", "Individual files over 250 GB exceed OneDrive sync limits.", Severity.HighRisk));
        }

        if (LargeRiskExtensions.Contains(info.Extension) && info.Length >= 250L * 1024 * 1024)
        {
            blockers.Add(new SyncBlocker(path, "large-risk-file", "Large archive, media, virtual disk, or mail data files can prolong processing changes.", Severity.Warning));
        }

        if (PreviewExtensions.Contains(info.Extension) && info.Length > PreviewGenerationLimitBytes)
        {
            blockers.Add(new SyncBlocker(path, "preview-size-limit", "OneDrive thumbnails or previews are not generated for images or PDFs larger than 100 MB.", Severity.Info));
        }

        return blockers;
    }

    public static SyncBlocker CreateDuplicateNameBlocker(string path, string existingPath)
    {
        _ = existingPath;
        return new SyncBlocker(path, "duplicate-filename", "Another item in the same folder differs only by case or Unicode normalization.", Severity.Warning);
    }

    public static SyncBlocker CreateLargeFolderSharingBlocker(string path, int childEntryCount)
    {
        return new SyncBlocker(path, "large-folder-sharing-limit", $"Folder has {childEntryCount:N0} direct child items; sharing folders with more than 50,000 sub-items is restricted.", Severity.Info);
    }

    private static string SafeRelativePath(string root, string path)
    {
        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return path;
        }
    }
}
