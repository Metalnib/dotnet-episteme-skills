using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

/// <summary>
/// Narrow persistence surface for the Synopsis daemon's combined multi-repo
/// graph. The <see cref="CombinedGraph"/> holds the live in-memory graph;
/// this interface only backs it with whatever cold-start + crash-recovery
/// story a deployment wants. P0 ships <see cref="JsonFileStateStore"/>
/// (one file per repo, human-inspectable) and <see cref="MemoryStateStore"/>
/// (no persistence, for ephemeral daemons and tests). See ADR 0014.
/// </summary>
public interface IGraphStateStore
{
    /// <summary>
    /// Load everything the store knows about. Returns an empty snapshot
    /// (not null) on a fresh store; returns null only if the backing
    /// location is unreadable and the caller should log and continue with
    /// an empty in-memory graph.
    /// </summary>
    Task<CombinedGraphSnapshot?> LoadAsync(CancellationToken ct);

    /// <summary>
    /// Persist one repository's scan result. Called after every successful
    /// <see cref="CombinedGraph.ReplaceRepositoryAsync"/>. Implementations
    /// should write atomically (temp file + rename on disk-backed stores)
    /// so a process kill mid-write does not leave a half-written state.
    /// </summary>
    Task SaveRepositoryAsync(string repoPath, ScanResult result, CancellationToken ct);

    /// <summary>List repositories known to the store with quick summary stats.</summary>
    Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesAsync(CancellationToken ct);
}

/// <summary>
/// Snapshot loaded from an <see cref="IGraphStateStore"/> at daemon startup.
/// </summary>
public sealed record CombinedGraphSnapshot(
    IReadOnlyDictionary<string, ScanResult> PerRepo);

/// <summary>Summary stats for a repository tracked by the store.</summary>
public sealed record RepositoryRecord(
    string RepoPath,
    DateTimeOffset LastScannedAtUtc,
    int NodeCount,
    int EdgeCount);
