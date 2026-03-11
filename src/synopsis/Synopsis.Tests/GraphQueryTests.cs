using System.Collections.Immutable;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class GraphQueryTests
{
    private static ScanResult BuildTestGraph()
    {
        var builder = new GraphBuilder();
        builder.AddNode("a", NodeType.Controller, "ControllerA");
        builder.AddNode("b", NodeType.Method, "ServiceB.DoWork");
        builder.AddNode("c", NodeType.Method, "RepoC.Load");
        builder.AddNode("d", NodeType.Table, "Orders");
        builder.AddNode("e", NodeType.Method, "Unrelated.Stuff");

        builder.AddEdge("a", "b", EdgeType.Calls, "A calls B");
        builder.AddEdge("b", "c", EdgeType.Calls, "B calls C");
        builder.AddEdge("c", "d", EdgeType.DependsOn, "C touches Orders");
        builder.AddEdge("a", "e", EdgeType.Ambiguous, "ambiguous link", certainty: Certainty.Ambiguous);

        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }

    [Fact]
    public void ResolveNode_ById()
    {
        var query = new GraphQuery(BuildTestGraph());
        var node = query.ResolveNode("a");
        Assert.Equal("ControllerA", node.DisplayName);
    }

    [Fact]
    public void ResolveNode_ByDisplayName()
    {
        var query = new GraphQuery(BuildTestGraph());
        var node = query.ResolveNode("ServiceB.DoWork");
        Assert.Equal("b", node.Id);
    }

    [Fact]
    public void ResolveNode_ByFuzzyMatch()
    {
        var query = new GraphQuery(BuildTestGraph());
        var node = query.ResolveNode("ServiceB");
        Assert.Equal("b", node.Id);
    }

    [Fact]
    public void ResolveNode_ThrowsOnNoMatch()
    {
        var query = new GraphQuery(BuildTestGraph());
        Assert.Throws<InvalidOperationException>(() => query.ResolveNode("nonexistent"));
    }

    [Fact]
    public void FindImpact_Downstream_TraversesCallChain()
    {
        var query = new GraphQuery(BuildTestGraph());
        var impact = query.FindImpact("a", upstream: false, maxDepth: 10);

        Assert.Equal("a", impact.FocusNode.Id);
        Assert.Contains(impact.Nodes, n => n.Id == "b");
        Assert.Contains(impact.Nodes, n => n.Id == "c");
        Assert.Contains(impact.Nodes, n => n.Id == "d");
    }

    [Fact]
    public void FindImpact_Upstream_WalksBackward()
    {
        var query = new GraphQuery(BuildTestGraph());
        var impact = query.FindImpact("d", upstream: true, maxDepth: 10);

        Assert.Contains(impact.Nodes, n => n.Id == "c");
        Assert.Contains(impact.Nodes, n => n.Id == "b");
        Assert.Contains(impact.Nodes, n => n.Id == "a");
    }

    [Fact]
    public void FindImpact_ExcludesAmbiguous_WhenRequested()
    {
        var query = new GraphQuery(BuildTestGraph());
        var impact = query.FindImpact("a", upstream: false, includeAmbiguous: false);

        Assert.DoesNotContain(impact.Nodes, n => n.Id == "e");
    }

    [Fact]
    public void FindImpact_RespectsMaxDepth()
    {
        var query = new GraphQuery(BuildTestGraph());
        var impact = query.FindImpact("a", upstream: false, maxDepth: 1);

        Assert.Contains(impact.Nodes, n => n.Id == "b");
        Assert.DoesNotContain(impact.Nodes, n => n.Id == "d"); // depth 3, beyond limit
    }

    [Fact]
    public void FindPaths_FindsDirectPath()
    {
        var query = new GraphQuery(BuildTestGraph());
        var result = query.FindPaths("a", "d");

        Assert.Single(result.Paths);
        Assert.Equal(4, result.Paths[0].Nodes.Length); // a -> b -> c -> d
    }

    [Fact]
    public void FindPaths_ReturnsEmpty_WhenNoPath()
    {
        var query = new GraphQuery(BuildTestGraph());
        var result = query.FindPaths("d", "a"); // no reverse path

        Assert.Empty(result.Paths);
    }

    [Fact]
    public void GetAmbiguityReport_FindsAmbiguousEdges()
    {
        var query = new GraphQuery(BuildTestGraph());
        var report = query.GetAmbiguityReport();

        Assert.Single(report.AmbiguousEdges);
        Assert.Equal(EdgeType.Ambiguous, report.AmbiguousEdges[0].Type);
    }
}
