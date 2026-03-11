namespace Synopsis.Analysis.Model;

public static class Paths
{
    public static string Normalize(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool IsUnder(string candidatePath, string rootPath)
    {
        var candidate = Normalize(candidatePath);
        var root = Normalize(rootPath);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcluded(string candidatePath, string rootPath, IReadOnlyList<string>? excludedPaths)
    {
        if (excludedPaths is null || excludedPaths.Count == 0)
            return false;

        var candidate = Normalize(candidatePath);
        var normalizedRoot = Normalize(rootPath);
        var candidateForward = ToForwardSlash(candidate);
        var relativeToRoot = ToForwardSlash(Path.GetRelativePath(normalizedRoot, candidate));

        foreach (var rawPattern in excludedPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPattern))
                continue;

            var pattern = rawPattern.Trim();
            if (Path.IsPathRooted(pattern))
            {
                if (IsUnder(candidate, pattern))
                    return true;
                continue;
            }

            var normalizedPattern = ToForwardSlash(pattern).Trim('/');
            if (string.IsNullOrWhiteSpace(normalizedPattern))
                continue;

            if (relativeToRoot.Equals(normalizedPattern, StringComparison.OrdinalIgnoreCase)
                || relativeToRoot.StartsWith(normalizedPattern + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            if (candidateForward.Contains("/" + normalizedPattern + "/", StringComparison.OrdinalIgnoreCase)
                || candidateForward.EndsWith("/" + normalizedPattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string ToRelative(string rootPath, string fullPath) =>
        Path.GetRelativePath(Normalize(rootPath), Normalize(fullPath))
            .Replace('\\', '/');

    private static string ToForwardSlash(string path) => path.Replace('\\', '/');
}
