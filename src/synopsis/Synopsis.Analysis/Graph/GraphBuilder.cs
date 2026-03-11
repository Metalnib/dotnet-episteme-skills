using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

public sealed class GraphBuilder
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GraphEdge> _edges = new(StringComparer.OrdinalIgnoreCase);

    // Endpoint index for fast HTTP resolution
    private readonly Dictionary<string, List<GraphNode>> _endpointsByRouteHead = new(StringComparer.OrdinalIgnoreCase);

    // Single-pass statistics counters
    private int _repositoryCount, _solutionCount, _projectCount, _endpointCount;
    private int _methodCount, _httpEdgeCount, _tableCount, _crossRepoCount, _ambiguousCount;

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;

    public IReadOnlyCollection<GraphEdge> Edges => _edges.Values;

    public GraphNode AddNode(
        string id,
        NodeType type,
        string displayName,
        SourceLocation? location = null,
        string? repositoryName = null,
        string? projectName = null,
        Certainty certainty = Certainty.Exact,
        IReadOnlyDictionary<string, string?>? metadata = null)
    {
        var candidate = new GraphNode(id, type, displayName, location, repositoryName, projectName,
            certainty, metadata ?? EmptyMetadata.Instance);

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_nodes, id, out bool exists);
        if (exists)
        {
            var existing = slot!;
            slot = existing with
            {
                Type = PickMoreSpecificType(existing.Type, candidate.Type),
                DisplayName = string.IsNullOrWhiteSpace(existing.DisplayName) ? candidate.DisplayName : existing.DisplayName,
                Location = existing.Location ?? candidate.Location,
                RepositoryName = existing.RepositoryName ?? candidate.RepositoryName,
                ProjectName = existing.ProjectName ?? candidate.ProjectName,
                Certainty = existing.Certainty >= candidate.Certainty ? existing.Certainty : candidate.Certainty,
                Metadata = MergeMetadata(existing.Metadata, candidate.Metadata)
            };

            // Update index if type changed to Endpoint
            if (existing.Type != NodeType.Endpoint && slot.Type == NodeType.Endpoint)
                IndexEndpoint(slot);

            return slot;
        }

        slot = candidate;
        IncrementNodeCounter(type);
        if (type == NodeType.Endpoint)
            IndexEndpoint(candidate);

        return candidate;
    }

    public GraphEdge AddEdge(
        string sourceId,
        string targetId,
        EdgeType type,
        string displayName,
        SourceLocation? location = null,
        string? repositoryName = null,
        string? projectName = null,
        Certainty certainty = Certainty.Exact,
        IReadOnlyDictionary<string, string?>? metadata = null)
    {
        var edgeId = NodeId.From("edge", sourceId, targetId, type.ToString(), displayName);
        var candidate = new GraphEdge(edgeId, sourceId, targetId, type, displayName, location,
            repositoryName, projectName, certainty, metadata ?? EmptyMetadata.Instance);

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_edges, edgeId, out bool exists);
        if (exists)
        {
            var existing = slot!;
            slot = existing with
            {
                Location = existing.Location ?? candidate.Location,
                RepositoryName = existing.RepositoryName ?? candidate.RepositoryName,
                ProjectName = existing.ProjectName ?? candidate.ProjectName,
                Certainty = existing.Certainty >= candidate.Certainty ? existing.Certainty : candidate.Certainty,
                Metadata = MergeMetadata(existing.Metadata, candidate.Metadata)
            };
            return slot;
        }

        slot = candidate;
        IncrementEdgeCounter(type, certainty);
        return candidate;
    }

    /// <summary>
    /// Merges all nodes and edges from another builder into this one.
    /// Used to combine per-project builders after parallel analysis.
    /// </summary>
    public void Merge(GraphBuilder other)
    {
        foreach (var node in other._nodes.Values)
            AddNode(node.Id, node.Type, node.DisplayName, node.Location,
                node.RepositoryName, node.ProjectName, node.Certainty, node.Metadata);

        foreach (var edge in other._edges.Values)
            AddEdge(edge.SourceId, edge.TargetId, edge.Type, edge.DisplayName,
                edge.Location, edge.RepositoryName, edge.ProjectName, edge.Certainty, edge.Metadata);
    }

    public IReadOnlyList<GraphNode> FindEndpointCandidates(string path)
    {
        var head = ExtractRouteHead(path);
        if (string.IsNullOrEmpty(head))
            return [];

        return _endpointsByRouteHead.TryGetValue(head, out var list) ? list : [];
    }

    public ScanResult Build(ScanInfo scanInfo, IReadOnlyList<ScanWarning> warnings)
    {
        var nodes = _nodes.Values
            .OrderBy(n => n.Type)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var edges = _edges.Values
            .OrderBy(e => e.Type)
            .ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var stats = new ScanStatistics(
            _repositoryCount, _solutionCount, _projectCount, _endpointCount,
            _methodCount, _httpEdgeCount, _tableCount, _crossRepoCount, _ambiguousCount);

        var unresolved = warnings
            .Where(w => w.Certainty is Certainty.Unresolved or Certainty.Ambiguous)
            .ToImmutableArray();

        var result = new ScanResult
        {
            Nodes = nodes,
            Edges = edges,
            Metadata = scanInfo,
            Warnings = [.. warnings],
            Unresolved = unresolved,
            Statistics = stats
        };

        return result.WithAdjacency();
    }

    private void IndexEndpoint(GraphNode node)
    {
        var route = node.Metadata.GetValueOrDefault("route");
        var head = ExtractRouteHead(route);
        if (string.IsNullOrEmpty(head))
            return;

        ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(_endpointsByRouteHead, head, out bool exists);
        list ??= [];
        list.Add(node);
    }

    internal static string? ExtractRouteHead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.AsSpan().Trim('/');
        var slashIndex = segments.IndexOf('/');
        var head = slashIndex >= 0 ? segments[..slashIndex] : segments;
        return head.Length > 0 ? head.ToString() : null;
    }

    private void IncrementNodeCounter(NodeType type)
    {
        switch (type)
        {
            case NodeType.Repository: _repositoryCount++; break;
            case NodeType.Solution: _solutionCount++; break;
            case NodeType.Project: _projectCount++; break;
            case NodeType.Endpoint: _endpointCount++; break;
            case NodeType.Method: _methodCount++; break;
            case NodeType.Table: _tableCount++; break;
        }
    }

    private void IncrementEdgeCounter(EdgeType type, Certainty certainty)
    {
        if (type is EdgeType.CallsHttp or EdgeType.UsesHttpClient)
            _httpEdgeCount++;
        if (type == EdgeType.CrossesRepoBoundary)
            _crossRepoCount++;
        if (certainty is Certainty.Ambiguous or Certainty.Unresolved)
            _ambiguousCount++;
    }

    internal static NodeType PickMoreSpecificType(NodeType left, NodeType right) =>
        TypePriority(left) >= TypePriority(right) ? left : right;

    private static int TypePriority(NodeType type) => type switch
    {
        NodeType.Endpoint => 100,
        NodeType.Controller => 90,
        NodeType.DbContext => 85,
        NodeType.Entity => 84,
        NodeType.Table => 83,
        NodeType.ExternalEndpoint => 82,
        NodeType.ExternalService => 81,
        NodeType.HttpClient => 80,
        NodeType.Method => 70,
        NodeType.Interface => 60,
        NodeType.Service => 55,
        NodeType.Implementation => 50,
        NodeType.ConfigurationKey => 45,
        NodeType.Project => 40,
        NodeType.Solution => 30,
        NodeType.Repository => 20,
        NodeType.Workspace => 10,
        _ => 0
    };

    private static IReadOnlyDictionary<string, string?> MergeMetadata(
        IReadOnlyDictionary<string, string?> left,
        IReadOnlyDictionary<string, string?> right)
    {
        if (left.Count == 0) return right;
        if (right.Count == 0) return left;

        var merged = new Dictionary<string, string?>(left, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in right)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!merged.TryGetValue(key, out var existing) || string.IsNullOrWhiteSpace(existing))
                merged[key] = value;
        }

        return merged;
    }
}

internal static class EmptyMetadata
{
    internal static readonly IReadOnlyDictionary<string, string?> Instance =
        new Dictionary<string, string?>(0).AsReadOnly();
}
