using System.Collections.Immutable;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

public static class GraphDiffer
{
    public static GraphDiff Compare(ScanResult before, ScanResult after)
    {
        var beforeNodes = before.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var afterNodes = after.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var beforeEdges = before.Edges.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var afterEdges = after.Edges.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);

        var addedNodes = after.Nodes.Where(n => !beforeNodes.ContainsKey(n.Id)).ToImmutableArray();
        var removedNodes = before.Nodes.Where(n => !afterNodes.ContainsKey(n.Id)).ToImmutableArray();

        var changedNodes = ImmutableArray.CreateBuilder<NodeChange>();
        foreach (var afterNode in after.Nodes)
        {
            if (!beforeNodes.TryGetValue(afterNode.Id, out var beforeNode))
                continue;

            var changes = DetectNodeChanges(beforeNode, afterNode);
            if (changes.Length > 0)
                changedNodes.Add(new NodeChange(beforeNode, afterNode, changes));
        }

        var addedEdges = after.Edges.Where(e => !beforeEdges.ContainsKey(e.Id)).ToImmutableArray();
        var removedEdges = before.Edges.Where(e => !afterEdges.ContainsKey(e.Id)).ToImmutableArray();

        return new GraphDiff(
            addedNodes, removedNodes, changedNodes.ToImmutable(),
            addedEdges, removedEdges,
            before.Statistics, after.Statistics);
    }

    private static ImmutableArray<string> DetectNodeChanges(GraphNode before, GraphNode after)
    {
        var changes = ImmutableArray.CreateBuilder<string>();

        if (before.Type != after.Type)
            changes.Add($"type: {before.Type} -> {after.Type}");
        if (!string.Equals(before.DisplayName, after.DisplayName, StringComparison.Ordinal))
            changes.Add($"displayName: {before.DisplayName} -> {after.DisplayName}");
        if (before.Certainty != after.Certainty)
            changes.Add($"certainty: {before.Certainty} -> {after.Certainty}");
        if (!string.Equals(before.RepositoryName, after.RepositoryName, StringComparison.OrdinalIgnoreCase))
            changes.Add($"repository: {before.RepositoryName} -> {after.RepositoryName}");
        if (!string.Equals(before.ProjectName, after.ProjectName, StringComparison.OrdinalIgnoreCase))
            changes.Add($"project: {before.ProjectName} -> {after.ProjectName}");

        return changes.ToImmutable();
    }
}

public sealed record GraphDiff(
    ImmutableArray<GraphNode> AddedNodes,
    ImmutableArray<GraphNode> RemovedNodes,
    ImmutableArray<NodeChange> ChangedNodes,
    ImmutableArray<GraphEdge> AddedEdges,
    ImmutableArray<GraphEdge> RemovedEdges,
    ScanStatistics BeforeStatistics,
    ScanStatistics AfterStatistics);

public sealed record NodeChange(
    GraphNode Before,
    GraphNode After,
    ImmutableArray<string> Changes);
