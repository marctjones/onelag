namespace OneLag.Core;

public sealed class Redactor
{
    private readonly bool fullPaths;
    private readonly string userProfile;
    private readonly Dictionary<string, string> rootAliases = new(StringComparer.OrdinalIgnoreCase);

    public Redactor(bool fullPaths, IEnumerable<string> roots)
    {
        this.fullPaths = fullPaths;
        userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var index = 1;
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            rootAliases[root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)] = $"<root:{index++}>";
        }
    }

    public string PathValue(string path)
    {
        if (fullPaths || string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var redacted = path;
        foreach (var (root, alias) in rootAliases.OrderByDescending(pair => pair.Key.Length))
        {
            if (redacted.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                redacted = alias + redacted[root.Length..];
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            redacted = redacted.Replace(userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }
}
