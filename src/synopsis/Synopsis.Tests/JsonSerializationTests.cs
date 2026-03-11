using System.Text.Json;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Output;

namespace Synopsis.Tests;

public sealed class JsonSerializationTests
{
    [Fact]
    public void ScanResult_RoundTrips_ThroughSourceGenJson()
    {
        var builder = new GraphBuilder();
        builder.AddNode("n1", NodeType.Method, "Foo.Bar",
            new SourceLocation("/src/foo.cs", 10, 5),
            "repo-a", "proj-a", Certainty.Exact,
            new Dictionary<string, string?> { ["key"] = "value" });
        builder.AddNode("n2", NodeType.Table, "Orders");
        builder.AddEdge("n1", "n2", EdgeType.DependsOn, "Foo touches Orders");

        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string> { ["scanner"] = "test" });
        var original = builder.Build(info, [new ScanWarning("test", "warning msg")]);

        var json = JsonSerializer.Serialize(original, SynopsisJsonContext.Default.ScanResult);
        var deserialized = JsonSerializer.Deserialize(json, SynopsisJsonContext.Default.ScanResult);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Nodes.Length, deserialized.Nodes.Length);
        Assert.Equal(original.Edges.Length, deserialized.Edges.Length);
        Assert.Equal(original.Statistics.MethodCount, deserialized.Statistics.MethodCount);
        Assert.Equal(original.Statistics.TableCount, deserialized.Statistics.TableCount);
        Assert.Single(deserialized.Warnings);
    }

    [Fact]
    public void ScanResult_AdjacencyFields_NotSerialized()
    {
        var builder = new GraphBuilder();
        builder.AddNode("a", NodeType.Method, "A");
        builder.AddNode("b", NodeType.Method, "B");
        builder.AddEdge("a", "b", EdgeType.Calls, "A calls B");

        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        var original = builder.Build(info, []);

        // Original has adjacency
        Assert.NotNull(original.NodesById);
        Assert.NotNull(original.OutgoingEdges);

        var json = JsonSerializer.Serialize(original, SynopsisJsonContext.Default.ScanResult);

        // JSON should NOT contain adjacency fields
        Assert.DoesNotContain("nodesById", json);
        Assert.DoesNotContain("outgoingEdges", json);
        Assert.DoesNotContain("incomingEdges", json);

        // Deserialized should have null adjacency
        var deserialized = JsonSerializer.Deserialize(json, SynopsisJsonContext.Default.ScanResult);
        Assert.NotNull(deserialized);
        Assert.Null(deserialized.NodesById);

        // WithAdjacency rebuilds it
        var rehydrated = deserialized.WithAdjacency();
        Assert.NotNull(rehydrated.NodesById);
        Assert.True(rehydrated.OutgoingEdges!.ContainsKey("a"));
    }

    [Fact]
    public void NodeId_From_ProducesStableIds()
    {
        var id1 = NodeId.From("test", "a", "b", "c");
        var id2 = NodeId.From("test", "a", "b", "c");
        var id3 = NodeId.From("test", "a", "b", "d");

        Assert.Equal(id1, id2);
        Assert.NotEqual(id1, id3);
        Assert.StartsWith("test:", id1);
    }
}
