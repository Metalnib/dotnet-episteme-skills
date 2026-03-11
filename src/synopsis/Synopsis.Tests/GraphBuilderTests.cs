using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class GraphBuilderTests
{
    [Fact]
    public void AddNode_FirstAdd_CreatesNode()
    {
        var builder = new GraphBuilder();
        var node = builder.AddNode("test:1", NodeType.Method, "Foo.Bar");

        Assert.Equal("test:1", node.Id);
        Assert.Equal(NodeType.Method, node.Type);
        Assert.Equal("Foo.Bar", node.DisplayName);
        Assert.Equal(Certainty.Exact, node.Certainty);
    }

    [Fact]
    public void AddNode_DuplicateId_MergesWithHigherCertainty()
    {
        var builder = new GraphBuilder();
        builder.AddNode("test:1", NodeType.Implementation, "Foo", certainty: Certainty.Ambiguous);
        var merged = builder.AddNode("test:1", NodeType.Controller, "Foo", certainty: Certainty.Exact);

        Assert.Equal(NodeType.Controller, merged.Type); // Controller has higher priority
        Assert.Equal(Certainty.Exact, merged.Certainty);
        Assert.Single(builder.Nodes);
    }

    [Fact]
    public void AddNode_MergesFillsNullFields()
    {
        var builder = new GraphBuilder();
        builder.AddNode("test:1", NodeType.Method, "Foo");
        var merged = builder.AddNode("test:1", NodeType.Method, "",
            repositoryName: "repo-a", projectName: "proj-a",
            location: new SourceLocation("/file.cs", 10));

        Assert.Equal("Foo", merged.DisplayName); // keeps non-empty
        Assert.Equal("repo-a", merged.RepositoryName);
        Assert.Equal("proj-a", merged.ProjectName);
        Assert.NotNull(merged.Location);
    }

    [Fact]
    public void AddEdge_CreatesEdgeWithStableId()
    {
        var builder = new GraphBuilder();
        var edge1 = builder.AddEdge("src", "tgt", EdgeType.Calls, "A calls B");
        var edge2 = builder.AddEdge("src", "tgt", EdgeType.Calls, "A calls B");

        Assert.Equal(edge1.Id, edge2.Id);
        Assert.Single(builder.Edges);
    }

    [Fact]
    public void Build_CountsStatisticsInSinglePass()
    {
        var builder = new GraphBuilder();
        builder.AddNode("r1", NodeType.Repository, "repo-1");
        builder.AddNode("r2", NodeType.Repository, "repo-2");
        builder.AddNode("p1", NodeType.Project, "proj-1");
        builder.AddNode("e1", NodeType.Endpoint, "GET /api/foo",
            metadata: new Dictionary<string, string?> { ["route"] = "/api/foo" });
        builder.AddNode("m1", NodeType.Method, "Foo.Bar");
        builder.AddNode("m2", NodeType.Method, "Foo.Baz");
        builder.AddNode("t1", NodeType.Table, "Orders");
        builder.AddEdge("e1", "m1", EdgeType.CallsHttp, "http call");
        builder.AddEdge("r1", "r2", EdgeType.CrossesRepoBoundary, "cross");
        builder.AddEdge("m1", "m2", EdgeType.Calls, "call", certainty: Certainty.Ambiguous);

        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        var result = builder.Build(info, []);

        Assert.Equal(2, result.Statistics.RepositoryCount);
        Assert.Equal(1, result.Statistics.ProjectCount);
        Assert.Equal(1, result.Statistics.EndpointCount);
        Assert.Equal(2, result.Statistics.MethodCount);
        Assert.Equal(1, result.Statistics.TableCount);
        Assert.Equal(1, result.Statistics.HttpEdgeCount);
        Assert.Equal(1, result.Statistics.CrossRepoLinkCount);
        Assert.Equal(1, result.Statistics.AmbiguousEdgeCount);
    }

    [Fact]
    public void Build_CreatesAdjacencyIndexes()
    {
        var builder = new GraphBuilder();
        builder.AddNode("a", NodeType.Method, "A");
        builder.AddNode("b", NodeType.Method, "B");
        builder.AddEdge("a", "b", EdgeType.Calls, "A calls B");

        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        var result = builder.Build(info, []);

        Assert.NotNull(result.NodesById);
        Assert.NotNull(result.OutgoingEdges);
        Assert.NotNull(result.IncomingEdges);
        Assert.True(result.OutgoingEdges.ContainsKey("a"));
        Assert.True(result.IncomingEdges.ContainsKey("b"));
    }

    [Fact]
    public void FindEndpointCandidates_MatchesByRouteHead()
    {
        var builder = new GraphBuilder();
        builder.AddNode("e1", NodeType.Endpoint, "GET /api/orders",
            metadata: new Dictionary<string, string?> { ["route"] = "/api/orders" });
        builder.AddNode("e2", NodeType.Endpoint, "GET /products",
            metadata: new Dictionary<string, string?> { ["route"] = "/products" });

        var apiCandidates = builder.FindEndpointCandidates("/api/orders/123");
        var productCandidates = builder.FindEndpointCandidates("/products/42");

        Assert.Single(apiCandidates);
        Assert.Equal("e1", apiCandidates[0].Id);
        Assert.Single(productCandidates);
        Assert.Equal("e2", productCandidates[0].Id);
    }

    [Fact]
    public void MergeMetadata_LastWriteWins()
    {
        var builder = new GraphBuilder();
        builder.AddNode("test:1", NodeType.Method, "Foo",
            metadata: new Dictionary<string, string?> { ["key1"] = "val1", ["key2"] = "original" });
        var merged = builder.AddNode("test:1", NodeType.Method, "Foo",
            metadata: new Dictionary<string, string?> { ["key2"] = "updated", ["key3"] = "val3" });

        Assert.Equal("val1", merged.Metadata["key1"]);
        Assert.Equal("original", merged.Metadata["key2"]); // first non-empty wins
        Assert.Equal("val3", merged.Metadata["key3"]);
    }
}
