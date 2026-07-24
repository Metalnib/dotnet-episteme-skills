using System.Collections.Frozen;
using System.Collections.Immutable;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Scanning;

public static class WorkspaceDiscovery
{
    private static readonly FrozenSet<string> SkippedDirectories = FrozenSet.ToFrozenSet(
        [".git", ".hg", ".svn", "bin", "obj", "node_modules", ".idea", ".vs"],
        StringComparer.OrdinalIgnoreCase);

    public static DiscoveryResult Discover(string rootPath, CancellationToken ct = default, ScanOptions? options = null)
    {
        var normalizedRoot = Paths.Normalize(rootPath);
        options ??= ScanOptions.For(normalizedRoot);
        var repositories = new Dictionary<string, FoundRepo>(StringComparer.OrdinalIgnoreCase);
        var solutions = new List<FoundSolution>();
        var projects = new List<FoundProject>();
        var configs = new List<FoundConfig>();
        var warnings = new List<ScanWarning>();

        if (!Directory.Exists(normalizedRoot))
        {
            warnings.Add(new ScanWarning("root-not-found", $"Root folder '{normalizedRoot}' does not exist.", normalizedRoot));
            return new DiscoveryResult(normalizedRoot,
                ImmutableArray<FoundRepo>.Empty,
                ImmutableArray<FoundSolution>.Empty,
                ImmutableArray<FoundProject>.Empty,
                ImmutableArray<FoundConfig>.Empty,
                [.. warnings]);
        }

        var pending = new Stack<string>();
        pending.Push(normalizedRoot);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            if (Paths.IsExcluded(dir, normalizedRoot, options.ExcludedPaths))
                continue;

            try
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))
                    || File.Exists(Path.Combine(dir, ".git"))
                    || File.Exists(Path.Combine(dir, ".synopsis-repo")))
                {
                    repositories[dir] = new FoundRepo(Path.GetFileName(dir), dir);
                }

                foreach (var filePath in Directory.EnumerateFiles(dir))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Paths.IsExcluded(filePath, normalizedRoot, options.ExcludedPaths))
                        continue;

                    var fileName = Path.GetFileName(filePath);
                    var repo = FindOwner(filePath, repositories.Values);

                    // .slnx (default `dotnet new sln` format on .NET 9/10) opens like
                    // classic .sln; not matching it reported 0 projects (GitHub issue #9).
                    if (fileName.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                        || fileName.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                    {
                        solutions.Add(new FoundSolution(
                            Path.GetFileNameWithoutExtension(filePath),
                            Paths.Normalize(filePath),
                            repo?.Name, repo?.RootPath));
                    }
                    else if (fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    {
                        projects.Add(new FoundProject(
                            Path.GetFileNameWithoutExtension(filePath),
                            Paths.Normalize(filePath),
                            repo?.Name, repo?.RootPath));
                    }
                    else if (fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                             && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            foreach (var kvp in ConfigReader.Flatten(filePath))
                                configs.Add(new FoundConfig(kvp.Key, kvp.Value, Paths.Normalize(filePath), repo?.Name, repo?.RootPath));
                        }
                        catch (Exception ex)
                        {
                            warnings.Add(new ScanWarning("config-parse-failed", ex.Message, filePath, Certainty.Ambiguous));
                        }
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(child);
                    if (!SkippedDirectories.Contains(name)
                        && !Paths.IsExcluded(child, normalizedRoot, options.ExcludedPaths))
                    {
                        pending.Push(child);
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add(new ScanWarning("directory-scan-failed", ex.Message, dir, Certainty.Ambiguous));
            }
        }

        return new DiscoveryResult(
            normalizedRoot,
            [.. repositories.Values.OrderBy(r => r.RootPath, StringComparer.OrdinalIgnoreCase)],
            [.. PreferSlnx(solutions).OrderBy(s => s.FullPath, StringComparer.OrdinalIgnoreCase)],
            [.. projects.DistinctBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.FullPath, StringComparer.OrdinalIgnoreCase)],
            [.. configs],
            [.. warnings]);
    }

    /// <summary>
    /// When a directory holds both <c>X.sln</c> and <c>X.slnx</c> (mid-migration),
    /// keep only the <c>.slnx</c>: loading both fails the second open with
    /// "already part of the workspace".
    /// </summary>
    private static IEnumerable<FoundSolution> PreferSlnx(IEnumerable<FoundSolution> solutions)
    {
        var byPath = solutions.DistinctBy(s => s.FullPath, Paths.FileSystemComparer).ToList();
        var slnxSiblings = byPath
            .Where(s => s.FullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.FullPath[..^1])
            .ToHashSet(Paths.FileSystemComparer);
        return byPath.Where(s =>
            !(s.FullPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
              && slnxSiblings.Contains(s.FullPath)));
    }

    private static FoundRepo? FindOwner(string filePath, IEnumerable<FoundRepo> repositories) =>
        repositories
            .Where(r => Paths.IsUnder(filePath, r.RootPath))
            .OrderByDescending(r => r.RootPath.Length)
            .FirstOrDefault();
}
