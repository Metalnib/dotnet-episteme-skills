using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Scanning;

public static class IgnoreFile
{
    public static IReadOnlyList<string> Load(string rootPath, IReadOnlyList<string>? excludeFilePaths = null)
    {
        var normalizedRoot = Paths.Normalize(rootPath);
        var files = new List<string>();
        var defaultFile = Path.Combine(normalizedRoot, ".synopsisignore");

        if (File.Exists(defaultFile))
            files.Add(defaultFile);

        if (excludeFilePaths is not null)
        {
            foreach (var path in excludeFilePaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var resolved = Path.GetFullPath(path);
                if (!File.Exists(resolved))
                    throw new FileNotFoundException($"Exclude file '{path}' was not found.", resolved);

                files.Add(resolved);
            }
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(file))
            {
                var pattern = line.Trim();
                if (!string.IsNullOrWhiteSpace(pattern) && !pattern.StartsWith('#'))
                    excluded.Add(pattern);
            }
        }

        return excluded.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
