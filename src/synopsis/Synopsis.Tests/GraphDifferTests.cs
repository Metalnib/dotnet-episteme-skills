using System.Collections.Immutable;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class GraphDifferTests
{
    private static ScanResult MakeGraph(params (string Id, NodeType Type, string Name)[] nodes)
    {
        var builder = new GraphBuilder();
        foreach (var (id, type, name) in nodes)
            builder.AddNode(id, type, name);

        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }

    [Fact]
    public void Compare_DetectsAddedNodes()
    {
        var before = MakeGraph(("a", NodeType.Method, "A"));
        var after = MakeGraph(("a", NodeType.Method, "A"), ("b", NodeType.Method, "B"));

        var diff = GraphDiffer.Compare(before, after);

        Assert.Single(diff.AddedNodes);
        Assert.Equal("b", diff.AddedNodes[0].Id);
        Assert.Empty(diff.RemovedNodes);
    }

    [Fact]
    public void Compare_DetectsRemovedNodes()
    {
        var before = MakeGraph(("a", NodeType.Method, "A"), ("b", NodeType.Method, "B"));
        var after = MakeGraph(("a", NodeType.Method, "A"));

        var diff = GraphDiffer.Compare(before, after);

        Assert.Empty(diff.AddedNodes);
        Assert.Single(diff.RemovedNodes);
        Assert.Equal("b", diff.RemovedNodes[0].Id);
    }

    [Fact]
    public void Compare_DetectsChangedNodes()
    {
        var builder1 = new GraphBuilder();
        builder1.AddNode("a", NodeType.Method, "A", certainty: Certainty.Ambiguous);
        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        var before = builder1.Build(info, []);

        var builder2 = new GraphBuilder();
        builder2.AddNode("a", NodeType.Method, "A", certainty: Certainty.Exact);
        var after = builder2.Build(info, []);

        var diff = GraphDiffer.Compare(before, after);

        Assert.Single(diff.ChangedNodes);
        Assert.Contains("certainty", diff.ChangedNodes[0].Changes[0]);
    }

    [Fact]
    public void Compare_IdenticalGraphs_NoChanges()
    {
        var graph = MakeGraph(("a", NodeType.Method, "A"), ("b", NodeType.Method, "B"));

        var diff = GraphDiffer.Compare(graph, graph);

        Assert.Empty(diff.AddedNodes);
        Assert.Empty(diff.RemovedNodes);
        Assert.Empty(diff.ChangedNodes);
        Assert.Empty(diff.AddedEdges);
        Assert.Empty(diff.RemovedEdges);
    }
}
