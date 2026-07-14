namespace OneLag.Core;

/// <summary>
/// Names output files so a capture cannot destroy the one before it.
///
/// Diagnosing lag is a comparison exercise: a docked day against an undocked day, a capture before a fix
/// against one after it, commit growth on Monday against commit growth on Friday. Every one of those needs
/// two captures to still exist. The default output names used to be fixed — `onelag-report.md`,
/// `onelag-watch-report.md` — so a second run silently overwrote the evidence the first run existed to
/// produce, and the user found out only when they went looking for it.
///
/// Two rules follow. Defaults are timestamped, so repeated runs accumulate rather than collide. And an
/// explicit path that already exists is refused rather than overwritten, because a user who names a file is
/// usually naming a file they want to keep — `--overwrite` is there for when they genuinely do not.
/// </summary>
public static class OutputPaths
{
    /// <summary>
    /// Sortable, filename-safe, second-resolution, and local rather than UTC — these names are read by a
    /// person who is trying to remember which capture was "the bad one this morning", and they should not
    /// have to do timezone arithmetic to find it.
    /// </summary>
    public const string TimestampFormat = "yyyyMMdd-HHmmss";

    public static string Timestamped(string prefix, string extension, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(extension);

        var stamp = now.ToLocalTime().ToString(TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);

        if (extension.Length == 0)
        {
            return $"{prefix}-{stamp}";
        }

        var suffix = extension.StartsWith('.') ? extension : $".{extension}";
        return $"{prefix}-{stamp}{suffix}";
    }

    public static string Timestamped(string prefix, string extension) =>
        Timestamped(prefix, extension, DateTimeOffset.Now);

    /// <summary>
    /// Refuses to clobber an existing file or directory unless the caller explicitly asked for it.
    ///
    /// Deliberately throws rather than silently renaming: a tool that quietly writes somewhere other than
    /// where it was told is worse than one that stops, because the user goes looking in the path they named.
    /// </summary>
    public static void EnsureWritable(string path, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (overwrite)
        {
            return;
        }

        var full = Path.GetFullPath(path);

        if (File.Exists(full))
        {
            throw new InvalidOperationException(
                $"'{path}' already exists, and overwriting it would destroy a previous capture. Pass --overwrite to replace it, or choose another --output. Leaving the default output unset gives every run its own timestamped file.");
        }

        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new InvalidOperationException(
                $"'{path}' already exists and is not empty, and overwriting it would destroy a previous capture. Pass --overwrite to replace it, or choose another --output.");
        }
    }
}
