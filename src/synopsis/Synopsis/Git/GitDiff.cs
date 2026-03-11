using System.Collections.Immutable;
using System.Diagnostics;
using Synopsis.Analysis.Model;

namespace Synopsis.Git;

public static class GitDiff
{
    public static async Task<ImmutableArray<string>> GetChangedFilesAsync(
        string repoPath, string baseBranch, string headRef = "HEAD", CancellationToken ct = default)
    {
        var args = $"diff --name-only {baseBranch}...{headRef}";
        var (exitCode, output) = await RunGitAsync(repoPath, args, ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"git {args} failed (exit {exitCode}): {output}");

        return [.. output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(relative => Path.GetFullPath(Path.Combine(repoPath, relative)))
            .Where(path => IsRelevantFile(path))];
    }

    public static ImmutableArray<GraphNode> FindAffectedNodes(ScanResult graph, ImmutableArray<string> changedFiles)
    {
        var fileSet = changedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. graph.Nodes.Where(n =>
            n.Location is not null && fileSet.Contains(n.Location.FilePath))];
    }

    private static bool IsRelevantFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(
        string workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, process.ExitCode == 0 ? output : error);
    }
}

public sealed record GitImpactResult(
    string BaseBranch,
    string HeadRef,
    ImmutableArray<string> ChangedFiles,
    ImmutableArray<GraphNode> DirectlyAffectedNodes,
    ImpactGraph BlastRadius,
    ScanInfo ScanMetadata);
