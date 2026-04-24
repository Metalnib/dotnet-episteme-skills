using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class CombinedGraphTests
{
    [Fact]
    public async Task Current_EmptyGraph_HasNoNodes()
    {
        var graph = new CombinedGraph();
        Assert.Empty(graph.Current.Nodes);
        Assert.Empty(graph.Current.Edges);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ReplaceRepositoryAsync_FirstRepo_MergesIntoCurrent()
    {
        var graph = new CombinedGraph();

        var repoA = BuildRepo("/repo-a", b =>
        {
            b.AddNode("node:a1", NodeType.Method, "A.Service.Do");
        });

        await graph.ReplaceRepositoryAsync("/repo-a", repoA, CancellationToken.None);

        Assert.Contains(graph.Current.Nodes, n => n.Id == "node:a1");
    }

    [Fact]
    public async Task ReplaceRepositoryAsync_TwoRepos_BothVisibleInCurrent()
    {
        var graph = new CombinedGraph();

        await graph.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("node:a", NodeType.Method, "A.Do")),
            CancellationToken.None);
        await graph.ReplaceRepositoryAsync("/repo-b",
            BuildRepo("/repo-b", b => b.AddNode("node:b", NodeType.Method, "B.Do")),
            CancellationToken.None);

        Assert.Contains(graph.Current.Nodes, n => n.Id == "node:a");
        Assert.Contains(graph.Current.Nodes, n => n.Id == "node:b");
    }

    [Fact]
    public async Task ReplaceRepositoryAsync_SameRepoTwice_SecondWins()
    {
        var graph = new CombinedGraph();

        await graph.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("node:old", NodeType.Method, "Before")),
            CancellationToken.None);
        await graph.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("node:new", NodeType.Method, "After")),
            CancellationToken.None);

        // First repo's nodes are gone; second repo's nodes replace them.
        Assert.DoesNotContain(graph.Current.Nodes, n => n.Id == "node:old");
        Assert.Contains(graph.Current.Nodes, n => n.Id == "node:new");
    }

    [Fact]
    public async Task ReplaceRepositoryAsync_CrossRepoHttpCall_EmitsResolvesToServiceEdge()
    {
        // This is the M2 guarantee: a per-repo scan that emits an
        // ExternalEndpoint gets linked to an internal Endpoint in a
        // different repo only after CrossRepoResolver runs on the merged
        // graph. Verify it lands.
        var graph = new CombinedGraph();

        // Repo B owns the real endpoint.
        var repoB = BuildRepo("/repo-b", b =>
        {
            b.AddNode("endpoint:b-get-orders", NodeType.Endpoint, "GET /orders",
                repositoryName: "repo-b", projectName: "Orders.Api",
                metadata: new Dictionary<string, string?>
                {
                    ["verb"] = "GET",
                    ["route"] = "/orders",
                    ["handler"] = "method:Orders.Api.GetAll",
                });
        });

        // Repo A has a client calling that endpoint via HTTP.
        var repoA = BuildRepo("/repo-a", b =>
        {
            b.AddNode("external:a-orders-get", NodeType.ExternalEndpoint, "GET /orders",
                repositoryName: "repo-a", projectName: "Basket.Api",
                metadata: new Dictionary<string, string?>
                {
                    ["verb"] = "GET",
                    ["path"] = "/orders",
                    ["clientName"] = "OrdersClient",
                    ["clientBaseUrl"] = "http://orders-api:5000",
                });
        });

        await graph.ReplaceRepositoryAsync("/repo-a", repoA, CancellationToken.None);
        await graph.ReplaceRepositoryAsync("/repo-b", repoB, CancellationToken.None);

        var current = graph.Current;
        var resolveEdges = current.Edges
            .Where(e => e.SourceId == "external:a-orders-get"
                     && e.TargetId == "endpoint:b-get-orders")
            .ToArray();

        Assert.Contains(resolveEdges, e => e.Type == EdgeType.ResolvesToService);
        Assert.Contains(resolveEdges, e => e.Type == EdgeType.CrossesRepoBoundary);
    }

    [Fact]
    public async Task ReplaceRepositoryAsync_Replay_DropsStaleResolvedEdges()
    {
        // A replaced repo must not leave behind ResolvesToService edges
        // from its previous snapshot. The resolver is re-run on every
        // rebuild against the live merged graph, so stale per-endpoint
        // links vanish with the old scan.
        var graph = new CombinedGraph();

        // v1: repo A has external /orders, repo B has endpoint /orders.
        await graph.ReplaceRepositoryAsync("/repo-b",
            BuildRepo("/repo-b", b => b.AddNode("endpoint:b-orders", NodeType.Endpoint, "GET /orders",
                repositoryName: "repo-b", projectName: "Orders.Api",
                metadata: new Dictionary<string, string?>
                {
                    ["verb"] = "GET", ["route"] = "/orders", ["handler"] = "method:h",
                })),
            CancellationToken.None);
        await graph.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => b.AddNode("external:a-orders", NodeType.ExternalEndpoint, "GET /orders",
                repositoryName: "repo-a", projectName: "Basket.Api",
                metadata: new Dictionary<string, string?>
                {
                    ["verb"] = "GET", ["path"] = "/orders",
                    ["clientName"] = "OrdersClient", ["clientBaseUrl"] = "http://orders-api",
                })),
            CancellationToken.None);

        Assert.Contains(graph.Current.Edges, e =>
            e.SourceId == "external:a-orders" && e.TargetId == "endpoint:b-orders");

        // v2: replace repo A with a scan that no longer calls /orders.
        await graph.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b => { /* nothing */ }),
            CancellationToken.None);

        Assert.DoesNotContain(graph.Current.Edges, e =>
            e.SourceId == "external:a-orders" && e.TargetId == "endpoint:b-orders");
    }


    [Fact]
    public async Task ReplaceRepositoryAsync_SameIdAcrossRepos_MergeIsDeterministic()
    {
        // _perRepo is a ConcurrentDictionary whose
        // enumeration order is unspecified. RebuildAndPublish must order
        // by key so the merged node (first-wins attributes on collision)
        // is identical regardless of replace order. Two iterations with
        // swapped insert orders must produce byte-identical merged nodes.
        async Task<GraphNode> MergedFor(string firstKey, string secondKey)
        {
            var graph = new CombinedGraph();
            await graph.ReplaceRepositoryAsync(firstKey,
                BuildRepo(firstKey, b => b.AddNode("shared:pkg", NodeType.Package, "Pkg@1.0",
                    metadata: new Dictionary<string, string?>
                    {
                        ["packageId"] = "Pkg",
                        ["version"] = "1.0",
                        ["origin"] = firstKey,
                    })),
                CancellationToken.None);
            await graph.ReplaceRepositoryAsync(secondKey,
                BuildRepo(secondKey, b => b.AddNode("shared:pkg", NodeType.Package, "Pkg@1.0",
                    metadata: new Dictionary<string, string?>
                    {
                        ["packageId"] = "Pkg",
                        ["version"] = "1.0",
                        ["origin"] = secondKey,
                    })),
                CancellationToken.None);
            return Assert.Single(graph.Current.Nodes, n => n.Id == "shared:pkg");
        }

        var nodeA = await MergedFor("/repo-a", "/repo-b");
        var nodeB = await MergedFor("/repo-b", "/repo-a");
        // The winning "origin" metadata is the one that sorts first by key
        // (case-insensitive ordinal) — same in both orderings.
        Assert.Equal(nodeA.Metadata["origin"], nodeB.Metadata["origin"]);
    }

    [Fact]
    public async Task ReplaceRepositoryAsync_ConcurrentDifferentRepos_AllEntriesMerged()
    {
        // parallel ReplaceRepositoryAsync calls on
        // distinct repos must all land in the final merged graph. Writers
        // serialise through _swapLock internally; the test proves nothing
        // is dropped under concurrency.
        var graph = new CombinedGraph();
        var tasks = Enumerable.Range(0, 16).Select(i =>
            graph.ReplaceRepositoryAsync($"/repo-{i}",
                BuildRepo($"/repo-{i}", b => b.AddNode($"node:{i}", NodeType.Method, $"M{i}")),
                CancellationToken.None)).ToArray();

        await Task.WhenAll(tasks);

        for (var i = 0; i < 16; i++)
            Assert.Contains(graph.Current.Nodes, n => n.Id == $"node:{i}");
        Assert.Equal(16, graph.Repositories.Count);
    }

    [Fact]
    public async Task Repositories_ExposesPerRepoMetadata()
    {
        // list_repositories used to return only path.
        // CombinedGraph now tracks RepositoryRecord per repo; the tool
        // surfaces last-scanned + node/edge counts from it.
        var graph = new CombinedGraph();
        await graph.ReplaceRepositoryAsync("/repo-a",
            BuildRepo("/repo-a", b =>
            {
                b.AddNode("a1", NodeType.Method, "A1");
                b.AddNode("a2", NodeType.Method, "A2");
                b.AddEdge("a1", "a2", EdgeType.Calls, "A1 calls A2");
            }),
            CancellationToken.None);

        var record = Assert.Single(graph.Repositories);
        Assert.Equal(2, record.NodeCount);
        Assert.Equal(1, record.EdgeCount);
        Assert.True(record.LastScannedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task LoadAsync_CalledTwice_Throws()
    {
        // LoadAsync swaps dictionaries under lock;
        // calling it twice risks reader surprise. Daemon startup has
        // exactly one caller — enforce that contract.
        var graph = new CombinedGraph();
        await graph.LoadAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            graph.LoadAsync(CancellationToken.None));
    }

    private static ScanResult BuildRepo(string rootPath, Action<GraphBuilder> setup)
    {
        var builder = new GraphBuilder();
        setup(builder);
        var now = DateTimeOffset.UtcNow;
        var info = new ScanInfo(rootPath, now, now, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }
}
