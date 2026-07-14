using OneLag.Core;

namespace OneLag.Windows;

/// <summary>
/// Turns a resolved known-folder path into a <see cref="KnownFolderRedirect"/>.
///
/// Split out of <see cref="WindowsFileSystemContextProbe"/> so the actual decision — is this folder's path
/// underneath any discovered OneDrive root, and should it be recorded at all — is testable off Windows. The
/// probe itself only resolves paths (<c>Environment.GetFolderPath</c>, a registry read for Downloads); every
/// call is against real Win32 state and cannot be exercised on macOS, so the wiring that turns those resolved
/// paths into the contract type belongs here instead, where it can be proven correct directly.
/// </summary>
internal static class KnownFolderRedirectClassifier
{
    /// <summary>
    /// Returns <see langword="null"/> for a folder OneLag could not resolve at all, rather than fabricating a
    /// record with an empty path: a folder that failed to resolve is a gap in the evidence, not a "not
    /// redirected" finding, and the two must not be conflated.
    /// </summary>
    public static KnownFolderRedirect? Classify(string knownFolder, string? path, IReadOnlyList<string> cloudRoots)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var redirected = cloudRoots.Any(root => PathContainment.IsUnderRoot(path, root));
        return new KnownFolderRedirect(knownFolder, path, redirected);
    }
}
