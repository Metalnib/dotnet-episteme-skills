using System.Collections.Immutable;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

public sealed class GraphQuery
{
    private readonly ScanResult _graph;

    public GraphQuery(ScanResult graph)
    {
        _graph = graph.NodesById is not null ? graph : graph.WithAdjacency();
    }

    public GraphNode ResolveNode(string idOrName)
    {
        if (_graph.NodesById!.TryGetValue(idOrName, out var exact))
            return exact;

        var byName = _graph.Nodes.FirstOrDefault(n =>
            string.Equals(n.DisplayName, idOrName, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return byName;

        var fuzzy = _graph.Nodes.FirstOrDefault(n =>
            n.DisplayName.Contains(idOrName, StringComparison.OrdinalIgnoreCase));

        return fuzzy ?? throw new InvalidOperationException($"No node matched '{idOrName}'.");
    }

    public ImpactGraph FindImpact(
        string idOrName,
        bool upstream,
        int maxDepth = 6,
        bool exactOnly = false,
        bool includeAmbiguous = true)
    {
        var focus = ResolveNode(idOrName);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { focus.Id };
        var pending = new Queue<(string NodeId, int Depth)>();
        pending.Enqueue((focus.Id, 0));
        var edges = new List<GraphEdge>();

        var adjacency = upstream ? _graph.IncomingEdges! : _graph.OutgoingEdges!;
        var emptyEdges = ImmutableArray<GraphEdge>.Empty;

        while (pending.Count > 0)
        {
            var (currentId, depth) = pending.Dequeue();
            if (depth >= maxDepth)
                continue;

            var candidates = adjacency.GetValueOrDefault(currentId, emptyEdges);
            foreach (var edge in candidates)
            {
                if (!ShouldInclude(edge, exactOnly, includeAmbiguous))
                    continue;

                edges.Add(edge);
                var nextId = upstream ? edge.SourceId : edge.TargetId;
                if (visited.Add(nextId))
                    pending.Enqueue((nextId, depth + 1));
            }
        }

        var nodes = visited
            .Select(id => _graph.NodesById![id])
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new ImpactGraph(focus, nodes, [.. edges.DistinctBy(e => e.Id)]);
    }

    public PathSet FindPaths(
        string fromIdOrName,
        string toIdOrName,
        int maxDepth = 8,
        bool exactOnly = false,
        bool includeAmbiguous = false)
    {
        var from = ResolveNode(fromIdOrName);
        var to = ResolveNode(toIdOrName);
        var results = new List<GraphPath>();
        var emptyEdges = ImmutableArray<GraphEdge>.Empty;

        var nodePath = new List<GraphNode> { from };
        var edgePath = new List<GraphEdge>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { from.Id };

        Search(from.Id, maxDepth);
        return new PathSet(from, to, [.. results]);

        void Search(string currentId, int remaining)
        {
            if (remaining < 0)
                return;

            if (string.Equals(currentId, to.Id, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new GraphPath([.. nodePath], [.. edgePath]));
                return;
            }

            foreach (var edge in _graph.OutgoingEdges!.GetValueOrDefault(currentId, emptyEdges))
            {
                if (!ShouldInclude(edge, exactOnly, includeAmbiguous))
                    continue;
                if (!visited.Add(edge.TargetId))
                    continue;

                edgePath.Add(edge);
                nodePath.Add(_graph.NodesById![edge.TargetId]);
                Search(edge.TargetId, remaining - 1);
                edgePath.RemoveAt(edgePath.Count - 1);
                nodePath.RemoveAt(nodePath.Count - 1);
                visited.Remove(edge.TargetId);
            }
        }
    }

    public AmbiguityReport GetAmbiguityReport()
    {
        var unresolvedEdges = _graph.Edges
            .Where(e => e.Certainty == Certainty.Unresolved)
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var ambiguousEdges = _graph.Edges
            .Where(e => e.Certainty == Certainty.Ambiguous)
            .OrderBy(e => e.Type.ToString())
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        return new AmbiguityReport(unresolvedEdges, ambiguousEdges, _graph.Unresolved);
    }

    private static bool ShouldInclude(GraphEdge edge, bool exactOnly, bool includeAmbiguous)
    {
        if (exactOnly && edge.Certainty != Certainty.Exact)
            return false;
        if (!includeAmbiguous && edge.Certainty is Certainty.Ambiguous or Certainty.Unresolved)
            return false;
        return true;
    }
}
