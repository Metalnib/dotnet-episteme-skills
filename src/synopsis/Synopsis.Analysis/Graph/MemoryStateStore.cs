using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

/// <summary>
/// No-op <see cref="IGraphStateStore"/>. Used by ephemeral daemons
/// (<c>synopsis mcp --root …</c>) and by tests. A crashed process loses
/// everything; restart rescans from source.
/// </summary>
public sealed class MemoryStateStore : IGraphStateStore
{
    public Task<CombinedGraphSnapshot?> LoadAsync(CancellationToken ct) =>
        Task.FromResult<CombinedGraphSnapshot?>(
            new CombinedGraphSnapshot(new Dictionary<string, ScanResult>()));

    public Task SaveRepositoryAsync(string repoPath, ScanResult result, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<RepositoryRecord>> ListRepositoriesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RepositoryRecord>>([]);
}
