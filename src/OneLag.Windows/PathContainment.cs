namespace OneLag.Windows;

/// <summary>
/// Answers "is this path inside that root?" the way Windows paths actually need it answered.
///
/// A naive <c>StartsWith</c> is wrong here in a way that would silently corrupt the diagnosis: the root
/// <c>C:\Users\x\OneDrive</c> is a string-prefix of <c>C:\Users\x\OneDriveFoo\Documents</c>, a sibling folder
/// that has nothing to do with OneDrive at all. This normalizes separators, strips a trailing separator from
/// the root, and then requires either an exact match or a match followed by a separator, so a folder name that
/// merely starts with the same characters as the root is never mistaken for a child of it.
///
/// Kept pure — no filesystem access, no registry, no environment lookups — so it is testable off Windows and
/// reusable by anything that needs the same containment check (<see cref="WindowsFileSystemContextProbe"/> for
/// Known Folder Move detection, <see cref="ShellExtensionClassifier"/> for the %SystemRoot% check).
/// </summary>
internal static class PathContainment
{
    public static bool IsUnderRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Normalize(path);
        var normalizedRoot = Normalize(root);

        if (normalizedRoot.Length == 0)
        {
            return false;
        }

        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot + '\\';
        return normalizedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Accepts both separator styles (registry and environment values are inconsistent about it) and drops a
    /// trailing separator so a root written as <c>C:\OneDrive\</c> compares equal to one written as
    /// <c>C:\OneDrive</c>.
    /// </summary>
    private static string Normalize(string path)
    {
        var withBackslashes = path.Replace('/', '\\');
        return withBackslashes.Length > 3 ? withBackslashes.TrimEnd('\\') : withBackslashes;
    }
}
