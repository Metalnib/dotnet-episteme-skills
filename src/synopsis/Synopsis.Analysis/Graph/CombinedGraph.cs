using System.Collections.Concurrent;
using System.Collections.Immutable;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

/// <summary>
/// Multi-repo graph held in memory by the Synopsis daemon. One
/// <see cref="ScanResult"/> per repository root; a single unioned
/// <see cref="ScanResult"/> published as <see cref="Current"/> for readers.
/// Writes are serialised through an internal lock; reads are lock-free
/// via <see cref="Volatile"/>.
/// </summary>
/// <remarks>
/// <para>
/// Replace-then-rebuild is a full re-union after every
/// <see cref="ReplaceRepositoryAsync"/>. For a ~20-repo fleet with ~1k
/// nodes per repo, this is low-hundreds of milliseconds of in-memory work,
/// much less than the scan that produced the new <see cref="ScanResult"/>.
/// Incremental edge-level merges are a later optimisation.
/// </para>
/// <para>
/// <see cref="CrossRepoResolver"/> runs after every union so HTTP call
/// resolution always reflects the live combined view — stale
/// <c>ResolvesToService</c> edges from before a repo was replaced are
/// dropped on the next rebuild.
/// </para>
/// <para>
/// The optional <see cref="IGraphStateStore"/> backs cold-start recovery
/// (see ADR 0014). Save is fire-and-forget after the in-memory swap so a
/// slow or broken store never blocks readers.
/// </para>
/// </remarks>
public sealed class CombinedGraph
{
    private readonly IGraphStateStore _store;
    private readonly object _swapLock = new();
    // Serialises scanner invocations: WorkspaceScanner wraps MSBuildWorkspace
    // whose internals are not known to be reentrant. Concurrent clients that
    // both call reindex_repository would otherwise race through the same
    // workspace. Held across scan + replace so two callers don't also fight
    // over the per-repo slot.
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    // Per-repo state behind the swap lock. _records shadows _perRepo so
    // list_repositories can surface last-scanned / node / edge counts
    // without walking ScanResults. Keys are normalised absolute paths;
    // comparer mirrors the host filesystem (case-sensitive on Linux).
    private ConcurrentDictionary<string, ScanResult> _perRepo =
        new(Paths.FileSystemComparer);
    private ConcurrentDictionary<string, RepositoryRecord> _records =
        new(Paths.FileSystemComparer);

    private ScanResult _current;
    private int _loaded;  // 0 = not yet; 1 = LoadAsync has run

    public CombinedGraph(IGraphStateStore? store = null)
    {
        _store = store ?? new MemoryStateStore();
        _current = EmptyScanResult();
    }

    /// <summary>Atomic snapshot of the unioned graph. Lock-free read.</summary>
    public ScanResult Current => Volatile.Read(ref _current);

    /// <summary>
    /// Keys of every repository tracked by this graph. Ordered for
    /// deterministic enumeration by callers (e.g. reindex_all, tests).
    /// </summary>
    public IReadOnlyList<string> KnownRepositories =>
        _perRepo.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Summary records for every tracked repository (path + last-scanned
    /// timestamp + node/edge counts). Ordered by path.
    /// </summary>
    public IReadOnlyList<RepositoryRecord> Repositories =>
        _records.Values.OrderBy(r => r.RepoPath, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Hydrate from the backing state store. Callable exactly once, at
    /// daemon startup. Subsequent repo updates must go through
    /// <see cref="ReplaceRepositoryAsync"/> or <see cref="ReindexAsync"/>.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _loaded, 1) != 0)
            throw new InvalidOperationException("CombinedGraph.LoadAsync has already been called.");

        var snapshot = await _store.LoadAsync(ct);
        if (snapshot is null) return;

        // Build the replacement dictionaries off-lock so readers of
        // KnownRepositories / Repositories never observe a transient empty
        // state between Clear() and the refill loop.
        var newPerRepo = new ConcurrentDictionary<string, ScanResult>(Paths.FileSystemComparer);
        var newRecords = new ConcurrentDictionary<string, RepositoryRecord>(Paths.FileSystemComparer);
        foreach (var (path, result) in snapshot.PerRepo)
        {
            var key = NormalizePath(path);
            newPerRepo[key] = result;
            // Use the scan's own completion time so restart freshness
            // signals reflect the actual scan, not daemon uptime. Stamping
            // UtcNow here would tell every client "everything was just
            // scanned" on restart and trigger an unnecessary reindex_all.
            newRecords[key] = new RepositoryRecord(
                key, result.Metadata.CompletedAtUtc,
                result.Nodes.Length, result.Edges.Length);
        }

        lock (_swapLock)
        {
            _perRepo = newPerRepo;
            _records = newRecords;
            RebuildAndPublish();
        }
    }

    /// <summary>
    /// Scan <paramref name="repoPath"/> with <paramref name="scanner"/> and
    /// atomically replace that repo in the combined graph. Helper for the
    /// MCP <c>reindex_repository</c> tool so the tool layer does not have
    /// to orchestrate scan + replace itself. Serialises against other
    /// in-progress reindex calls.
    /// </summary>
    public async Task<RepositoryRecord> ReindexAsync(
        string repoPath, WorkspaceScanner scanner, ScanOptions? options = null,
        CancellationToken ct = default, IProgress<ProgressEvent>? progress = null)
    {
        await _scanLock.WaitAsync(ct);
        try
        {
            var result = await scanner.ScanRepositoryAsync(repoPath, options, ct, progress);
            return await ReplaceRepositoryAsync(repoPath, result, ct);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Install or replace the scan result for <paramref name="repoPath"/>.
    /// Rebuilds the merged view, runs the cross-repo resolver, publishes
    /// the new snapshot, then persists to the state store (non-fatal on
    /// save failure).
    /// </summary>
    public async Task<RepositoryRecord> ReplaceRepositoryAsync(
        string repoPath, ScanResult newResult, CancellationToken ct)
    {
        var key = NormalizePath(repoPath);
        var record = new RepositoryRecord(
            key, DateTimeOffset.UtcNow, newResult.Nodes.Length, newResult.Edges.Length);

        lock (_swapLock)
        {
            _perRepo[key] = newResult;
            _records[key] = record;
            RebuildAndPublish();
        }

        try
        {
            await _store.SaveRepositoryAsync(key, newResult, ct);
        }
        catch (Exception ex)
        {
            // Losing a persisted snapshot is a warm-cache loss, not a data
            // loss — the in-memory graph still serves reads correctly. Log
            // and continue.
            Console.Error.WriteLine($"[combined-graph] state save failed for {key}: {ex.Message}");
        }

        return record;
    }

    /// <summary>
    /// Rebuild the merged view from per-repo snapshots + run the resolver,
    /// then publish atomically. Must be called under <see cref="_swapLock"/>.
    /// </summary>
    private void RebuildAndPublish()
    {
        var builder = new GraphBuilder();
        var allWarnings = ImmutableArray.CreateBuilder<ScanWarning>();
        var startedAt = DateTimeOffset.MaxValue;

        // Ordered iteration: GraphBuilder.AddNode merges duplicate-id nodes
        // with first-wins metadata/display/location semantics, so iteration
        // order determines which repo's attributes win. Sort by key so the
        // merged graph is byte-stable across restarts and across iteration
        // order of the backing ConcurrentDictionary.
        foreach (var kv in _perRepo.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var result = kv.Value;
            foreach (var node in result.Nodes)
                builder.AddNode(node.Id, node.Type, node.DisplayName, node.Location,
                    node.RepositoryName, node.ProjectName, node.Certainty, node.Metadata);

            foreach (var edge in result.Edges)
                builder.AddEdge(edge.SourceId, edge.TargetId, edge.Type, edge.DisplayName,
                    edge.Location, edge.RepositoryName, edge.ProjectName, edge.Certainty, edge.Metadata);

            allWarnings.AddRange(result.Warnings);
            if (result.Metadata.StartedAtUtc < startedAt)
                startedAt = result.Metadata.StartedAtUtc;
        }

        CrossRepoResolver.Resolve(builder);

        var completedAt = DateTimeOffset.UtcNow;
        var info = new ScanInfo(
            RootPath: "(combined)",
            StartedAtUtc: startedAt == DateTimeOffset.MaxValue ? completedAt : startedAt,
            CompletedAtUtc: completedAt,
            Timings: [],
            Properties: new Dictionary<string, string>
            {
                ["scanner"] = nameof(CombinedGraph),
                ["repositories"] = _perRepo.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        var merged = builder.Build(info, allWarnings.ToImmutable());
        Volatile.Write(ref _current, merged);
    }

    private static string NormalizePath(string repoPath) =>
        Paths.Normalize(Path.GetFullPath(repoPath));

    private static ScanResult EmptyScanResult()
    {
        var builder = new GraphBuilder();
        var now = DateTimeOffset.UtcNow;
        var info = new ScanInfo("(combined)", now, now, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }
}
