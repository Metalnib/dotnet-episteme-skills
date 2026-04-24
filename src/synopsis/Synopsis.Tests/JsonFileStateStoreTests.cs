using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class JsonFileStateStoreTests
{
    [Fact]
    public async Task LoadAsync_EmptyDirectory_ReturnsEmptySnapshot()
    {
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);
        var snapshot = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.PerRepo);
    }

    [Fact]
    public async Task SaveLoad_RoundTripsScanResult()
    {
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        var before = BuildRepo("/repo-a", b =>
        {
            b.AddNode("node:1", NodeType.Method, "A.Do");
            b.AddNode("node:2", NodeType.Endpoint, "GET /x",
                metadata: new Dictionary<string, string?> { ["route"] = "/x", ["verb"] = "GET", ["handler"] = "h" });
            b.AddEdge("node:1", "node:2", EdgeType.Calls, "A.Do calls endpoint");
        });

        await store.SaveRepositoryAsync("/repo-a", before, CancellationToken.None);

        // Fresh store instance → prove we re-read from disk, not from memory.
        var store2 = new JsonFileStateStore(temp.Path);
        var snapshot = await store2.LoadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.PerRepo.ContainsKey("/repo-a"));
        var after = snapshot.PerRepo["/repo-a"];
        Assert.Equal(before.Nodes.Length, after.Nodes.Length);
        Assert.Equal(before.Edges.Length, after.Edges.Length);
    }

    [Fact]
    public async Task ListRepositoriesAsync_ReportsScannedMetadata()
    {
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        await store.SaveRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b =>
            {
                b.AddNode("n1", NodeType.Method, "A");
                b.AddNode("n2", NodeType.Method, "B");
                b.AddEdge("n1", "n2", EdgeType.Calls, "A calls B");
            }),
            CancellationToken.None);

        var records = await store.ListRepositoriesAsync(CancellationToken.None);
        var entry = Assert.Single(records);
        Assert.Equal("/repo-a", entry.RepoPath);
        Assert.Equal(2, entry.NodeCount);
        Assert.Equal(1, entry.EdgeCount);
    }

    [Fact]
    public async Task SaveRepositoryAsync_TwoRepos_BothAppearInIndex()
    {
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        await store.SaveRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("a", NodeType.Method, "A")),
            CancellationToken.None);
        await store.SaveRepositoryAsync("/repo-b",
            BuildRepo("/repo-b", b => b.AddNode("b", NodeType.Method, "B")),
            CancellationToken.None);

        var records = await store.ListRepositoriesAsync(CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.RepoPath == "/repo-a");
        Assert.Contains(records, r => r.RepoPath == "/repo-b");
    }

    [Fact]
    public async Task SaveRepositoryAsync_ReplacingExistingRepo_UpdatesCounts()
    {
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        await store.SaveRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("a1", NodeType.Method, "A")),
            CancellationToken.None);

        await store.SaveRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b =>
            {
                b.AddNode("a1", NodeType.Method, "A");
                b.AddNode("a2", NodeType.Method, "A2");
            }),
            CancellationToken.None);

        var records = await store.ListRepositoriesAsync(CancellationToken.None);
        var entry = Assert.Single(records);
        Assert.Equal(2, entry.NodeCount);   // reflects the newer scan
    }

    [Fact]
    public async Task Load_RestoresIntoCombinedGraph()
    {
        // End-to-end: persist via store, reload into a fresh CombinedGraph,
        // merged view matches.
        using var temp = new TempDir();
        var store1 = new JsonFileStateStore(temp.Path);

        var graph1 = new CombinedGraph(store1);
        await graph1.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("a", NodeType.Method, "A")),
            CancellationToken.None);
        await graph1.ReplaceRepositoryAsync("/repo-b",
            BuildRepo("/repo-b", b => b.AddNode("b", NodeType.Method, "B")),
            CancellationToken.None);

        var store2 = new JsonFileStateStore(temp.Path);
        var graph2 = new CombinedGraph(store2);
        await graph2.LoadAsync(CancellationToken.None);

        Assert.Contains(graph2.Current.Nodes, n => n.Id == "a");
        Assert.Contains(graph2.Current.Nodes, n => n.Id == "b");
    }

    [Fact]
    public async Task Load_PreservesOriginalScanTimestamp()
    {
        // LoadAsync used to stamp LastScannedAtUtc with
        // DateTimeOffset.UtcNow at restart. After restart every repo looked
        // freshly-scanned regardless of when it was actually scanned, which
        // defeats the point of the persistent state-store cache and
        // triggers unnecessary reindex_all calls from freshness-aware
        // clients. The fix reads Metadata.CompletedAtUtc from the loaded
        // ScanResult. This test pins that behaviour: persist with a
        // deliberately old timestamp, reload, assert the restored record
        // still carries the old timestamp rather than `now`.
        using var temp = new TempDir();
        var oldScanTime = DateTimeOffset.UtcNow.AddDays(-1);

        // Save with backdated ScanInfo.
        var store = new JsonFileStateStore(temp.Path);
        var builder = new GraphBuilder();
        builder.AddNode("n", NodeType.Method, "N");
        var backdated = builder.Build(
            new ScanInfo("/repo-a", oldScanTime, oldScanTime, [], new Dictionary<string, string>()),
            []);
        await store.SaveRepositoryAsync("/repo-a", backdated, CancellationToken.None);

        // Reload into a fresh graph and check the record's timestamp.
        var graph = new CombinedGraph(new JsonFileStateStore(temp.Path));
        await graph.LoadAsync(CancellationToken.None);

        var record = Assert.Single(graph.Repositories);
        // Allow ±1 second for JSON round-trip precision; definitely not "now".
        Assert.InRange(record.LastScannedAtUtc,
            oldScanTime.AddSeconds(-1), oldScanTime.AddSeconds(1));
    }


    [Fact]
    public async Task LoadAsync_CorruptRepoFile_SkippedWithWarning()
    {
        // a half-written repo file must not crash
        // LoadAsync. The index points at slug X; slug X on disk is junk;
        // LoadAsync should skip it and keep going. Other known-good repos
        // still load.
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        await store.SaveRepositoryAsync("/repo-good",
            BuildRepo("/repo-good", b => b.AddNode("g", NodeType.Method, "Good")),
            CancellationToken.None);
        await store.SaveRepositoryAsync("/repo-bad",
            BuildRepo("/repo-bad", b => b.AddNode("b", NodeType.Method, "Bad")),
            CancellationToken.None);

        // Corrupt the bad file in-place.
        var badFile = Directory.EnumerateFiles(Path.Combine(temp.Path, "repos"), "*.json")
            .First(f => File.ReadAllText(f).Contains("Bad", StringComparison.Ordinal));
        await File.WriteAllTextAsync(badFile, "{ not valid json ");

        var store2 = new JsonFileStateStore(temp.Path);
        var snapshot = await store2.LoadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot.PerRepo.ContainsKey("/repo-good"));
        Assert.False(snapshot.PerRepo.ContainsKey("/repo-bad"));
    }

    [Fact]
    public async Task LoadAsync_OrphanRepoFile_IgnoredSilently()
    {
        // a repo file with no index entry should
        // be ignored (not crash, not surface as a loaded repo).
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        await store.SaveRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("a", NodeType.Method, "A")),
            CancellationToken.None);

        // Drop an orphan file into repos/ that the index doesn't know about.
        var orphan = Path.Combine(temp.Path, "repos", "deadbeefdeadbeef.json");
        await File.WriteAllTextAsync(orphan, """{ "nodes": [], "edges": [] }""");

        var store2 = new JsonFileStateStore(temp.Path);
        var snapshot = await store2.LoadAsync(CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot.PerRepo);
        Assert.True(snapshot.PerRepo.ContainsKey("/repo-a"));
    }

    [Fact]
    public async Task SaveRepositoryAsync_ConcurrentSameSlug_BothSucceed()
    {
        // two simultaneous writes for the same repo
        // used to collide on the fixed "<slug>.tmp" name. Random tmp
        // suffixes let both callers run; last rename wins for the final
        // file, both SaveAsync calls return success.
        using var temp = new TempDir();
        var store = new JsonFileStateStore(temp.Path);

        var repo = BuildRepo("/repo-hot", b => b.AddNode("x", NodeType.Method, "X"));
        var tasks = Enumerable.Range(0, 8).Select(_ =>
            store.SaveRepositoryAsync("/repo-hot", repo, CancellationToken.None)).ToArray();
        await Task.WhenAll(tasks);

        var records = await store.ListRepositoriesAsync(CancellationToken.None);
        var entry = Assert.Single(records);
        Assert.Equal("/repo-hot", entry.RepoPath);
    }

    private static ScanResult BuildRepo(string rootPath, Action<GraphBuilder> setup)
    {
        var builder = new GraphBuilder();
        setup(builder);
        var now = DateTimeOffset.UtcNow;
        var info = new ScanInfo(rootPath, now, now, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"syn-state-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
